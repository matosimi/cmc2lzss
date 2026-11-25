# LZSS players (Atari 6502)

These players decode the `.lz16` streams written by `cmc2lzss` and write the POKEY registers
once per update. They are in MADS syntax.

| Source | Streams | POKEYs | Input file (`ins` line) | Use for |
|---|---|---|---|---|
| `player18.asm` | 18 (+1 with `PHASE`) | two, one stream per register | `song_18.lz16` | stereo, 18 streams |
| `player_il.asm` | 9 (+1 with `PHASE`) | two, rows alternate | `song_il.lz16` | stereo, interleaved |
| `player_mono.asm` | 9 | one, or pseudo stereo with `PSEUDO = 1` | `song.lz16` | mono and DMC, optional pseudo stereo |

## Compile

Requires the MADS assembler (`mads.exe`). Run from this folder:

```powershell
mads player18.asm -o:player18.xex
mads player_il.asm -o:player_il.xex
mads player_mono.asm -o:player_mono.xex
```

The file named in the `ins` line must exist next to the source. Copy or rename the `cmc2lzss` output
to that name, or change the `ins` line. Example for the 18-stream player:

```powershell
copy ..\..\GEARS_s0_0_18.lz16 song_18.lz16
mads player18.asm -o:player18.xex
```

Change the constants at the top of the source and assemble again for another setting.
The `.xex` runs in Altirra or on hardware.

## Matching the player to the `cmc2lzss` output

| `cmc2lzss` output | Player | Settings |
|---|---|---|
| `*_18.lz16` | `player18.asm` | `UPD = 2` for CMS/DMC double mode |
| `*_18_phase.lz16` | `player18.asm` | `PHASE = 1`, `UPD = 2` |
| `*_il.lz16` | `player_il.asm` | `UPD = 2` for CMS/DMC double mode |
| `*_il_phase.lz16` | `player_il.asm` | `PHASE = 1`, `UPD = 2` |
| `*_s0_N.lz16` (mono module) | `player_mono.asm` | `UPD = 2` for CMC/DMC double mode |

`UPD` must match the capture: `cmc2lzss` prints `build the players with UPD = 2` (or `UPD = 1`) in its report.
With `rate=1` the capture has one row per frame, so use `UPD = 1`.

## Constants

| Constant | Meaning |
|---|---|
| `UPD` | updates per VBI (1 or 2). Each update decodes one row per stream (`player_il`: a base row and a stereo row). |
| `PHASE` | 1 when the stream has the STIMER reset flag (`mono-safe=phase`). Adds one stream and writes `STIMER` on both POKEYs when the flag is set. |
| `PSEUDO` | `player_mono` only. 1 writes the same 9 values to POKEY 2, delayed by one frame (`UPD` rows). |
| `NCH` | number of streams (derived from `PHASE` in the stereo players). |

With `UPD = 2`, the stereo-interleaved and mono players wait about 15 scanlines (`vcpause`) between the two updates
of a frame. `player_il` lets you change this with SELECT/START and shows the value and the stream address on screen.

## Differences between the three sources

**`player18.asm`** — 18 streams, one per register of both POKEYs.
- Each update decodes all 18 streams and writes `$D200..$D208` and `$D210..$D218` together (`output_regs`).
- Skip header: 3 bytes (17 flags, channels 17..1).
- `PHASE` adds stream 18 and resets both dividers with `STIMER` at `$D209` / `$D219`.
- Largest of the three files: 18 history buffers of 256 bytes.

**`player_il.asm`** — 9 streams, two rows per update.
- Row 2k is the base POKEY (`out_base`), row 2k+1 is the stereo POKEY (`out_ext`). `decode_update` decodes and writes both.
- Writes SKCTL on both POKEYs at init (`mva #$03 $d20f`).
- Skip header: 1 byte (8 flags).
- `PHASE` stores the reset flag in stream 9 of the base rows.
- Has a small on-screen UI: SELECT/START adjusts the vcount delay between updates, and the stream address is shown.
- The stream's first row is written at init; the first stereo row is decoded right after.

**`player_mono.asm`** — 9 streams, one POKEY by default.
- `PSEUDO = 0`: writes only `$D200..$D208`.
- `PSEUDO = 1`: also writes `$D210..$D218`. The values are the ones written to POKEY 1 `UPD` rows earlier:
  `hist0` for `UPD = 1`, `hist0` and `hist1` for `UPD = 2`. At init both POKEYs get the initial values.
- No `PHASE` option (mono has no second POKEY to reset).
- No on-screen UI; the program loops from the start when the song ends (`jmp start`).

Common to all three: the same LZSS decoder as `playlzs16.asm` (8-bit offset, 8-bit length, min length 1), the same
entry points (`$0600` `init_song`, `$0603` `play_frame`), and the same end-of-song check against `song_end`.

## Test harness

`cmc2lzss --test-player <player.xex> <file.sap> <values-per-row> <song_end-hex> [updates-per-frame]` runs a player in the
6502 core and compares every update with the `.sap` file. It only supports the 18-value (`player18`) players at the moment.
The `song_end` address is in the player's `.lab` or `.lst` file.

## Notes on the source headers

Some header comments name a different input file than the `ins` line:

- `player18.asm` says `song.lz16` in its header, but the `ins` line reads `song_18.lz16`.
- `player_il.asm` says `song.lz16` in its header, but the `ins` line reads `song_il.lz16`.
- `player_mono.asm` reads `song.lz16` in the `ins` line, and its `UPD` comment says the default is 2, but the value in the file is `UPD = 1`.

The `ins` line is what the assembler uses, so follow it.
