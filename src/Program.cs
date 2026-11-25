using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SapRExport
{
    internal static class Program
    {
        // cmc2lzss -h prints the full usage. Options are name=value and may come in any order.
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                PrintHelp(Console.Error);
                return 1;
            }
            foreach (string a in args)
            {
                if (a == "-h" || a == "--help" || a == "-?" || a == "/?" || a == "help")
                {
                    PrintHelp(Console.Out);
                    return 0;
                }
            }

            if (args[0] == "--test-player")
                return PlayerTest.Run(args[1], args[2], int.Parse(args[3]),
                                      Convert.ToInt32(args[4], 16),
                                      args.Length > 5 ? int.Parse(args[5]) : 1);

            Console.WriteLine("cmc2lzss " + Version);
            Settings settings;
            try
            {
                settings = ParseArgs(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("cmc2lzss: " + ex.Message);
                Console.Error.WriteLine("run 'cmc2lzss -h' for usage and examples");
                return 1;
            }

            string module = settings.Module;
            int song = settings.Song;
            int seconds = settings.Seconds;
            string outDir = settings.OutDir;
            double minBlockSeconds = settings.MinBlockSeconds;
            string mode = settings.Mode;
            string rate = settings.Rate;
            string monoSafe = settings.MonoSafe;
            Directory.CreateDirectory(outDir);

            // CMS and DMC (dual mono CMC, Fastplay 156) call the player twice per frame in double mode:
            // capture each player update, so no write is lost. Other CMC formats keep one row per frame.
            bool isCms = ModuleIsCms(module);
            bool isDmc = Path.GetExtension(module).ToUpperInvariant() == ".DMC";
            bool perUpdate = rate == "auto" && (isCms || isDmc);

            PokeyDump dump = PokeyDumper.Capture(module, song, seconds, perUpdate);
            List<byte[]> rows = dump.Rows;

            MonoSafe.Result fix = null;
            if (dump.Streams == 18 && monoSafe != "off")
            {
                fix = MonoSafe.Apply(rows, monoSafe);
                rows = fix.Rows;
            }
            bool flagged = monoSafe == "phase";

            int minBlock = Math.Max(1, (int)(minBlockSeconds * dump.RowsPerSecond));
            SongLayout layout = LoopAnalyzer.Analyze(rows, dump.RowsPerSecond, minBlock);
            bool ok = LoopAnalyzer.Verify(rows, layout);

            string baseName = Path.GetFileNameWithoutExtension(module) + "_s" + song;
            if (monoSafe != "off")
                baseName += "_" + monoSafe;

            // SAP header. AUTHOR (30 characters max, after the constant "cmc2lzss:"): stream layout, then 2xVBI (two updates per frame) or 1xVBI (one), then the mono-safe fix
            // twice per frame (double-mode CMS and DMC), and the mono-safe fix. NAME: the full file name.
            // Examples: "cmc2lzss:stereo18 2xVBI detune", "cmc2lzss:mono9 1xVBI", "cmc2lzss:stereo18 1xVBI phase".
            string author = "cmc2lzss:" + (dump.Streams == 18 ? "stereo18" : "mono9") +
                            (dump.UpdatesPerFrame == 2 ? " 2xVBI" : " 1xVBI") +
                            (monoSafe == "detune" ? " detune" : monoSafe == "phase" ? " phase" : "");
            string originalName = Path.GetFileName(module);

            // One .sap (raw SAP-R, one row per update) and one or more .lz16 (LZSS) per subsong.
            foreach (Subsong sub in layout.Subsongs)
            {
                string sapr = Path.Combine(outDir, baseName + "_" + sub.Index + ".sap");
                PokeyDumper.WriteSapR(rows, sub.FirstFrame, sub.Length, sapr, author, originalName);
                sub.Path = sapr;

                string stem = Path.Combine(outDir, baseName + "_" + sub.Index);
                if (dump.Streams == 9)
                {
                    byte[][] cols = Columns(rows, sub.FirstFrame, sub.Length, 9, false);
                    WriteLz16(stem + ".lz16", cols, "mono 9 streams", "mono");
                }
                else
                {
                    if (mode == "both" || mode == "interleave")
                    {
                        byte[][] cols = Interleaved(rows, sub.FirstFrame, sub.Length, flagged);
                        string label = flagged ? "stereo interleaved + reset flag, 10 streams x 2 rows/update"
                                               : "stereo interleaved, 9 streams x 2 rows/update";
                        WriteLz16(stem + (flagged ? "_il_phase.lz16" : "_il.lz16"), cols, label, "interleaved");
                    }
                    if (mode == "both" || mode == "18")
                    {
                        byte[][] cols = Columns(rows, sub.FirstFrame, sub.Length, 18, flagged);
                        string label = flagged ? "stereo 18 streams + reset flag, 19 streams" : "stereo 18 streams";
                        WriteLz16(stem + (flagged ? "_18_phase.lz16" : "_18.lz16"), cols, label, "18");
                    }
                }
            }

            PrintReport(module, song, dump, rows, layout, perUpdate, fix, monoSafe);
            PrintTotals();
            Console.WriteLine("verify   : " + (ok ? "OK (layout rebuilds the capture)" : "FAILED"));
            return ok ? 0 : 2;
        }

        // ---------------------------------------------------------------- command line

        internal const string Version = "1.0";

        private sealed class Settings
        {
            public string Module;
            public int Song = 0;
            public int Seconds = 600;
            public string OutDir = ".";
            public double MinBlockSeconds = 0.5;
            public string Mode = "both";
            public string Rate = "auto";
            public string MonoSafe = "off";
        }

        // Plain (unnamed) values after the module are read in this order, as in the older command line.
        private static readonly string[] PositionalOrder = { "song", "seconds", "out", "minblock", "mode", "rate", "mono-safe" };

        private static Settings ParseArgs(string[] args)
        {
            var s = new Settings();
            var positional = new List<string>();
            var named = new List<KeyValuePair<string, string>>();
            foreach (string a in args)
            {
                int eq = a.IndexOf('=');
                if (eq > 0)
                    named.Add(new KeyValuePair<string, string>(a.Substring(0, eq).ToLowerInvariant(), a.Substring(eq + 1)));
                else if (a.StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("unknown option '" + a + "'");
                else
                    positional.Add(a);
            }

            if (positional.Count == 0)
                throw new ArgumentException("no module given");
            if (positional.Count - 1 > PositionalOrder.Length)
                throw new ArgumentException("too many values: '" + positional[PositionalOrder.Length + 1] + "'");

            s.Module = positional[0];
            for (int i = 1; i < positional.Count; i++)
                SetOption(s, PositionalOrder[i - 1], positional[i]);
            foreach (KeyValuePair<string, string> kv in named)
                SetOption(s, kv.Key, kv.Value);

            if (!File.Exists(s.Module))
                throw new ArgumentException("file not found: " + s.Module);
            return s;
        }

        private static void SetOption(Settings s, string key, string val)
        {
            switch (key)
            {
                case "song":
                    s.Song = ParseInt(key, val, 0, 999);
                    break;
                case "seconds":
                    s.Seconds = ParseInt(key, val, 1, 36000);
                    break;
                case "out":
                case "outdir":
                    if (val.Length == 0)
                        throw new ArgumentException("out needs a folder name");
                    s.OutDir = val;
                    break;
                case "minblock":
                    s.MinBlockSeconds = ParseDouble(key, val, 0.01, 60.0);
                    break;
                case "mode":
                    s.Mode = OneOf(key, val, "both", "interleave", "18");
                    break;
                case "rate":
                    s.Rate = OneOf(key, val, "auto", "1");
                    break;
                case "mono-safe":
                    s.MonoSafe = OneOf(key, val, "off", "detune", "phase");
                    break;
                default:
                    throw new ArgumentException("unknown option '" + key + "='");
            }
        }

        private static int ParseInt(string key, string val, int min, int max)
        {
            int v;
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < min || v > max)
                throw new ArgumentException(key + " must be a whole number from " + min + " to " + max + ", got '" + val + "'");
            return v;
        }

        private static double ParseDouble(string key, string val, double min, double max)
        {
            double v;
            if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < min || v > max)
                throw new ArgumentException(key + " must be a number from " + min + " to " + max + ", got '" + val + "'");
            return v;
        }

        private static string OneOf(string key, string val, params string[] allowed)
        {
            if (Array.IndexOf(allowed, val) < 0)
                throw new ArgumentException(key + " must be " + string.Join(", ", allowed) + ", got '" + val + "'");
            return val;
        }

        private static void PrintHelp(TextWriter w)
        {
            w.WriteLine(
@"cmc2lzss - converts Atari CMC / CMS (SDCMC) / DMC / CM3 modules into POKEY register streams.
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
                  Header: SAP, AUTHOR ""cmc2lzss:"" then ""stereo18"" or ""mono9"", then "" 2xVBI"" (two updates per frame, double-mode CMS and DMC) or "" 1xVBI"" (one update per frame), then
                  then "" detune"" or "" phase"". AUTHOR is
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
  3  --test-player: mismatch or stream not consumed exactly");
        }

        private static bool ModuleIsCms(string module)
        {
            string ext = Path.GetExtension(module).ToUpperInvariant();
            return ext == ".CMS";
        }

        // Stream row k of the encoded data (k = 0 is the initial state, which the player writes at init).
        // Row 0 is a copy of the first captured row, so the player's init consumes exactly one update
        // and every following VBI consumes exactly UpdatesPerFrame updates.
        private static byte[] RowAt(List<byte[]> rows, int start, int k)
        {
            return rows[start + Math.Max(k - 1, 0)];
        }

        // `streams` columns of count+1 rows, register values normalised as lzss-sap does.
        // With withFlag, one more column (index 18) holds the phase-reset flag of each row.
        private static byte[][] Columns(List<byte[]> rows, int start, int count, int streams, bool withFlag)
        {
            int n = count + 1;
            int total = withFlag ? streams + 1 : streams;
            var cols = new byte[total][];
            for (int i = 0; i < total; i++)
                cols[i] = new byte[n];
            for (int k = 0; k < n; k++)
            {
                byte[] r = RowAt(rows, start, k);
                for (int i = 0; i < streams; i++)
                    cols[i][k] = NormValue(i % 9, r[i]);
                if (withFlag)
                    cols[streams][k] = r[MonoSafe.FlagIndex];
            }
            return cols;
        }

        // Stereo "interleave" mode: row 2k = base POKEY of update k, row 2k+1 = stereo POKEY of update k.
        // With withFlag, stream 9 carries the reset flag in the base rows (0 in the stereo rows).
        private static byte[][] Interleaved(List<byte[]> rows, int start, int count, bool withFlag)
        {
            int n = count + 1;
            int total = withFlag ? 10 : 9;
            var cols = new byte[total][];
            for (int i = 0; i < total; i++)
                cols[i] = new byte[2 * n];
            for (int k = 0; k < n; k++)
            {
                byte[] r = RowAt(rows, start, k);
                for (int i = 0; i < 9; i++)
                {
                    cols[i][2 * k] = NormValue(i, r[i]);
                    cols[i][2 * k + 1] = NormValue(i, r[9 + i]);
                }
                if (withFlag)
                    cols[9][2 * k] = r[MonoSafe.FlagIndex];
            }
            return cols;
        }

        // Same simplification as lzss.c: AUDC silence -> 0, and ignore bits that do not change the sound.
        internal static byte NormValue(int reg, byte v)
        {
            if ((reg & 1) == 0)
                return v;   // AUDF and AUDCTL are not changed
            int vol = v & 0x0F;
            int dist = v & 0xF0;
            if (vol == 0)
                return 0;
            if ((dist & 0x10) != 0)
                return (byte)(v & 0x1F);     // volume-only
            if ((dist & 0x20) != 0)
                return (byte)(v & 0xBF);     // no noise: ignore the noise type bit
            return v;
        }

        // Whole-song totals per output format: [0] = raw bytes, [1] = LZSS bytes, summed over all subsongs.
        private static readonly Dictionary<string, long[]> Totals = new Dictionary<string, long[]>();

        private static void PrintTotals()
        {
            Console.WriteLine("whole song (all subsongs together, bytes as the players read them):");
            var names = new Dictionary<string, string>
            {
                { "mono", "mono, 9 streams" },
                { "interleaved", "stereo interleaved, 9 streams" },
                { "18", "stereo, 18 streams" },
            };
            foreach (string key in new[] { "mono", "interleaved", "18" })
            {
                long[] t;
                if (!Totals.TryGetValue(key, out t) || t[0] == 0)
                    continue;
                double pct = 100.0 * t[1] / t[0];
                Console.WriteLine("  " + names[key].PadRight(30) + ": " + t[1] + " from " + t[0] + " raw = " +
                                  pct.ToString("0.0", CultureInfo.InvariantCulture) + "%, saving " +
                                  (100.0 - pct).ToString("0.0", CultureInfo.InvariantCulture) + "%");
            }
            Console.WriteLine();
        }

        private static void WriteLz16(string path, byte[][] cols, string label, string format)
        {
            byte[] enc = LzssEncoder.Encode(cols);
            byte[][] dec = LzssDecoder.Decode(enc, cols.Length, cols[0].Length);

            for (int i = 0; i < cols.Length; i++)
                for (int f = 0; f < cols[i].Length; f++)
                    if (cols[i][f] != dec[i][f])
                        throw new InvalidOperationException(path + ": round trip mismatch at stream " + i + ", row " + f);

            File.WriteAllBytes(path, enc);
            int raw = cols.Length * cols[0].Length;
            long[] t;
            if (!Totals.TryGetValue(format, out t))
            {
                t = new long[2];
                Totals[format] = t;
            }
            t[0] += raw;
            t[1] += enc.Length;
            Console.WriteLine("lz16     : " + path + "  " + enc.Length + " bytes from " + raw + " raw (" +
                              (100.0 * enc.Length / raw).ToString("0.0", CultureInfo.InvariantCulture) + "%), " +
                              label + ", round trip OK");
        }

        private static void PrintReport(string module, int song, PokeyDump dump, List<byte[]> rows, SongLayout layout,
                                        bool perUpdate, MonoSafe.Result fix, string monoSafe)
        {
            int hz = dump.Ntsc ? 60 : 50;
            var sb = new StringBuilder();
            sb.AppendLine("module   : " + module + " (song " + song + ")");
            sb.AppendLine("channels : " + dump.Channels + (dump.Streams == 18 ? " (stereo, 18 streams)" : " (mono, 9 streams)"));
            sb.AppendLine("standard : " + (dump.Ntsc ? "NTSC" : "PAL") + ", " + hz + " frames/s");
            sb.AppendLine("captured : " + dump.Frames + " frames (" + Secs(dump.Frames, hz) + ")");
            if (perUpdate)
                sb.AppendLine("rate     : one row per player update, " + dump.UpdatesPerFrame + " per frame" +
                              (dump.UpdatesPerFrame > 1 ? " (double mode: build the players with UPD = " + dump.UpdatesPerFrame + ")" : " (build the players with UPD = 1)") +
                              ", " + rows.Count + " rows");
            else
                sb.AppendLine("rate     : one row per frame (" + rows.Count + " rows, build the players with UPD = 1)");
            sb.AppendLine("writes   : " + dump.TotalWrites + " POKEY register writes, " + dump.OverwrittenWrites +
                          " overwritten within a row (lost)");

            if (fix != null)
            {
                sb.AppendLine("mono-safe: " + monoSafe + ", rows with a left/right same-pitch clash: " + fix.RowsBefore +
                              " before, " + fix.RowsAfter + " after");
                if (monoSafe == "detune")
                    sb.AppendLine("           right voices nudged by +/-1 AUDF: " + fix.VoicesDetuned +
                                  " voice-rows, left alone (AUDF < " + MonoSafe.MinDetuneAudf + "): " + fix.VoicesSkipped);
                else
                    sb.AppendLine("           phase resets (STIMER on both chips): " + fix.ResetRows +
                                  " rows (build the players with PHASE = 1)");
            }

            int rps = dump.RowsPerSecond;
            if (layout.LoopStart >= 0)
                sb.AppendLine("loop     : from row " + layout.LoopStart + ", period " + layout.LoopPeriod + " rows (" + Secs(layout.LoopPeriod, rps) + ")");
            else
                sb.AppendLine("loop     : none found (song ends)");
            sb.AppendLine();

            sb.AppendLine("subsongs:");
            foreach (Subsong s in layout.Subsongs)
                sb.AppendLine("  " + s.Index + ": " + s.Length + " rows (" + Secs(s.Length, rps) + ")  " + s.Path);
            sb.AppendLine();

            sb.AppendLine("composition (playback order):");
            foreach (Segment seg in layout.Intro)
                sb.AppendLine("  intro: subsong " + seg.Subsong + " x" + seg.Repeat);
            if (layout.LoopStart >= 0)
            {
                sb.AppendLine("  loop body, repeats forever:");
                foreach (Segment seg in layout.Loop)
                    sb.AppendLine("    subsong " + seg.Subsong + " x" + seg.Repeat);
            }
            sb.AppendLine();

            Console.Write(sb.ToString());
        }

        private static string Secs(int rows, int rowsPerSecond)
        {
            return (rows / (double)rowsPerSecond).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }
    }
}
