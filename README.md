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
.NET 10. On the Mac Studio the .NET 10 SDK is in `/usr/local/share/dotnet` (Homebrew's `dotnet` is
.NET 11), so put it first:

    export PATH=/usr/local/share/dotnet:$PATH
    dotnet run --project src/SimpleAnalyzer -- --port 50001

`--host` and `--port` override the saved server; the host and port can also be set in the window
and are remembered (in `~/Library/Application Support/SimpleAnalyzer/settings.json` on macOS). The
default is 127.0.0.1:40001 (the ExpertSDR3 default); Thetis and this ExpertSDR3 install use 50001.

## Benchmark
`tools/signal-bench` streams model AM signals of known depth through the analysis code and checks
the readings (`cd tools/signal-bench && dotnet run -c Release`); see its README.

## Licence
GPL-3.0, as SimpleHPSDR, which the analysis code comes from.
