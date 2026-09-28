using System.Globalization;

namespace Vanta.Core.Engine;

/// <summary>Array-of-bytes pattern: "F2 0F ?? 1? *". Wildcards: ?? / ? / * (byte) and nibble wildcards (1? / ?F).</summary>
public sealed class AobPattern
{
    public byte[] Bytes { get; }
    public byte[] Mask { get; }      // 0xFF exact, 0x00 any, 0xF0/0x0F nibble
    public int Length => Bytes.Length;
    public string Text { get; }
    private readonly int _anchor = -1;  // index of the first fully-known byte (for vectorized IndexOf)

    public AobPattern(string text)
    {
        Text = text.Trim();
        var toks = Text.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // also accept "F20F10" without spaces
        if (toks.Length == 1 && toks[0].Length > 2 && toks[0].Length % 2 == 0)
            toks = Enumerable.Range(0, toks[0].Length / 2).Select(i => toks[0].Substring(i * 2, 2)).ToArray();
        if (toks.Length == 0) throw new FormatException("lege AOB");
        Bytes = new byte[toks.Length]; Mask = new byte[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            var t = toks[i].ToUpperInvariant();
            if (t is "?" or "??" or "*" or "**") continue;
            if (t.Length != 2) throw new FormatException($"ongeldige AOB-byte '{toks[i]}'");
            byte b = 0, m = 0;
            for (int n = 0; n < 2; n++)
            {
                char c = t[n];
                int shift = n == 0 ? 4 : 0;
                if (c == '?' || c == '*') continue;
                if (!Uri.IsHexDigit(c)) throw new FormatException($"ongeldige AOB-byte '{toks[i]}'");
                b |= (byte)(int.Parse(c.ToString(), NumberStyles.HexNumber) << shift);
                m |= (byte)(0xF << shift);
            }
            Bytes[i] = b; Mask[i] = m;
        }
        for (int i = 0; i < Mask.Length; i++) if (Mask[i] == 0xFF) { _anchor = i; break; }
    }

    public bool MatchAt(ReadOnlySpan<byte> data, int pos)
    {
        if (pos < 0 || pos + Bytes.Length > data.Length) return false;
        for (int i = 0; i < Bytes.Length; i++)
            if ((data[pos + i] & Mask[i]) != Bytes[i]) return false;
        return true;
    }

    /// <summary>All match offsets in <paramref name="data"/> (stops after <paramref name="limit"/>).</summary>
    public List<int> FindAll(ReadOnlySpan<byte> data, int limit = int.MaxValue)
    {
        var hits = new List<int>();
        int last = data.Length - Bytes.Length;
        if (last < 0) return hits;
        if (_anchor < 0)
        {
            for (int p = 0; p <= last && hits.Count < limit; p++) if (MatchAt(data, p)) hits.Add(p);
            return hits;
        }
        byte a = Bytes[_anchor];
        int searchFrom = _anchor;
        while (hits.Count < limit)
        {
            int rel = data.Slice(searchFrom, last + _anchor - searchFrom + 1).IndexOf(a);
            if (rel < 0) break;
            int idx = searchFrom + rel;
            int p = idx - _anchor;
            if (MatchAt(data, p)) hits.Add(p);
            searchFrom = idx + 1;
            if (searchFrom > last + _anchor) break;
        }
        return hits;
    }

    public override string ToString() => Text;
}

public static class AobScanner
{
    public const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>Scans [start, start+size) region by region (executable-only unless anyMemory). Returns absolute addresses.</summary>
    public static List<ulong> Scan(IProcessMemory mem, AobPattern pat, ulong start, ulong size, bool anyMemory = false, int limit = 64)
    {
        var hits = new List<ulong>();
        var buf = new byte[ChunkSize + pat.Length];
        foreach (var r in mem.Regions(start, start + size))
        {
            if (!r.Readable || (!anyMemory && !r.Executable)) continue;
            ulong rs = Math.Max(r.Start, start), re = Math.Min(r.Start + r.Size, start + size);
            for (ulong pos = rs; pos < re; pos += ChunkSize)
            {
                int len = (int)Math.Min((ulong)(ChunkSize + pat.Length - 1), re - pos);
                if (len < pat.Length) break;
                var span = buf.AsSpan(0, len);
                if (!mem.Read(pos, span)) continue;
                foreach (var off in pat.FindAll(span, limit - hits.Count))
                {
                    if (off >= ChunkSize) continue;   // belongs to the next chunk (overlap)
                    hits.Add(pos + (ulong)off);
                }
                if (hits.Count >= limit) return hits;
            }
        }
        return hits;
    }

    public static List<ulong> ScanModule(IProcessMemory mem, ModuleInfo mod, AobPattern pat, bool anyMemory = false, int limit = 64) =>
        Scan(mem, pat, mod.Base, mod.Size, anyMemory, limit);

    public static byte[] ParseHex(string hex)
    {
        var p = new AobPattern(hex);
        if (p.Mask.Any(m => m != 0xFF)) throw new FormatException("wildcards not allowed in bytes");
        return p.Bytes;
    }
    public static string ToHex(ReadOnlySpan<byte> b) => string.Join(' ', b.ToArray().Select(x => x.ToString("X2")));
}
