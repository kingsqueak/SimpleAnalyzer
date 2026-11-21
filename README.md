# SimpleAnalyzer

A standalone SSB and AM signal analyser for a TCI radio: an envelope scope (modulation monitor),
the spectrum around the dial, and readings with plain advice (peaks and flat-topping, density,
bandwidth, tone, splatter; for AM the modulation depth up and down, loudness and carrier). It is the
Signal Analyser from SimpleHPSDR, taken out as its own app.

## How it gets the signal
It is a TCI client written to the TCI spec (ExpertSDR3, protocol 2.x), so it should work with any server
that implements it properly. Tested with ExpertSDR3 and a SunSDR2 DX, and with the SimpleHPSDR engine.

It **only listens**. It receives RX1's IQ (`iq_start:0`) and follows the frequency, mode and filters the
server announces (`vfo`, `dds`, `modulation`, `rx_filter_band`, `trx`, `tune`, `tx_frequency`). What it
sends:

- `iq_start:0` / `iq_stop:0` for its own IQ stream.
- `iq_samplerate` with no value, to learn the rate. **The one setting it changes:** the IQ is centred
  on RX1's DDS frequency, so when the dial is too near the edge of the span it asks for the lowest
  standard rate (48, 96, 192 or 384 kHz) that takes it in. It never lowers the rate while connected and
  puts the original back when it disconnects. Other IQ clients of the same server see the change meanwhile.
- The read-only queries `trx:0`, `trx:1` and `tune:0`. A server should announce these anyway; asking as
  well covers servers that only answer when asked (the SimpleHPSDR engine).
- `tx_filter_band_ex` with no value: an extension the SimpleHPSDR engine answers with the TX filter.
  Without it, transmit is judged against RX1's filter. `tx_alc` is also used if a server sends it.

The client refuses any command, or any extra argument, that could tune, change another setting or key the
radio (`TciClient.IsAllowed`).

**IQ orientation:** per the spec, I + jQ with a signal above the DDS at a positive frequency, as ExpertSDR3
sends it. If signals show on the wrong side of the dial (an LSB station's audio to the right of the dial
line), the server sends its IQ mirrored: tick **Swap I/Q**. The SimpleHPSDR engine needs it at the time
of writing.

- **Receive:** analyses the signal on RX1's dial.
- **Transmit:** while keyed it analyses your own signal as RX1 hears it. That needs RX1 to keep running
  while you transmit (it does on the SimpleHPSDR engine with a two-receiver radio; other radios may mute
  or stop it, and then only receive works) and the transmit frequency inside RX1's IQ span.
- TCI carries no mic level here, so mic advice is left out.

## Build and run
There are no prebuilt releases; build it from source. It runs on macOS, Linux and Windows.

**You need** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or a
later 10.0 feature band; `global.json` pins it). Check with `dotnet --list-sdks`. If a newer major
version (.NET 11) is first on your PATH and 10 is installed elsewhere, put the .NET 10 one first,
e.g. on macOS `export PATH=/usr/local/share/dotnet:$PATH`. On Linux, Avalonia also needs the usual
desktop libraries (`libx11-6`, `libice6`, `libsm6`, `libfontconfig1`), present on most desktops.

**Run from the source:**

    git clone https://github.com/kingsqueak/SimpleAnalyzer.git
    cd SimpleAnalyzer
    dotnet run --project src/SimpleAnalyzer -c Release -- --host 127.0.0.1 --port 40001

**Make a standalone copy** that runs without .NET installed (pick your platform's runtime:
`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`):

    dotnet publish src/SimpleAnalyzer -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64

Then run `publish/osx-arm64/SimpleAnalyzer` (`SimpleAnalyzer.exe` on Windows); keep the files in
that folder together, as the graphics libraries sit beside the program. On macOS this is a
plain executable, not a signed `.app`: start it from Terminal.

**Mac app and DMG:** `scripts/make-dmg.sh` (or `scripts/make-dmg.sh osx-x64` for an Intel Mac)
builds `artifacts/mac/SimpleAnalyzer.app` and `artifacts/SimpleAnalyzer-<version>-<arch>.dmg`. The app
is ad-hoc signed, not notarized: the first time, right-click it and choose Open (or allow it in System
Settings > Privacy & Security).

**Server and port:** `--host` and `--port` set the TCI server for that run; they can also be set in
the window, and are remembered (`~/Library/Application Support/SimpleAnalyzer/settings.json` on macOS,
`~/.local/share/SimpleAnalyzer/` on Linux, `%LOCALAPPDATA%\SimpleAnalyzer\` on Windows). The default,
127.0.0.1:40001, is ExpertSDR3's default port; Thetis uses 50001. Turn the TCI server on in your SDR
program first.

## Benchmark
`tools/signal-bench` streams model AM signals of known depth through the analysis code and checks
the readings (`cd tools/signal-bench && dotnet run -c Release`); see its README.

## Licence
GPL-3.0, as SimpleHPSDR, which the analysis code comes from.
