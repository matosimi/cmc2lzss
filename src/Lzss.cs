using System;
using System.Collections.Generic;

namespace SapRExport
{
    /// <summary>
    /// C# port of the LZSS SAP-R encoder from dmsc/lzss-sap (src/lzss.c, MIT license),
    /// fixed to the "-6" format used by asm/playlzs16.asm:
    ///   match offset 8 bits, match length 8 bits (1..256), total 16 bits, format version 0
    ///   (first literal of every stream is always present in the output).
    ///
    /// Stream layout produced:
    ///   1. skip flags for streams n-1..1 (1 = stream is constant and not stored), packed LSB first
    ///   2. initial values, streams n-1..0 (one byte each, always present)
    ///   3. the coded data, positions 1..size-1, for each position streams n-1..0.
    ///      Each coded item is one flag bit (1 = literal, 0 = match) packed into a shared
    ///      byte, followed by the literal byte or two match bytes (offset, length-1).
    /// </summary>
    public static class LzssEncoder
    {
        private const int BitsMoff = 8;
        private const int MaxOff = 1 << BitsMoff;                // 256
        private const int BitsMlen = 8;
        private const int MinMlen = 1;
        private const int MaxMlen = MinMlen + (1 << BitsMlen) - 1; // 256
        private const int BitsLiteral = 1 + 8;
        private const int BitsMatch = 1 + BitsMoff + BitsMlen;   // 17

        /// <param name="streams">streams[i][pos] - one array per POKEY-register stream, equal lengths.</param>
        /// <param name="forceLastLiteral">Force the last byte of stream 0 to be a literal (lzss-sap default).</param>
        public static byte[] Encode(byte[][] streams, bool forceLastLiteral = true)
        {
            int n = streams.Length;
            int sz = streams[0].Length;
            if (sz == 0)
                throw new ArgumentException("empty stream");

            var w = new BitWriter();
            var skip = new bool[n];

            // Header: skip flags for streams n-1..1
            for (int i = n - 1; i >= 0; i--)
            {
                byte first = streams[i][0];
                int changes = 0;
                for (int j = 0; j < sz; j++)
                    if (streams[i][j] != first)
                        changes++;

                if (i != 0 && changes == 0)
                {
                    w.AddBit(1);
                    skip[i] = true;
                }
                else
                {
                    if (i != 0)
                        w.AddBit(0);
                    skip[i] = false;
                }
            }
            w.Flush();

            // Initial values, streams n-1..0
            for (int i = n - 1; i >= 0; i--)
                w.AddByte(streams[i][0]);
            w.Flush();

            // Optimal parsing per stream
            var lz = new LzState[n];
            for (int i = 0; i < n; i++)
            {
                if (skip[i])
                    continue;
                lz[i] = new LzState(streams[i], sz);
                Backfill(lz[i], sz, false);
            }

            bool endNotOk = true;
            for (int i = 0; i < n; i++)
                if (!skip[i])
                    endNotOk &= LastIsMatch(lz[i], sz);

            if (forceLastLiteral && endNotOk)
                Backfill(lz[0], sz, true);   // stream 0 is never skipped

            // Emit
            var lpos = new int[n];
            for (int i = 0; i < n; i++)
                lpos[i] = -1;

            for (int pos = 1; pos < sz; pos++)
                for (int i = n - 1; i >= 0; i--)
                    if (!skip[i])
                        lpos[i] = EncodeAt(w, lz[i], pos, lpos[i]);

            return w.Buffer.ToArray();
        }

        private sealed class LzState
        {
            public readonly byte[] Data;
            public readonly int[] Bits;   // bits needed to code from this position
            public readonly int[] Mlen;   // best match length at this position (0 = literal)
            public readonly int[] Mpos;   // best match distance at this position

            public LzState(byte[] data, int size)
            {
                Data = data;
                Bits = new int[size];
                Mlen = new int[size];
                Mpos = new int[size];
            }
        }

        private sealed class BitWriter
        {
            public readonly List<byte> Buffer = new List<byte>();
            private int bpos = -1;   // index of the byte holding the current flag bits
            private int bnum;        // bits used in that byte

            public void AddBit(int bit)
            {
                if (bpos < 0)
                {
                    bpos = Buffer.Count;
                    bnum = 0;
                    Buffer.Add(0);
                }
                if (bit != 0)
                    Buffer[bpos] = (byte)(Buffer[bpos] | (1 << bnum));
                bnum++;
                if (bnum == 8)
                {
                    bpos = -1;
                    bnum = 0;
                }
            }

            public void AddByte(int value)
            {
                Buffer.Add((byte)value);
            }

            public void Flush()
            {
                bpos = -1;
                bnum = 0;
            }
        }

        // Longest match at pos among the previous MaxOff positions. Returns length, distance in mpos.
        private static int Match(byte[] data, int pos, int size, out int mpos)
        {
            int mxlen = Math.Min(MaxMlen, size - pos);
            int mlen = 0;
            mpos = 0;
            for (int i = Math.Max(pos - MaxOff, 0); i < pos; i++)
            {
                int ml = CommonLength(data, pos, i, mxlen);
                if (ml > mlen)
                {
                    mlen = ml;
                    mpos = pos - i;
                }
            }
            return mlen;
        }

