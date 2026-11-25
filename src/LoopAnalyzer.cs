using System;
using System.Collections.Generic;

namespace SapRExport
{
    /// <summary>
    /// A run of one block: <see cref="Period"/> frames starting at <see cref="Start"/>,
    /// played <see cref="Repeat"/> times in a row.
    /// </summary>
    public sealed class Segment
    {
        public int Start;
        public int Period;     // block length in frames
        public int Repeat;     // consecutive plays of the block
        public int Subsong;    // index into SongLayout.Subsongs
    }

    /// <summary>A distinct block of frames. Each subsong is written to its own file.</summary>
    public sealed class Subsong
    {
        public int Index;
        public int FirstFrame;  // where the first occurrence starts in the capture
        public int Length;      // frames
        public string Path;     // output file, set by the caller
    }

    public sealed class SongLayout
    {
        public readonly List<Segment> Intro = new List<Segment>();   // played once
        public readonly List<Segment> Loop = new List<Segment>();    // played forever (empty if no loop)
        public readonly List<Subsong> Subsongs = new List<Subsong>();
        public int IntroEnd;        // frames of the capture used by the intro
        public int LoopStart = -1;  // -1 = the song does not loop
        public int LoopPeriod;
    }

    public static class LoopAnalyzer
    {
        /// <param name="frames">Captured POKEY state, one record per frame.</param>
        /// <param name="hz">Frames per second (50 PAL, 60 NTSC).</param>
        /// <param name="minBlockFrames">Shortest block that may be treated as a repeat.</param>
        public static SongLayout Analyze(List<byte[]> frames, int hz, int minBlockFrames)
        {
            int n = frames.Count;
            var layout = new SongLayout();

            // Give each distinct frame record an exact integer id, so comparisons are cheap.
            var idOf = new Dictionary<byte[], int>(new FrameComparer());
            int[] id = new int[n];
            for (int i = 0; i < n; i++)
            {
                int v;
                if (!idOf.TryGetValue(frames[i], out v))
                {
                    v = idOf.Count;
                    idOf[frames[i]] = v;
                }
                id[i] = v;
            }

            // 1. Find the loop: smallest period P that the tail of the capture repeats with.
            int minCover = Math.Min(n / 4, 10 * hz);
            int loopStart = -1, loopPeriod = 0;
            for (int P = 1; P <= n / 2; P++)
            {
                int s = n;   // pairs (i, i-P) match for every i in [s, n)
                while (s - 1 >= P && id[s - 1] == id[s - 1 - P])
                    s--;
                int L = s - P;                      // earliest frame where periodicity holds
                if (n - L >= Math.Max(2 * P, minCover))
                {
                    loopStart = L;
                    loopPeriod = P;
                    break;
                }
            }

            int introEnd = n;
            if (loopStart >= 0)
            {
                bool constant = true;
                for (int j = loopStart + 1; j < loopStart + loopPeriod; j++)
                {
                    if (id[j] != id[loopStart])
                    {
                        constant = false;
                        break;
                    }
                }
                if (constant)
                {
                    // Silent / held tail: treat it as the end of the song, not as a loop.
                    introEnd = loopStart;
                    loopStart = -1;
                    loopPeriod = 0;
                }
                else
                {
                    introEnd = loopStart;
                }
            }

            layout.IntroEnd = introEnd;
            layout.LoopStart = loopStart;
            layout.LoopPeriod = loopPeriod;

            // 2. Split intro and loop body into literal runs and repeated blocks.
            SegmentRange(id, 0, introEnd, minBlockFrames, layout.Intro);
            if (loopStart >= 0)
                SegmentRange(id, loopStart, loopStart + loopPeriod, minBlockFrames, layout.Loop);

            // 3. Assign each segment's block to a subsong (identical blocks share one subsong).
            foreach (Segment seg in layout.Intro)
                seg.Subsong = AssignSubsong(layout, id, seg);
            foreach (Segment seg in layout.Loop)
                seg.Subsong = AssignSubsong(layout, id, seg);

            return layout;
        }

