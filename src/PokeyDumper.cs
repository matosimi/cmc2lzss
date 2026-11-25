using System;
using System.Collections.Generic;
using System.IO;
using Sf.Asap;

namespace SapRExport
{
    /// <summary>
    /// Register state of one subsong, one row per "row" (see PerUpdate).
    /// Each row holds 9 bytes for the base POKEY ($D200-$D208), followed by
    /// 9 bytes for the stereo POKEY ($D210-$D218) when the module is stereo.
    /// Byte order inside a block: AUDF1, AUDC1, AUDF2, AUDC2, AUDF3, AUDC3, AUDF4, AUDC4, AUDCTL.
    /// </summary>
    public sealed class PokeyDump
    {
        public int Streams;            // 9 (mono) or 18 (stereo: 9 base + 9 extra)
        public bool Ntsc;
        public int Channels;
        public bool PerUpdate;         // true: a row per player update (CMS double mode gives 2 per frame)
        public int Frames;             // video frames captured
        public int UpdatesPerFrame;    // player updates per video frame (1 or 2 for CMS)
        public int RowsPerSecond;      // rows of the output stream per second of music
        public readonly List<byte[]> Rows = new List<byte[]>();

        // Diagnostics: raw register writes that reached the hook, and how many were overwritten
        // by a later write to the same register inside the same row (lost in the output).
        public int TotalWrites;
        public int OverwrittenWrites;
    }

    public static class PokeyDumper
    {
        // Offset of the CMS replay update routine (L_0AA8 in the SDCMC/ASAP player source, player at $0500).
        private const int CmsUpdateOffset = 0x0AA8 - 0x0500;

        /// <summary>
        /// Plays one song of the module in the emulated Atari and records the POKEY register state.
        /// </summary>
        /// <param name="path">Module file (CMC, CM3, CMR, CMS, ...).</param>
        /// <param name="song">Zero-based song index.</param>
        /// <param name="seconds">Length to capture, in seconds.</param>
        /// <param name="perUpdate">
        /// false: one row per video frame (the register state at the end of the frame).
        /// true: one row per player update. Only CMS is supported; double-mode CMS calls the update
        /// twice per frame, so this keeps both sets of writes.
        /// </param>
        public static PokeyDump Capture(string path, int song, int seconds, bool perUpdate)
        {
            byte[] module = File.ReadAllBytes(path);
            var asap = new ASAP();
            asap.Load(path, module, module.Length);

            ASAPInfo info = asap.GetInfo();
            int channels = info.GetChannels();
            bool ntsc = info.IsNtsc();
            int streams = channels > 1 ? 18 : 9;
            int hz = ntsc ? 60 : 50;

            if (perUpdate && info.Type != ASAPModuleType.Cms)
                throw new ArgumentException("per-update capture is only implemented for CMS modules");

            // Shadow of the 18 registers. Every raw write updates it.
            byte[] shadow = new byte[18];
            var seenInRow = new bool[18];
            var dump = new PokeyDump
            {
                Streams = streams,
                Ntsc = ntsc,
                Channels = channels,
                PerUpdate = perUpdate,
            };

            asap.PokeyWriteHook = (addr, data) =>
            {
                int reg = addr & 0x0F;
                if (reg > 8)
                    return;                       // only AUDF/AUDC/AUDCTL are dumped
                int idx = (addr & 0x10) != 0 ? 9 + reg : reg;
                shadow[idx] = (byte)data;
                dump.TotalWrites++;
                if (seenInRow[idx])
                    dump.OverwrittenWrites++;
                seenInRow[idx] = true;
            };

            Action<bool> pushRow = (reset) =>
            {
                byte[] rec = new byte[streams];
                Array.Copy(shadow, 0, rec, 0, streams);
                dump.Rows.Add(rec);
                if (reset)
                    Array.Clear(seenInRow, 0, seenInRow.Length);
            };

            int updates = 0;
            bool haveUpdate = false;
            if (perUpdate)
            {
                // Each entry into the update routine means the previous update has finished, so the
                // shadow then holds that update's state. The state after the last update is added at the end.
                asap.PlayerUpdateAddr = info.GetPlayerAddress() + CmsUpdateOffset;
                asap.PlayerUpdateHook = () =>
                {
                    updates++;
                    if (haveUpdate)
                        pushRow(true);
                    haveUpdate = true;
                };
            }

            asap.PlaySong(song, -1);

            int frameCount = seconds * hz;
            for (int f = 0; f < frameCount; f++)
            {
                asap.RunFrame();
                if (!perUpdate)
                {
                    pushRow(true);
                }
            }
            if (perUpdate && haveUpdate)
                pushRow(true);

            dump.Frames = frameCount;
            if (perUpdate)
            {
                dump.UpdatesPerFrame = (int)Math.Round(updates / (double)frameCount);
                dump.RowsPerSecond = hz * Math.Max(1, dump.UpdatesPerFrame);
            }
            else
            {
                dump.UpdatesPerFrame = 1;
                dump.RowsPerSecond = hz;
            }
            return dump;
        }

        /// <summary>
        /// Writes the dump as a SAP-R file: text header (terminated by a blank line),
        /// then one raw record per row. Records are streams-long (9 or 18 bytes).
        /// </summary>
        public static void WriteSapR(PokeyDump dump, string outPath)
        {
            WriteSapR(dump.Rows, 0, dump.Rows.Count, outPath);
        }

        /// <summary>Writes rows [start, start+count) as a SAP-R file.</summary>
        public static void WriteSapR(List<byte[]> rows, int start, int count, string outPath)
        {
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                byte[] header = System.Text.Encoding.ASCII.GetBytes("SAP\r\nTYPE R\r\n\r\n");
                fs.Write(header, 0, header.Length);
                for (int i = start; i < start + count; i++)
                    fs.Write(rows[i], 0, rows[i].Length);
            }
        }
    }
}
