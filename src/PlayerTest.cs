using System;
using System.Collections.Generic;
using System.IO;
using Sf.Asap;

namespace SapRExport
{
    /// <summary>
    /// Runs an assembled player (.xex, see players/lz16) in the 6502 core from the ASAP
    /// copy and checks every update against the normalised SAP-R rows it was built from.
    /// Each update writes 18 POKEY values (base 0..8, then stereo 9..17), so the write stream is
    /// split into groups of 18, one per update.
    /// </summary>
    public static class PlayerTest
    {
        private const int InitEntry = 0x0600;   // jmp init_song
        private const int PlayEntry = 0x0603;   // jmp play_frame
        private const int CycleLimit = 29868;   // one NTSC frame per call is plenty for a player call
        private const int HeaderLen = 15;       // "SAP\r\nTYPE R\r\n\r\n"

        /// <param name="xex">Player binary (MADS output).</param>
        /// <param name="sapr">Raw SAP-R file the player's stream was built from (one row per update).</param>
        /// <param name="valuesPerRow">Values per SAP-R row: 18 for stereo, 9 for mono.</param>
        /// <param name="getByte">Address of get_byte (= song_end in the player listing). The stream
        /// pointer is the operand at getByte+1; after the last frame it must equal getByte.</param>
        /// <param name="updatesPerFrame">UPD the player was built with (1 or 2).</param>
        public static int Run(string xex, string sapr, int valuesPerRow, int getByte, int updatesPerFrame)
        {
            if (valuesPerRow != 18)
                throw new ArgumentException("the test harness covers the 18-value (stereo) players only");

            var asap = new ASAP();
            asap.LoadXex(xex);

            byte[] raw = File.ReadAllBytes(sapr);
            int rows = (raw.Length - HeaderLen) / valuesPerRow;
            int frames = rows / updatesPerFrame;

            // Expected update sequence: the stream starts with a copy of the first row (the init update).
            var expected = new List<byte[]>();
            for (int k = 0; k <= rows; k++)
            {
                int src = Math.Max(k - 1, 0);
                var u = new byte[18];
                for (int i = 0; i < 18; i++)
                    u[i] = Program.NormValue(i % 9, raw[HeaderLen + src * valuesPerRow + i]);
                expected.Add(u);
            }

            var writes = new List<int>();   // POKEY index 0..17 per write, in order
            var values = new List<byte>();
            asap.PokeyWriteHook = (addr, data) =>
            {
                int reg = addr & 0x0F;
                if (reg > 8)
                    return;
                writes.Add((addr & 0x10) != 0 ? 9 + reg : reg);
                values.Add((byte)data);
            };

            asap.RunRoutine(InitEntry, CycleLimit);
            for (int f = 0; f < frames; f++)
                asap.RunRoutine(PlayEntry, CycleLimit);

            // Split the writes into updates of 18 values and compare each with the expected update.
            int updates = writes.Count / 18;
            int mismatches = 0;
            string first = null;
            for (int k = 0; k < updates; k++)
            {
                var state = new byte[18];
                var seen = new bool[18];
                for (int j = 0; j < 18; j++)
                {
                    int idx = writes[k * 18 + j];
                    state[idx] = values[k * 18 + j];
                    seen[idx] = true;
                }
                for (int i = 0; i < 18; i++)
                {
                    if (!seen[i])
                    {
                        mismatches++;
                        if (first == null)
                            first = "update " + k + ": register " + i + " not written";
                        continue;
                    }
                    if (k < expected.Count && state[i] != expected[k][i])
                    {
                        mismatches++;
                        if (first == null)
                            first = "update " + k + ", register " + i + ": player $" + state[i].ToString("X2") +
                                    ", expected $" + expected[k][i].ToString("X2");
                    }
                }
            }

            int ptr = asap.Cpu.Memory[getByte + 1] | (asap.Cpu.Memory[getByte + 2] << 8);
            bool endOk = ptr == getByte;
            bool countOk = updates == rows + 1;

            Console.WriteLine("rows     : " + rows + " (" + frames + " frames x " + updatesPerFrame + " updates)");
            Console.WriteLine("updates  : " + updates + " played, " + (rows + 1) + " expected" + (countOk ? "" : "  (COUNT MISMATCH)"));
            Console.WriteLine("mismatch : " + mismatches + (first != null ? "  first: " + first : ""));
            Console.WriteLine("stream   : pointer $" + ptr.ToString("X4") + ", song_end $" + getByte.ToString("X4") +
                              (endOk ? " (consumed exactly)" : " (NOT at end)"));
            bool pass = mismatches == 0 && endOk && countOk;
            Console.WriteLine(pass ? "result   : PASS" : "result   : FAIL");
            return pass ? 0 : 3;
        }
    }
}