        /// <summary>
        /// Self-check: rebuilds the capture from the layout (intro once, then the loop body
        /// forever) and compares it with the captured frames. Returns false on any mismatch.
        /// </summary>
        public static bool Verify(List<byte[]> frames, SongLayout layout)
        {
            // Intro: segments must be contiguous from frame 0 to IntroEnd, and each must equal
            // its block played Repeat times.
            int pos = 0;
            foreach (Segment seg in layout.Intro)
            {
                if (seg.Start != pos || !BlockMatches(frames, seg, layout, seg.Start))
                    return false;
                pos += seg.Period * seg.Repeat;
            }
            if (pos != layout.IntroEnd)
                return false;

            if (layout.LoopStart < 0)
                return true;

            // Loop body: contiguous from LoopStart to LoopStart + LoopPeriod.
            pos = layout.LoopStart;
            foreach (Segment seg in layout.Loop)
            {
                if (seg.Start != pos || !BlockMatches(frames, seg, layout, seg.Start))
                    return false;
                pos += seg.Period * seg.Repeat;
            }
            if (pos != layout.LoopStart + layout.LoopPeriod)
                return false;

            // The captured tail must be periodic with the loop period.
            int P = layout.LoopPeriod;
            for (int j = layout.LoopStart; j + P < frames.Count; j++)
                if (!SameFrame(frames[j], frames[j + P]))
                    return false;
            return true;
        }

        private static bool BlockMatches(List<byte[]> frames, Segment seg, SongLayout layout, int start)
        {
            Subsong sub = layout.Subsongs[seg.Subsong];
            for (int r = 0; r < seg.Repeat; r++)
                for (int j = 0; j < seg.Period; j++)
                {
                    int dst = start + r * seg.Period + j;
                    int src = sub.FirstFrame + j;
                    if (dst >= frames.Count || !SameFrame(frames[dst], frames[src]))
                        return false;
                }
            return true;
        }

        private static bool SameFrame(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        /// <summary>
        /// Greedy scan: at each position, take the block length Q (Q &gt;= minBlock) that saves the
        /// most frames by playing a block back to back. Anything else becomes a literal run.
        /// </summary>
        private static void SegmentRange(int[] id, int start, int end, int minBlock, List<Segment> output)
        {
            int p = start;
            bool lastWasLiteral = false;
            while (p < end)
            {
                int bestQ = 0, bestK = 1;
                long bestSave = 0;
                for (int Q = minBlock; Q <= (end - p) / 2; Q++)
                {
                    int m = 0;
                    while (p + m + Q < end && id[p + m] == id[p + m + Q])
                        m++;
                    int K = (m + Q) / Q;     // full copies of the block starting at p
                    if (K >= 2)
                    {
                        long save = (long)(K - 1) * Q;
                        if (save > bestSave)
                        {
                            bestSave = save;
                            bestQ = Q;
                            bestK = K;
                        }
                    }
                }

                if (bestQ > 0)
                {
                    output.Add(new Segment { Start = p, Period = bestQ, Repeat = bestK });
                    p += bestQ * bestK;
                    lastWasLiteral = false;
                }
                else
                {
                    // Literal frame: extend the previous literal run if there is one.
                    if (lastWasLiteral)
                        output[output.Count - 1].Period++;
                    else
                        output.Add(new Segment { Start = p, Period = 1, Repeat = 1 });
                    p++;
                    lastWasLiteral = true;
                }
            }
        }

        private static int AssignSubsong(SongLayout layout, int[] id, Segment seg)
        {
            foreach (Subsong s in layout.Subsongs)
            {
                if (s.Length != seg.Period)
                    continue;
                bool same = true;
                for (int j = 0; j < seg.Period; j++)
                {
                    if (id[s.FirstFrame + j] != id[seg.Start + j])
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                    return s.Index;
            }

            var created = new Subsong
            {
                Index = layout.Subsongs.Count,
                FirstFrame = seg.Start,
                Length = seg.Period,
            };
            layout.Subsongs.Add(created);
            return created.Index;
        }

        private sealed class FrameComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] a, byte[] b)
            {
                if (a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i])
                        return false;
                return true;
            }

            public int GetHashCode(byte[] a)
            {
                unchecked
                {
                    int h = 17;
                    foreach (byte x in a)
                        h = h * 31 + x;
                    return h;
                }
            }
        }
    }
}
