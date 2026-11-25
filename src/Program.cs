using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SapRExport
{
    internal static class Program
    {
        // usage: sapr <module> [song=0] [seconds=600] [outdir=.] [minblock_seconds=0.5] [mode=both] [rate=auto]
        //   mode (stereo modules only): both | interleave | 18
        //   rate: auto  = CMS: one row per player update (double mode gives 2 per frame); other formats: one per frame
        //         1     = always one row per video frame (last register state of the frame)
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: sapr <module> [song=0] [seconds=600] [outdir=.] [minblock_seconds=0.5] [mode=both] [rate=auto|1]");
                Console.Error.WriteLine("       sapr --test-player <player.xex> <subsong.sapr> <values-per-row> <get_byte-hex> [updates-per-frame=1]");
                return 1;
            }

            if (args[0] == "--test-player")
                return PlayerTest.Run(args[1], args[2], int.Parse(args[3]),
                                      Convert.ToInt32(args[4], 16),
                                      args.Length > 5 ? int.Parse(args[5]) : 1);

            string module = args[0];
            int song = args.Length > 1 ? int.Parse(args[1]) : 0;
            int seconds = args.Length > 2 ? int.Parse(args[2]) : 600;
            string outDir = args.Length > 3 ? args[3] : ".";
            double minBlockSeconds = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0.5;
            string mode = args.Length > 5 ? args[5] : "both";
            string rate = args.Length > 6 ? args[6] : "auto";
            if (mode != "both" && mode != "interleave" && mode != "18")
            {
                Console.Error.WriteLine("mode must be both, interleave or 18");
                return 1;
            }
            if (rate != "auto" && rate != "1")
            {
                Console.Error.WriteLine("rate must be auto or 1");
                return 1;
            }

            bool isCms = ModuleIsCms(module);
            bool perUpdate = rate == "auto" && isCms;

            PokeyDump dump = PokeyDumper.Capture(module, song, seconds, perUpdate);
            int minBlock = Math.Max(1, (int)(minBlockSeconds * dump.RowsPerSecond));

            SongLayout layout = LoopAnalyzer.Analyze(dump.Rows, dump.RowsPerSecond, minBlock);
            string baseName = Path.GetFileNameWithoutExtension(module) + "_s" + song;

            bool ok = LoopAnalyzer.Verify(dump.Rows, layout);

            // One .sapr (raw SAP-R, one row per update) and one or more .lz16 (LZSS) per subsong.
            foreach (Subsong sub in layout.Subsongs)
            {
                string sapr = Path.Combine(outDir, baseName + "_" + sub.Index + ".sapr");
                PokeyDumper.WriteSapR(dump.Rows, sub.FirstFrame, sub.Length, sapr);
                sub.Path = sapr;

                string stem = Path.Combine(outDir, baseName + "_" + sub.Index);
                if (dump.Streams == 9)
                {
                    byte[][] cols = Columns(dump.Rows, sub.FirstFrame, sub.Length, 9);
                    WriteLz16(stem + ".lz16", cols, "mono 9 streams");
                }
                else
                {
                    if (mode == "both" || mode == "interleave")
                    {
                        byte[][] cols = Interleaved(dump.Rows, sub.FirstFrame, sub.Length);
                        WriteLz16(stem + "_il.lz16", cols, "stereo interleaved, 9 streams x 2 rows/update");
                    }
                    if (mode == "both" || mode == "18")
                    {
                        byte[][] cols = Columns(dump.Rows, sub.FirstFrame, sub.Length, 18);
                        WriteLz16(stem + "_18.lz16", cols, "stereo 18 streams");
                    }
                }
            }

            PrintReport(module, song, dump, layout, perUpdate);
            Console.WriteLine("verify   : " + (ok ? "OK (layout rebuilds the capture)" : "FAILED"));
            return ok ? 0 : 2;
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
        private static byte[][] Columns(List<byte[]> rows, int start, int count, int streams)
        {
            int n = count + 1;
            var cols = new byte[streams][];
            for (int i = 0; i < streams; i++)
                cols[i] = new byte[n];
            for (int k = 0; k < n; k++)
            {
                byte[] r = RowAt(rows, start, k);
                for (int i = 0; i < streams; i++)
                    cols[i][k] = NormValue(i % 9, r[i]);
            }
            return cols;
        }

        // Stereo "interleave" mode: row 2k = base POKEY of update k, row 2k+1 = stereo POKEY of update k.
        private static byte[][] Interleaved(List<byte[]> rows, int start, int count)
        {
            int n = count + 1;
            var cols = new byte[9][];
            for (int i = 0; i < 9; i++)
                cols[i] = new byte[2 * n];
            for (int k = 0; k < n; k++)
            {
                byte[] r = RowAt(rows, start, k);
                for (int i = 0; i < 9; i++)
                {
                    cols[i][2 * k] = NormValue(i, r[i]);
                    cols[i][2 * k + 1] = NormValue(i, r[9 + i]);
                }
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

        private static void WriteLz16(string path, byte[][] cols, string label)
        {
            byte[] enc = LzssEncoder.Encode(cols);
            byte[][] dec = LzssDecoder.Decode(enc, cols.Length, cols[0].Length);

            for (int i = 0; i < cols.Length; i++)
                for (int f = 0; f < cols[i].Length; f++)
                    if (cols[i][f] != dec[i][f])
                        throw new InvalidOperationException(path + ": round trip mismatch at stream " + i + ", row " + f);

            File.WriteAllBytes(path, enc);
            int raw = cols.Length * cols[0].Length;
            Console.WriteLine("lz16     : " + path + "  " + enc.Length + " bytes from " + raw + " raw (" +
                              (100.0 * enc.Length / raw).ToString("0.0", CultureInfo.InvariantCulture) + "%), " +
                              label + ", round trip OK");
        }

        private static void PrintReport(string module, int song, PokeyDump dump, SongLayout layout, bool perUpdate)
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
                              ", " + dump.Rows.Count + " rows");
            else
                sb.AppendLine("rate     : one row per frame (" + dump.Rows.Count + " rows, build the players with UPD = 1)");
            sb.AppendLine("writes   : " + dump.TotalWrites + " POKEY register writes, " + dump.OverwrittenWrites +
                          " overwritten within a row (lost)");

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
