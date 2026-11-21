using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleAnalyzer.Engine;

namespace SimpleAnalyzer.ViewModels;

/// <summary>What the analyser should look at now: RX1 in receive, the transmit frequency while keyed.</summary>
public sealed record SignalTarget(bool Transmitting, long Hz, string Mode, int HighHz, string Slice,
    bool TestTone, double MicPeakDbfs, double AlcDb);

/// <summary>One reading in the analyser's list.</summary>
public sealed partial class FindingRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _advice = "";
    [ObservableProperty] private IBrush _brush = Brushes.Gray;
}

/// <summary>
/// The signal analyser: an envelope scope, the spectrum round the carrier, and readings
/// with advice, for SSB and AM. In receive it looks at the signal on RX1's dial; while
/// transmitting, at our own signal as RX1 hears it. Everything comes from the radio's TCI
/// server, and the client only listens, so the radio is left as it is.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#5A6270"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#5AA9FF"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5D76E"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly double[] Seconds = [0.05, 0.2, 1, 5];
    private const int Columns = 900;
    private const int FindingsEvery = 6;             // ticks: readings change a few times a second, not fifteen

    private readonly SignalAnalysis _analysis = new();
    private DispatcherTimer? _timer;
    private TciClient? _tci;
    private int _tick;
    private long _lastHz;
    private double _lastShift = double.NaN;

    [ObservableProperty] private SignalReport? _report;
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private IBrush _headlineBrush = Grey;
    [ObservableProperty] private int _timeBaseIndex = 2;
    [ObservableProperty] private bool _frozen;
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _portText;
    [ObservableProperty] private string _deviceText = "";
    [ObservableProperty] private bool _swapIq;
    // The analysis code takes IQ mirrored (conjugated), as SimpleHPSDR's engine sends it; TCI's is turned round here.
    private float[] _mirror = new float[1 << 16];

    public string[] TimeBases { get; } = ["50 ms", "200 ms", "1 s", "5 s"];
    public ObservableCollection<FindingRow> Findings { get; } = [];

    public MainViewModel(Settings settings)
    {
        _settings = settings;
        _host = settings.Host;
        _portText = settings.Port.ToString();
        _timeBaseIndex = Math.Clamp(settings.TimeBaseIndex, 0, Seconds.Length - 1);
        _swapIq = settings.SwapIq;
    }

    partial void OnSwapIqChanged(bool value)
    {
        _settings.SwapIq = value;
        _settings.Save();
        if (_tci is { } tci) tci.SwapIq = value;
        _analysis.Restart();
    }

    /// <summary>Called on the TCI thread with IQ the spec's way round.</summary>
    private void OnIq(ReadOnlySpan<float> iq, int rate)
    {
        if (_mirror.Length < iq.Length) _mirror = new float[iq.Length * 2];
        var m = _mirror.AsSpan(0, iq.Length);
        for (int i = 0; i < iq.Length; i += 2) (m[i], m[i + 1]) = (iq[i], -iq[i + 1]);
        _analysis.Push(m, rate);
    }

    private readonly Settings _settings;

    [RelayCommand] private void ToggleFrozen() => Frozen = !Frozen;

    partial void OnTimeBaseIndexChanged(int value)
    {
        _settings.TimeBaseIndex = value;
        _settings.Save();
    }

    /// <summary>Connect (again) to the TCI server in the host and port boxes.</summary>
    [RelayCommand]
    private void Connect()
    {
        string host = Host.Trim();
        if (host.Length == 0 || !int.TryParse(PortText.Trim(), out int port) || port is < 1 or > 65535)
        {
            Idle("Enter the TCI server's host and port (the SimpleHPSDR engine uses 40001).");
            return;
        }
        (_settings.Host, _settings.Port) = (host, port);
        _settings.Save();
        DropTci();
        var tci = new TciClient(host, port) { SwapIq = SwapIq };
        tci.IqReceived += OnIq;
        _tci = tci;
        Idle($"Connecting to the TCI server at {tci.Endpoint}…");
    }

    public void Start()
    {
        if (_timer is not null) return;
        Connect();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        DropTci();
    }

    private void DropTci()
    {
        if (_tci is not { } tci) return;
        _tci = null;
        _lastShift = double.NaN;
        Task.Run(tci.Dispose);      // it waits for the socket to close: not on the UI thread
    }

    private void Idle(string status)
    {
        Status = status;
        if (Frozen) return;
        Report = null;
        Findings.Clear();
        Headline = "";
    }

    /// <summary>RX1's dial in receive; while keyed, the transmit frequency, in the mode of the
    /// transceiver that is keyed, judged against the TX filter if the server reports one
    /// (tx_filter_band_ex is an extension), otherwise against RX1's filter.</summary>
    private static SignalTarget TargetOf(RadioState s)
    {
        int rxHigh = Math.Max(Math.Abs(s.Rx1FilterLow), Math.Abs(s.Rx1FilterHigh));
        if (!s.Transmitting)
            return new(false, s.Rx1Hz, s.Rx1Mode, rxHigh, "RX1", false, double.NaN, double.NaN);
        long hz = s.TxHz > 0 ? s.TxHz : s.Rx1Hz;
        bool rx2 = s.TxRx == 1;
        int high = s.TxFilterLow is int lo && s.TxFilterHigh is int hi && Math.Max(Math.Abs(lo), Math.Abs(hi)) > 0
            ? Math.Max(Math.Abs(lo), Math.Abs(hi))
            : rxHigh;
        // TCI carries no mic level: the analyser leaves it out of its advice.
        return new(true, hz, rx2 ? s.Rx2Mode : s.Rx1Mode, high, rx2 ? "RX2" : "RX1", s.Tune, double.NaN, s.AlcDb);
    }

    private void Tick()
    {
        if (_tci is not { } tci) return;
        var s = tci.State;
        DeviceText = s.Connected && s.Device.Length > 0 ? s.Device : "";
        if (!s.Connected)
        {
            SourceText = "";
            Idle(s.Error.Length > 0
                ? $"TCI at {tci.Endpoint}: {s.Error}. Is the TCI server on? Retrying…"
                : $"Connecting to the TCI server at {tci.Endpoint}…");
            return;
        }

        var t = TargetOf(s);
        bool am = t.Mode is "AM" or "SAM", ssb = t.Mode is "LSB" or "USB";
        // In receive the other station's audio may be wider than our filter: judge it against a normal channel.
        int high = t.Transmitting ? t.HighHz : Math.Max(t.HighHz, am ? 5000 : 3000);
        _analysis.Configure(new SignalSetup(ssb ? SignalKind.Ssb : am ? SignalKind.Am : SignalKind.Other,
            t.Mode == "LSB", high, t.Transmitting, t.TestTone, t.MicPeakDbfs, t.AlcDb, t.Transmitting ? 0 : t.HighHz));
        SourceText = t.Transmitting
            ? $"Transmitting  ·  {t.Hz / 1e6:0.000000} MHz {t.Mode}  ·  your signal as RX1 hears it"
            : $"Receiving on {t.Slice}  ·  {t.Hz / 1e6:0.000000} MHz {t.Mode}";

        int rate = _analysis.InputRate;
        if (s.DdsHz <= 0 || rate == 0 || DateTime.UtcNow - s.LastIq > TimeSpan.FromSeconds(2))
        {
            // Radios with one receiver give it to PureSignal while keyed: nothing to see then.
            Idle(t.Transmitting && rate != 0
                ? "The receiver isn't running while you transmit on this radio, so your own signal can't be seen."
                : "Waiting for the receiver's IQ…");
            return;
        }
        // The IQ is RX1's stream: the signal has to be inside it, with room for its sidebands.
        double shift = t.Hz - s.DdsHz;
        if (Math.Abs(shift) > rate / 2.0 - 12000)
        {
            Idle(t.Transmitting
                ? "The transmit frequency is outside RX1's IQ span: put RX1 on the band you transmit on to see your signal."
                : s.IqRate is > 0 and < 384000
                    ? "RX1's dial is near the edge of its IQ span: asking the server for a wider IQ rate…"
                    : "RX1's dial is too far from the centre of its IQ span (the DDS frequency) for the widest TCI IQ rate.");
            return;
        }
        if (shift != _lastShift)
        {
            _lastShift = shift;
            _analysis.ShiftHz = shift;
            if (Math.Abs(t.Hz - _lastHz) > 20) _analysis.Restart();
            _lastHz = t.Hz;
        }
        Status = "";
        if (Frozen) return;

        bool readings = _tick++ % FindingsEvery == 0;
        var report = _analysis.Snapshot(Seconds[Math.Clamp(TimeBaseIndex, 0, Seconds.Length - 1)], Columns, readings);
        Report = report;
        if (readings && report.Findings is { } findings) Show(findings, report);
    }

    private static IBrush BrushOf(FindingLevel level) => level switch
    {
        FindingLevel.Good => Green,
        FindingLevel.Info => Blue,
        FindingLevel.Caution => Amber,
        FindingLevel.Bad => Red,
        _ => Grey,
    };

    // Rows are changed in place, so the list doesn't flicker or lose its place.
    private void Show(IReadOnlyList<Finding> findings, SignalReport report)
    {
        Headline = report.Headline;
        HeadlineBrush = BrushOf(report.Overall);
        while (Findings.Count > findings.Count) Findings.RemoveAt(Findings.Count - 1);
        for (int i = 0; i < findings.Count; i++)
        {
            if (i == Findings.Count) Findings.Add(new FindingRow());
            var (row, f) = (Findings[i], findings[i]);
            (row.Name, row.Value, row.Advice, row.Brush) = (f.Name, f.Value, f.Advice, BrushOf(f.Level));
        }
    }
}
