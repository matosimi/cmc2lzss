# Plan: CMC-family module -> POKEY dump -> LZSS stream converter (.NET C#)

## Context

Goal: a C# tool in `C:\!atari\cmc2lzss` (currently empty, not a git repo) that plays Atari CMC / CMS / SDCMC-type modules in an emulated Atari, records every POKEY write (`$D200-$D208`, and stereo twin `$D210-$D218`), splits the result into subsongs and loop parts, and LZSS-compresses it in the lzss-sap format (https://github.com/dmsc/lzss-sap).

Findings that shape the design:

- **lzss-sap does not write dumps.** `bin/lzss` only reads a SAP-R text file (text header terminated by a blank line, then 9-byte records, one per frame). It writes the compressed stream. It has no stereo support, and its player (`asm/playlzs16.asm`, `.ds 256*9`) writes only `$D200-$D208`.
- **Loops in lzss-sap are trim-based.** `-t` cuts the song at a repeated tail (at least 50 frames). It writes no loop marker or jump, so subsong and loop splitting must be done by our tool before encoding.
- **Emulation core: `C:\!atari\prg\MDCMC\asapplay\asap.cs`.** It is a pure class with no I/O, and it already routes stereo POKEY (`PokeyPair` 4216, `Poke` 4268 uses `addr & 16`). Hook points: `PokeHardware` (~200, POKEY dispatch) and `DoFrame` (360, frame boundary). `asapplay.cs` is a console front end using NAudio and WinForms-era APIs, so it is not reusable as-is.
- **CMX core is GUI-bound.** `Cpu6502.cs` uses `guiForm.Invoke` and `Atari.cs` loads from `Properties.Resources`. Its `Pokey.cs` is WinForms-free but would need extraction work.
- **Neither project has any SAP-R or dump export.** The only trace is a to-do in `CMX\Form1.resx:141` ("record writes ... SAP-R").
- **Suspected bugs to verify, not yet confirmed:**
  - asapplay may underplay `.cms` (`Fastplay` stays at 312, CMX doubles the rate for CMS).
  - CMX `Form1.cs:482-519`: `hz50Multiplier` is not reset on `.cmx` load, so stale state can carry over.
  - CMX hard-codes `channels = 2` (Form1.cs:13).

## Recommended approach

1. **New core project `SapRExport` (C#, .NET Framework 4.x to match the rest of the code, or .NET 8 if preferred).** Copy the asapplay `asap.cs` core and its Pokey / Cpu6502 / Atari pieces. Remove all UI and audio dependencies. Expose a single method: load file, select subsong, run N frames, return a register log.
2. **Capture writes.** Log `(frame, addr, data)` in `PokeHardware`, before any dedupe. Keep a per-frame register state (last value per register per frame, as SAP-R does), with AUDC normalised the same way lzss.c does (volume 0 -> 0, mask volume-only and no-noise bits). Record init writes separately (before frame 0).
3. **Subsongs, loops and CMC repeat blocks (user decision: every loop is a "subsong").**
   - Each part of the output is a subsong: either the whole song played once, the loop section played indefinitely, or a repeat block of the module.
   - A song may be: [intro subsong, played once] + [loop subsong, looped forever]; or the whole music looped from the beginning (one subsong). The module's repeat options (CMC/CMS repeat counts on song lines) become separate subsongs with a repeat count, so the module is not stored repeatedly.
   - Detection: walk the module's order/pattern lines, mark repeat blocks, and confirm each block by comparing the captured frame states for exact equality. Loop start is the first frame that repeats an earlier tail exactly.
   - Console report: print the full composition list, e.g. `subsong 1 (intro) x1, subsong 2 (loop) x inf, subsong 3 (repeat block) x4`, so the user sees how the whole music is built. A repeat block is written once as its own subsong and then played `count` times by the composition list.
   - No extra header file: each subsong is a standard `.sapr` and `.lz16`, and loop/composition info goes to the console only.
4. **Encoding: both options the user asked for.**
   - **Option 1, 9 streams (mono, or stereo interleaved):** stereo is interleaved as one row from left then one row from right. Encoded either by shelling out to `bin/lzss` (`-6` for the 16-bit variant, matching `playlzs16.asm`) or by the C# port. The C# port is the reference, and the external tool is used to cross-check it.
   - **Option 2, 18 streams:** separate L and R POKEY streams, encoded with a C# port of the lzss.c encoder generalised to 18 streams and a 256*18 history buffer. Needs a modified player (copy of `playlzs16.asm`, loop 0..17, `.ds 256*18`). Lzss-sap itself cannot do this.
   - Each subsong is written as `<name>_<n>.sapr` (text, SAP-R style, for lzss-sap and debugging) and `<name>_<n>.lz16` (encoded blob). No extra header.
6. **Future (not in this step):** integration into CMX, after the stand-alone tool works.

## Critical files (reuse / reference)

- `C:\!atari\prg\MDCMC\asapplay\asap.cs` (core; `DoFrame` 360, `PokeHardware` ~200, `PokeyPair` 4216)
- `C:\!atari\prg\MDCMC\asapplay\asapplay.cs` (front end, reference only)
- `C:\!atari\prg\MDCMC\CMX\Pokey.cs` (reference for stereo mask 625-638)
- lzss-sap: `src/lzss.c` (encoder), `asm/playlzs16.asm` (player), used as reference for the port

## Verification

1. **Round trip:** encode a dump, decode it with a C# port of the player logic, and compare byte-for-byte with the original register log.
2. **lzss-sap cross-check:** for a mono module, feed the `.sapr` file to `bin/lzss -6` and check our mode A output is the same size or smaller.
3. **Emulator check:** play the converted stream in an emulator with a 9- or 18-stream player and compare the POKEY write trace against asapplay's own log.
4. **Stereo cases:** test a `.cms` and an SDCMC module; check that the second POKEY register is written and that asapplay does not underplay the CMS.