        private static int CommonLength(byte[] data, int a, int b, int max)
        {
            for (int i = 0; i < max; i++)
                if (data[a + i] != data[b + i])
                    return i;
            return max;
        }

        // Optimal parse from the end of the stream. If lastLiteral, the last byte is forced to be a literal.
        private static void Backfill(LzState lz, int size, bool lastLiteral)
        {
            int lastBits = 0;
            if (size == 0)
                return;

            if (lastLiteral)
            {
                lz.Mlen[size - 1] = 0;
                lz.Bits[size - 1] = BitsLiteral;
                lastBits = BitsLiteral;
                if (size == 1)
                    return;
                size--;
            }

            lz.Mlen[size - 1] = 0;
            lz.Bits[size - 1] = BitsLiteral + lastBits;

            for (int pos = size - 2; pos >= 0; pos--)
            {
                int mp;
                int ml = Match(lz.Data, pos, size, out mp);

                int best = lz.Bits[pos + 1] + BitsLiteral;
                lz.Bits[pos] = best;
                lz.Mlen[pos] = 0;
                lz.Mpos[pos] = mp;

                for (int l = ml; l >= MinMlen; l--)
                {
                    int b = 0;
                    if (pos + l < size)
                        b = lz.Bits[pos + l] + BitsMatch;
                    else if (pos + l == size)
                        b = BitsMatch + lastBits;

                    if (b < best)
                    {
                        best = b;
                        lz.Bits[pos] = best;
                        lz.Mlen[pos] = l;
                        lz.Mpos[pos] = mp;
                    }
                }
            }
        }

        private static bool LastIsMatch(LzState lz, int size)
        {
            bool last = false;
            for (int pos = 1; pos < size;)
            {
                int mlen = lz.Mlen[pos];
                if (mlen < MinMlen)
                {
                    last = false;
                    pos++;
                }
                else
                {
                    pos += mlen;
                    last = true;
                }
            }
            return last;
        }

        private static int EncodeAt(BitWriter w, LzState lz, int pos, int lpos)
        {
            if (pos <= lpos)
                return lpos;

            int mlen = lz.Mlen[pos];
            int mpos = lz.Mpos[pos];

            if (mlen < MinMlen)
            {
                w.AddBit(1);
                w.AddByte(lz.Data[pos]);
                return pos;
            }

            int codePos = (pos - mpos - 2) & (MaxOff - 1);   // absolute index in the 256-byte ring
            int codeLen = mlen - MinMlen;
            w.AddBit(0);
            int mb = ((codeLen + 1) << BitsMoff) + codePos;
            w.AddByte(mb & 0xFF);
            w.AddByte(mb >> 8);                               // truncated to 8 bits, as in the C code
            return pos + mlen - 1;
        }
    }

    /// <summary>
    /// Decoder that mirrors asm/playlzs16.asm exactly. Used to verify the encoder output
    /// (round trip). Produces the same per-stream arrays that were encoded.
    /// </summary>
    public static class LzssDecoder
    {
        public static byte[][] Decode(byte[] s, int n, int sz)
        {
            var skip = new bool[n];
            int headerBits = n - 1;
            int p = (headerBits + 7) / 8;
            for (int k = 0; k < headerBits; k++)
            {
                int ch = n - 1 - k;
                skip[ch] = ((s[k / 8] >> (k % 8)) & 1) != 0;
            }

            var init = new byte[n];
            for (int i = n - 1; i >= 0; i--)
                init[i] = s[p++];

            var streams = new byte[n][];
            var ring = new byte[n][];
            var copyLen = new int[n];
            var copyPos = new int[n];
            for (int i = 0; i < n; i++)
            {
                streams[i] = new byte[sz];
                ring[i] = new byte[256];
                streams[i][0] = init[i];
                ring[i][255] = init[i];   // the player writes init bytes at index 255
            }

            int bitData = 1;
            for (int f = 1; f < sz; f++)
            {
                int curPos = (f - 1) & 255;
                for (int ch = n - 1; ch >= 0; ch--)
                {
                    if (skip[ch])
                    {
                        streams[ch][f] = init[ch];
                        continue;
                    }

                    int value;
                    if (copyLen[ch] != 0)
                    {
                        value = CopyByte(ring[ch], copyPos, copyLen, ch);
                    }
                    else
                    {
                        // lsr bit_data / refill
                        int carry = bitData & 1;
                        bitData >>= 1;
                        int literal;
                        if (bitData != 0)
                        {
                            literal = carry;
                        }
                        else
                        {
                            byte a = s[p++];
                            literal = a & 1;
                            bitData = 0x80 | (a >> 1);
                        }

                        if (literal != 0)
                        {
                            value = s[p++];
                        }
                        else
                        {
                            copyPos[ch] = s[p++];
                            copyLen[ch] = s[p++];
                            value = CopyByte(ring[ch], copyPos, copyLen, ch);
                        }
                    }

                    streams[ch][f] = (byte)value;
                    ring[ch][curPos] = (byte)value;
                }
            }
            return streams;
        }

        private static int CopyByte(byte[] ring, int[] copyPos, int[] copyLen, int ch)
        {
            copyLen[ch] = (copyLen[ch] - 1) & 255;
            copyPos[ch] = (copyPos[ch] + 1) & 255;
            return ring[copyPos[ch]];
        }
    }
}
