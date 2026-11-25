# cmc2lzss - CMC / CMS / DMC to POKEY streams

`cmc2lzss` plays an Atari music module in an emulated Atari (the ASAP 6502 core), records every
POKEY register write, and writes the result as:

- a **SAP-R text file** (`.sap`): one row per player update, the register values of that update;
- an **LZSS stream** (`.lz16`): the same rows compressed in the lzss-sap format, read by the
  6502 players in [players/lz16](players/lz16/README.md).

It supports the Chaos Music Composer family: **CMC**, **CMS** (SDCMC stereo), **DMC** (dual mono CMC)
and **CM3**. Stereo modules are written as two POKEYs, either as 9 interleaved streams or as
18 separate streams. Loops and repeated blocks are found in the capture and written as subsongs,
with a playback order printed on the console.

## Build

Requires the .NET 10 SDK.

```powershell
dotnet build -c Release
```

The build puts `cmc2lzss.exe` in the output folder it reports. To run it without installing, use
`dotnet run -c Release -- <module> [options]`. Run `cmc2lzss -h` for the full usage.

## Quick start

```powershell
cmc2lzss GEARS.CMS out=build seconds=120
```

Then assemble a player for the stream you want (see [players/lz16/README.md](players/lz16/README.md)).

## Version

`cmc2lzss 1.0`. The version is printed when the program starts.

## Whole-song compression

After the subsong report, every run prints the total size of all subsongs together, per format.
Raw is the uncompressed register data the player reads; the percentage is the LZSS size divided by raw.

```text
whole song (all subsongs together, bytes as the players read them):
  mono, 9 streams               : 597 from 12267 raw = 4.9%, saving 95.1%
```

## Output files

For each subsong `N` of a module, named `<module>_s<song>[_<mono-safe>]_<N>`:

| File | Content |
|---|---|
| `.sap` | raw SAP-R file, one row per update. Header: `AUTHOR "cmc2lzss:stereo18 2xVBI detune"` (layout, then `2xVBI` or `1xVBI` for updates per frame, then `detune` or `phase`), at most 30 characters, with the prefix `cmc2lzss:`; `NAME "<full original file name>"` |
| `.lz16` (no suffix) | mono: 9 streams |
| `_il.lz16` | stereo, interleaved: 9 streams, base and stereo rows alternate |
| `_18.lz16` | stereo, 18 streams |
| `_il_phase.lz16`, `_18_phase.lz16` | as above with the `mono-safe=phase` reset flag stream |

## Notes

- `rate=auto` (default) captures one row per player update for CMS and DMC. Those players run twice
  per frame in double mode, so the players need `UPD = 2`. Use `rate=1` only with `UPD = 1` players;
  it drops half of the writes of double-mode modules.
- `mono-safe=detune` and `mono-safe=phase` fix left and right voices on the same pitch cancelling
  when the channels are mixed to mono. See the help text for details.

## Usage (`cmc2lzss -h`)

```text
cmc2lzss - converts Atari CMC / CMS (SDCMC) / DMC / CM3 modules into POKEY register streams.
       version 1.0
       Writes SAP-R text files (.sap) and LZSS streams (.lz16) for the players in players/lz16.

USAGE
  cmc2lzss <module> [name=value ...]
  cmc2lzss -h | --help
  cmc2lzss --test-player <player.xex> <subsong.sap> <values-per-row> <get_byte-hex> [updates-per-frame]

OPTIONS  (name=value, in any order; plain values after the module also work, in the order below)
  song=N          song index in the module, 0..999. Default 0.
  seconds=N       seconds of music to capture, 1..36000. Default 600.
                  Loops are only found inside this capture.
  out=DIR         output folder (created if missing). Default . (current folder).
  minblock=S      shortest repeated block accepted, in seconds. Default 0.5.
  mode=M          stereo modules only. Mono modules ignore it.
                    both        write both stereo layouts (default)
                    interleave  9 streams, left and right rows alternate  -> _il.lz16
                    18          18 streams, left and right in own streams -> _18.lz16
  rate=R          auto (default): CMS and DMC = one row per player update (two per frame
                  in double mode); other formats = one row per frame.
                    1           always one row per video frame. Use with UPD = 1 players.
  mono-safe=X     stereo modules only. Fixes left/right voices on the same pitch cancelling
                  when the two channels are mixed to mono.
                    off         no change (default)
                    detune      nudge the right voice's AUDF by +/-1 while it clashes.
                                Players need no change. Voices with AUDF below 8 are kept.
                    phase       extra stream with a reset flag; the players write STIMER on
                                both chips when the clashing pitches change. Build the players
                                with PHASE = 1 and use the *_phase.lz16 files.

OUTPUT  per subsong N, named <module>_s<song>[_<mono-safe>]_<N>:
  .sap            raw SAP-R file, one row per update (for lzss-sap and --test-player).
                  Header: SAP, AUTHOR "cmc2lzss:stereo18" or "cmc2lzss:mono9", then " 2xVBI" (two updates per frame, double-mode CMS and DMC) or " 1xVBI" (one update per frame), then
                  " detune" or " phase". AUTHOR is
                  cut to 30 characters. NAME is the full original file name.
  .lz16           LZSS stream for the player. Suffix: _18, _il, _18_phase or _il_phase.
  The console report lists the subsongs and the playback order: intro once, then the loop
  body, which repeats forever (if a loop was found).

EXAMPLES
  cmc2lzss GEARS.CMS
      Everything at default: both stereo layouts, 600 s, current folder.

  cmc2lzss GEARS.CMS mono-safe=phase
      Phase fix for mono playback. Build the players with PHASE = 1 and use the *_phase files.

  cmc2lzss GEARS.CMS out=build seconds=120 mode=18
      Only the 18-stream layout, 120 s of music, into folder build.

  cmc2lzss CYBER.CMS song=2 rate=1
      Song 2, one row per video frame (for double-mode CMS with UPD = 1 players).

  cmc2lzss CYBER.CMS mono-safe=detune seconds=60
      Detune fix for the first 60 seconds.

  cmc2lzss --test-player players\lz16\player18.xex GEARS_s0_0.sap 18 0x1234
      Runs a built player in the 6502 core and compares it with the .sap. 0x1234 is the
      address of song_end in the player's listing (player18.lab / .lst).

EXIT CODES
  0  done          1  usage or option error
  2  loop layout did not verify against the capture
  3  --test-player: mismatch or stream not consumed exactly
```

## License

This project is licensed under the GNU General Public License, version 2 (see [LICENSE](LICENSE)).
It includes a copy of the ASAP 6502 core, which is GPL version 2 or later, so the combined program is GPL.
The LZSS encoder and decoder are ports of lzss-sap, which is MIT licensed; the MIT terms are compatible with the GPL.

## References

- **lzss-sap** by Daniel Serpell (dmsc): LZSS compressor and the 6502 `playlzs16` player that the `.lz16` format follows.
  https://github.com/dmsc/lzss-sap (MIT License, Copyright (c) 2020 Daniel Serpell)
- **ASAP** (Another Slight Atari Player) by Piotr Fusik: the 6502 emulation core, plus the embedded CMC and CMS players (CMC/DMC players by Marcin Lewandowski, CMS player by David Spilka). It is the source of the POKEY capture core,
  GNU GPL version 2 or later. Project home: https://asap.sourceforge.net/
