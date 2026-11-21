using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleAnalyzer.Engine;

/// <summary>What the radio last told us over TCI. Receivers are numbered from 0 as in TCI.</summary>
public sealed record RadioState(
    bool Connected, string Device, string Error,
    long Rx1Hz, string Rx1Mode, int Rx1FilterLow, int Rx1FilterHigh,
    long Rx2Hz, string Rx2Mode,
    long TxHz, bool Transmitting, int TxRx, bool Tune,
    int? TxFilterLow, int? TxFilterHigh, double AlcDb,
    long DdsHz, int IqRate, DateTime LastIq, DateTime ReadyAt);

/// <summary>
/// A listen-only TCI client: it receives RX1's IQ and follows the radio's frequency, mode,
/// filters and transmit state. The one way out refuses every command that isn't on the
/// short list below, and the state questions are sent only in their query form (no value),
/// so nothing here can tune, change a setting or transmit. The one setting it does touch is
/// the server's IQ rate, raised only when RX1's dial is outside the IQ span and put back on
/// disconnect.
///
/// IQ is delivered as the TCI spec has it (ExpertSDR3): I + jQ centred on the DDS frequency,
/// a signal above the DDS at a positive frequency. <see cref="SwapIq"/> corrects a server
/// that sends it mirrored.
/// </summary>
public sealed class TciClient : IDisposable
{
    // Commands that may be sent, with how many arguments each may carry. trx and tune take
    // the receiver only, which makes them questions: with a second argument they would key.
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iq_start"] = 1, ["iq_stop"] = 1, ["iq_samplerate"] = 1,
        ["trx"] = 1, ["tune"] = 1, ["tx_filter_band_ex"] = 0,
    };

    public delegate void IqHandler(ReadOnlySpan<float> iq, int rate);

    private const int HeaderBytes = 64;
    private const int IqStream = 0;
    private const int Int16Format = 0, Int24Format = 1, Int32Format = 2, Float32Format = 3;
    private const int Rx = 0;   // the IQ is always RX1's
    private const int BatchesPerSecond = 25;
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    // The engine answers trx and tune when asked but doesn't announce a key-up, so ask often.
    private static readonly TimeSpan TxPoll = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FilterPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AlcFresh = TimeSpan.FromSeconds(1);
    /// <summary>The IQ rates TCI offers, lowest first.</summary>
    private static readonly int[] IqRates = [48000, 96000, 192000, 384000];
    /// <summary>Room the analyser needs between the dial and the edge of the IQ span.</summary>
    public const int SpanMarginHz = 12000;

    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _lock = new();
    private ClientWebSocket? _ws;
    /// <summary>IQ waiting to go to the analyser. TCI packets are tiny (about 1600 a second
    /// at 384 kHz); handing them over 25 times a second saves most of the thread wake-ups.</summary>
    private float[] _floats = new float[1 << 16];
    private int _pending;
    private int _pendingRate;

    // Radio state, under _lock.
    private bool _connected;
    private string _device = "", _error = "";
    private readonly long[] _vfo = new long[2];
    private readonly string[] _mode = ["", ""];
    private readonly int[] _filterLow = new int[2], _filterHigh = new int[2];
    private long _txHz, _dds;
    private readonly bool[] _trx = new bool[2];
    private bool _tune;
    private int _iqRate, _serverRate, _askedRate;
    private int? _txLow, _txHigh;
    private double _alcDb = double.NaN;
    private DateTime _alcAt, _lastIq, _readyAt;

    public event IqHandler? IqReceived;

    /// <summary>Swap I and Q, for a server whose IQ is mirrored (signals on the wrong side of the dial).</summary>
    public volatile bool SwapIq;

    public string Endpoint => $"{_host}:{_port}";

    public TciClient(string host, int port)
    {
        (_host, _port) = (host, port);
        _loop = Task.Run(RunAsync);
    }

    public RadioState State
    {
        get
        {
            lock (_lock)
            {
                double alc = DateTime.UtcNow - _alcAt < AlcFresh ? _alcDb : double.NaN;
                int txRx = _trx[0] ? 0 : _trx[1] ? 1 : -1;
                return new(_connected, _device, _error,
                    _vfo[0], _mode[0], _filterLow[0], _filterHigh[0], _vfo[1], _mode[1],
                    _txHz, txRx >= 0, Math.Max(txRx, 0), _tune, _txLow, _txHigh, alc, _dds, _iqRate, _lastIq, _readyAt);
            }
        }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/"), _stop.Token);
                _ws = ws;
                lock (_lock) _error = "";
                using var polling = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var poll = PollAsync(polling.Token);
                try { await ReceiveAsync(ws); }
                finally
                {
                    polling.Cancel();
                    try { await poll; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is WebSocketException or IOException or InvalidOperationException or InvalidDataException)
            {
                lock (_lock) _error = e.InnerException?.Message ?? e.Message;
            }
            _ws = null;
            lock (_lock)
            {
                _connected = false;
                _trx[0] = _trx[1] = _tune = false;
                _askedRate = _serverRate = _iqRate = 0;
            }
            try { await Task.Delay(2000, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Ask, a few times a second, whether the radio is keyed, and once a second
    /// for the TX filter in force (the SimpleHPSDR engine answers tx_filter_band_ex).</summary>
    private async Task PollAsync(CancellationToken stop)
    {
        var filterAsked = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TxPoll, stop);
            bool ready;
            lock (_lock) ready = _connected;
            if (!ready) continue;
            await EnsureIqRateAsync();
            // The spec has the server announce these; asking as well costs nothing, and some servers only answer.
            await SendAsync("trx:0");
            await SendAsync("trx:1");
            await SendAsync($"tune:{Rx}");
            if (DateTime.UtcNow - filterAsked >= FilterPoll)
            {
                filterAsked = DateTime.UtcNow;
                await SendAsync("tx_filter_band_ex");
            }
        }
    }

    /// <summary>The IQ is RX1's DDS span: if the dial (or, keyed, the transmit frequency) is too near
    /// its edge for the analyser, ask for the lowest standard rate that takes it in. Never lowered,
    /// so a dial moving about doesn't keep changing a setting other clients may share.</summary>
    private async Task EnsureIqRateAsync()
    {
        int want;
        lock (_lock)
        {
            if (_dds <= 0 || _vfo[0] <= 0 || _iqRate <= 0) return;
            long need = Math.Abs(_vfo[0] - _dds);
            if ((_trx[0] || _trx[1]) && _txHz > 0 && Math.Abs(_txHz - _dds) < 400_000) need = Math.Max(need, Math.Abs(_txHz - _dds));
            if (need <= _iqRate / 2 - SpanMarginHz) return;
            want = Array.Find(IqRates, r => need <= r / 2 - SpanMarginHz);
            if (want == 0) want = IqRates[^1];          // as wide as TCI goes; the analyser says if it's still outside
            if (want <= _iqRate || want == _askedRate) return;
            _askedRate = want;
        }
        await SendAsync($"iq_samplerate:{want}");
    }

    private async Task ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1 << 17];
        var used = 0;
        while (ws.State == WebSocketState.Open)
        {
            if (used == buffer.Length)
            {
                // A message that never ends is not TCI: drop the peer rather
                // than grow without limit (IQ frames are well under this).
                if (buffer.Length >= MaxMessageBytes) throw new InvalidDataException("TCI message too large.");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            var r = await ws.ReceiveAsync(buffer.AsMemory(used), _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) return;
            used += r.Count;
            if (!r.EndOfMessage) continue;
            if (r.MessageType == WebSocketMessageType.Text)
                await OnTextAsync(Encoding.UTF8.GetString(buffer, 0, used));
            else
                OnBinary(buffer.AsSpan(0, used));
            used = 0;
        }
    }

    private async Task OnTextAsync(string text)
    {
        foreach (var command in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = command.IndexOf(':');
            var verb = (colon < 0 ? command : command[..colon]).ToLowerInvariant();
            var args = colon < 0 ? [] : command[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries);
            bool ready = false;
            lock (_lock)
            {
                switch (verb)
                {
                    case "device" when args.Length > 0:
                        _device = args[0];
                        break;
                    case "ready":
                        _connected = ready = true;
                        _readyAt = DateTime.UtcNow;
                        break;
                    case "vfo" when args.Length >= 3 && Receiver(args[0]) is int rx && Int(args[1]) == 0:
                        _vfo[rx] = Long(args[2]);
                        break;
                    case "modulation" when args.Length >= 2 && Receiver(args[0]) is int rx:
                        _mode[rx] = args[1].ToUpperInvariant();
                        break;
                    case "rx_filter_band" when args.Length >= 3 && Receiver(args[0]) is int rx:
                        (_filterLow[rx], _filterHigh[rx]) = (Int(args[1]), Int(args[2]));
                        break;
                    case "dds" when args.Length >= 2 && Int(args[0]) == Rx:
                        _dds = Long(args[1]);
                        break;
                    case "tx_frequency" when args.Length >= 1:
                        _txHz = Long(args[0]);
                        break;
                    case "trx" when args.Length >= 2 && Receiver(args[0]) is int rx:
                        _trx[rx] = Bool(args[1]);
                        break;
                    case "tune" when args.Length >= 2 && Int(args[0]) == Rx:
                        _tune = Bool(args[1]);
                        break;
                    case "tx_filter_band_ex" when args.Length >= 2:
                        (_txLow, _txHigh) = (Int(args[0]), Int(args[1]));
                        break;
                    case "iq_samplerate" when args.Length >= 1 && Int(args[0]) > 0:
                        _iqRate = Int(args[0]);
                        if (_serverRate == 0) _serverRate = _iqRate;   // what it was before we asked: put back on leaving
                        break;
                    case "tx_alc" when args.Length >= 1:
                        // ALC gain reduction, sent as tenths of a dB capped at 100.
                        _alcDb = Int(args[0]) / 10.0;
                        _alcAt = DateTime.UtcNow;
                        break;
                }
            }
            if (ready)
            {
                // Ask for the rate in force (a server may not have announced it), then start RX1's IQ.
                await SendAsync("iq_samplerate");
                await SendAsync($"iq_start:{Rx}");
            }
        }
    }

    private void OnBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= HeaderBytes) return;
        var receiver = BinaryPrimitives.ReadInt32LittleEndian(frame);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
        var format = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        var type = BinaryPrimitives.ReadInt32LittleEndian(frame[24..]);
        if (type != IqStream || receiver != Rx) return;
        var data = frame[HeaderBytes..];
        var count = format switch
        {
            Int16Format => data.Length / 2,
            Int24Format => data.Length / 3,
            Int32Format or Float32Format => data.Length / 4,
            _ => 0,
        };
        count &= ~1;
        if (count == 0) return;
        if (rate != _pendingRate)
        {
            _pending = 0;
            _pendingRate = rate;
        }
        var batch = Math.Max(rate / BatchesPerSecond * 2, count);
        if (_floats.Length < batch + count) Array.Resize(ref _floats, (batch + count) * 2);
        var dest = _floats.AsSpan(_pending, count);
        switch (format)
        {
            case Float32Format:
                MemoryMarshal.Cast<byte, float>(data)[..count].CopyTo(dest);
                break;
            case Int16Format:
                for (var i = 0; i < count; i++) dest[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
                break;
            case Int24Format:
                for (var i = 0; i < count; i++)
                    dest[i] = ((data[i * 3 + 2] << 24) | (data[i * 3 + 1] << 16) | (data[i * 3] << 8)) / 2147483648f;
                break;
            case Int32Format:
                for (var i = 0; i < count; i++) dest[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648f;
                break;
        }
        if (SwapIq)
            for (var i = 0; i < count; i += 2) (dest[i], dest[i + 1]) = (dest[i + 1], dest[i]);
        lock (_lock)
        {
            _lastIq = DateTime.UtcNow;
            if (_iqRate == 0) _iqRate = rate;           // a server that never said: the frames do
        }
        _pending += count;
        if (_pending < batch) return;
        IqReceived?.Invoke(_floats.AsSpan(0, _pending), rate);
        _pending = 0;
    }

    /// <summary>The only way out. Refuses any command not on the list, or with more arguments
    /// than the list allows (which is what would turn a question into a setting).</summary>
    internal static bool IsAllowed(string command)
    {
        var parts = command.Split(':', 2);
        int args = parts.Length < 2 || parts[1].Length == 0 ? 0 : parts[1].Split(',').Length;
        return Allowed.TryGetValue(parts[0], out var most) && args <= most;
    }

    private async Task SendAsync(string command)
    {
        if (!IsAllowed(command)) throw new InvalidOperationException($"TCI command not allowed: {command}");
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.SendAsync(Encoding.UTF8.GetBytes(command + ";"), WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        try
        {
            SendAsync($"iq_stop:{Rx}").Wait(500);
            int restore;
            lock (_lock) restore = _askedRate != 0 && _serverRate > 0 && _serverRate != _askedRate ? _serverRate : 0;
            if (restore > 0) SendAsync($"iq_samplerate:{restore}").Wait(500);
        }
        catch (AggregateException) { }
        _stop.Cancel();
        try { _ws?.Abort(); } catch (Exception) { }
        bool stopped;
        try { stopped = _loop.Wait(2000); }
        catch (AggregateException) { stopped = true; }
        if (!stopped) return;   // still unwinding: leave the rest to the garbage collector
        _stop.Dispose();
        _sendLock.Dispose();
    }

    private static int? Receiver(string s) => Int(s) is var rx and (0 or 1) ? rx : null;
    private static bool Bool(string s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
    private static long Long(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
