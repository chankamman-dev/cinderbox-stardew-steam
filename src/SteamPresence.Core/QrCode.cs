using System;
using System.Collections.Generic;
using System.Text;

namespace SteamPresence.Core;

// Minimal QR Code encoder (byte mode, error correction M, versions 1-10), after Project Nayuki's
// reference implementation. Enough for a Steam login challenge URL (~40 bytes, version 3).
// Returns modules[y, x], true = dark, without the quiet zone.
public static class QrCode
{
    static readonly int[] EccPerBlockM = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    static readonly int[] BlocksM = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
    const int FormatBitsM = 0;

    public static bool[,] Encode(string text) => EncodeWithMask(text, -1);

    // forceMask 0-7 for tests; -1 picks the lowest penalty.
    public static bool[,] EncodeWithMask(string text, int forceMask)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        for (; version <= 10; version++)
        {
            int capacityBits = DataCodewords(version) * 8;
            int needed = 4 + (version <= 9 ? 8 : 16) + data.Length * 8;
            if (needed <= capacityBits) break;
        }
        if (version > 10) throw new ArgumentException("text too long for QR versions 1-10");

        // Data bits: mode, length, bytes, terminator, byte padding, pad codewords.
        var bits = new List<bool>();
        void Append(int value, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (byte b in data) Append(b, 8);
        int capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        byte[] all = AddEccAndInterleave(codewords, version);
        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var function = new bool[size, size];
        DrawFunctionPatterns(modules, function, version, size);
        DrawCodewords(modules, function, all, size);

        int bestMask = 0;
        long bestPenalty = long.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, function, mask, size);
            DrawFormatBits(modules, function, mask, size);
            long p = Penalty(modules, size);
            if (p < bestPenalty) { bestPenalty = p; bestMask = mask; }
            ApplyMask(modules, function, mask, size); // undo (XOR)
        }
        if (forceMask >= 0) bestMask = forceMask;
        ApplyMask(modules, function, bestMask, size);
        DrawFormatBits(modules, function, bestMask, size);
        return modules;
    }

    static int RawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    static int DataCodewords(int ver) => RawDataModules(ver) / 8 - EccPerBlockM[ver] * BlocksM[ver];

    static byte[] AddEccAndInterleave(byte[] data, int ver)
    {
        int numBlocks = BlocksM[ver];
        int blockEccLen = EccPerBlockM[ver];
        int rawCodewords = RawDataModules(ver) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;
        byte[] divisor = RsDivisor(blockEccLen);
        var blocks = new List<byte[]>();
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;
            byte[] ecc = RsRemainder(dat, divisor);
            var block = new byte[shortBlockLen + 1];
            Array.Copy(dat, 0, block, 0, datLen);
            // Short blocks keep a placeholder byte at index datLen, skipped when interleaving.
            Array.Copy(ecc, 0, block, block.Length - blockEccLen, blockEccLen);
            blocks.Add(block);
        }
        var result = new List<byte>(rawCodewords);
        for (int i = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < blocks.Count; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result.Add(blocks[j][i]);
        return result.ToArray();
    }

    static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Mul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Mul(root, 0x02);
        }
        return result;
    }

    static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= Mul(divisor[i], factor);
        }
        return result;
    }

    static byte Mul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    static void Set(bool[,] m, bool[,] f, int x, int y, bool dark) { m[y, x] = dark; f[y, x] = true; }

    static void DrawFunctionPatterns(bool[,] m, bool[,] f, int ver, int size)
    {
        for (int i = 0; i < size; i++)
        {
            Set(m, f, 6, i, i % 2 == 0);
            Set(m, f, i, 6, i % 2 == 0);
        }
        DrawFinder(m, f, 3, 3, size);
        DrawFinder(m, f, size - 4, 3, size);
        DrawFinder(m, f, 3, size - 4, size);

        int[] align = AlignmentPositions(ver, size);
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Set(m, f, align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);

        DrawFormatBits(m, f, 0, size); // reserve; real bits drawn after masking
        if (ver >= 7)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = ver << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(m, f, a, b, bit);
                Set(m, f, b, a, bit);
            }
        }
    }

    static void DrawFinder(bool[,] m, bool[,] f, int x, int y, int size)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < size && yy >= 0 && yy < size)
                    Set(m, f, xx, yy, dist != 2 && dist != 4);
            }
    }

    static int[] AlignmentPositions(int ver, int size)
    {
        if (ver == 1) return Array.Empty<int>();
        int numAlign = ver / 7 + 2;
        int step = (ver * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    static void DrawFormatBits(bool[,] m, bool[,] f, int mask, int size)
    {
        int data = FormatBitsM << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (int i = 0; i <= 5; i++) Set(m, f, 8, i, Bit(i));
        Set(m, f, 8, 7, Bit(6));
        Set(m, f, 8, 8, Bit(7));
        Set(m, f, 7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Set(m, f, 14 - i, 8, Bit(i));
        for (int i = 0; i < 8; i++) Set(m, f, size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Set(m, f, 8, size - 15 + i, Bit(i));
        Set(m, f, 8, size - 8, true);
    }

    static void DrawCodewords(bool[,] m, bool[,] f, byte[] data, int size)
    {
        int i = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!f[y, x] && i < data.Length * 8)
                    {
                        m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    static void ApplyMask(bool[,] m, bool[,] f, int mask, int size)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (f[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) m[y, x] = !m[y, x];
            }
    }

    // Runs, 2x2 blocks and dark/light balance (finder-like patterns left out; any mask decodes).
    static long Penalty(bool[,] m, int size)
    {
        long result = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? m[a, b] : m[b, a];
                    bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                    if (cur == prev) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                    else run = 1;
                }
            }
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                bool c = m[y, x];
                if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1]) result += 3;
            }
        int dark = 0;
        foreach (bool v in m) if (v) dark++;
        int total = size * size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += Math.Max(0, k) * 10;
        return result;
    }
}
