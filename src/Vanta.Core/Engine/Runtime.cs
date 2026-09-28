using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vanta.Core.Engine;

public sealed class CheatException : Exception { public CheatException(string m) : base(m) { } }

/// <summary>Everything one enabled cheat changed in the game, so it can be undone exactly.</summary>
public sealed class AppliedCheat
{
    public string CheatId = "";
    public readonly List<(ulong addr, byte[] orig, byte[] now)> Writes = new();
    public readonly List<ulong> Allocs = new();
    public readonly List<string> Exports = new();
}

/// <summary>Applies/restores code cheats (aobPatch / aobInject) and resolves pointer values for one process.</summary>
public sealed class CheatRuntime
{
    private readonly IProcessMemory _mem;
    private readonly GameDef _game;
    private readonly Dictionary<string, ulong> _siteCache = new();
    public Dictionary<string, ulong> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action<string>? Trace { get; set; }
    public TimeSpan FreeDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public CheatRuntime(IProcessMemory mem, GameDef game) { _mem = mem; _game = game; }

    private ModuleInfo Module(string? name)
    {
        var n = name ?? _game.MainModule;
        return _mem.FindModule(n) ?? throw new CheatException(Strings.Get("module.missing", n));
    }

    // ---------------- site resolution ----------------
    public ulong ResolveSite(CheatDef cheat, string siteName, SiteDef site, string? module)
    {
        string key = cheat.Id + "/" + siteName;
        if (_siteCache.TryGetValue(key, out var cached)) return cached;
        var mod = Module(module ?? cheat.Impl.Module);
        var expect = site.Expect != null ? new AobPattern(site.Expect) : null;
        var report = new List<string>();
        int i = 0;
        foreach (var p in site.Patterns)
        {
            i++;
            var pat = new AobPattern(p.Aob);
            var hits = AobScanner.ScanModule(_mem, mod, pat, site.AnyMemory)
                .Select(h => (ulong)((long)h + p.Offset))
                .Where(a => expect == null || Matches(a, expect))
                .Where(a => ChecksOk(a, site.Checks))
                .Distinct().ToList();
            report.Add(Strings.Get("aob.report", i, hits.Count));
            Trace?.Invoke($"{cheat.Id}/{siteName}: pattern {i} -> {hits.Count} hit(s)");
            if (hits.Count == 1) { _siteCache[key] = hits[0]; return hits[0]; }
        }
        if (LeftoverHook(mod, site)) throw new CheatException(Strings.Get("aob.hooked", cheat.Name));
        throw new CheatException(Strings.Get("aob.none", cheat.Name, string.Join(", ", report)));
    }

    /// <summary>True when the site is already hooked (jmp rel32 out of the module + NOP padding where the first
    /// pattern expects the original bytes), e.g. left behind by an earlier session that could not restore.</summary>
    private bool LeftoverHook(ModuleInfo mod, SiteDef site)
    {
        if (site.Overwrite < 5 || site.Patterns.Count == 0 || site.Patterns[0].Offset != 0) return false;
        var tokens = site.Patterns[0].Aob.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < site.Overwrite) return false;
        var hooked = new List<string> { "E9", "??", "??", "??", "??" };
        hooked.AddRange(Enumerable.Repeat("90", site.Overwrite - 5));
        hooked.AddRange(tokens.Skip(site.Overwrite));
        var hits = AobScanner.ScanModule(_mem, mod, new AobPattern(string.Join(' ', hooked)), site.AnyMemory, 2);
        if (hits.Count != 1 || !_mem.TryReadI32(hits[0] + 1, out var rel)) return false;
        ulong target = (ulong)((long)hits[0] + 5 + rel);
        return target < mod.Base || target >= mod.Base + mod.Size;
    }

    private bool Matches(ulong addr, AobPattern expect)
    {
        var b = _mem.ReadBytes(addr, expect.Length);
        return b != null && expect.MatchAt(b, 0);
    }

    private bool ChecksOk(ulong addr, List<CheckDef>? checks)
    {
        if (checks == null) return true;
        foreach (var c in checks)
        {
            long v;
            if (c.U8At.HasValue) { var bb = _mem.ReadBytes(addr + (ulong)(long)c.U8At.Value, 1); if (bb == null) return false; v = bb[0]; }
            else if (c.I32At.HasValue) { if (!_mem.TryReadI32(addr + (ulong)(long)c.I32At.Value, out var iv)) return false; v = iv; }
            else continue;
            if (c.Not != null && c.Not.Contains(v)) return false;
            if (c.Min.HasValue && v < c.Min.Value) return false;
            if (c.Max.HasValue && v > c.Max.Value) return false;
        }
        return true;
    }

    // ---------------- apply ----------------
    public AppliedCheat Apply(CheatDef cheat)
    {
        var impl = cheat.Impl;
        var applied = new AppliedCheat { CheatId = cheat.Id };
        var sites = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, site) in impl.Sites ?? new())
            sites[name] = ResolveSite(cheat, name, site, impl.Module);

        // original bytes (read everything first: nothing is written if a read fails)
        byte[] Orig(ulong addr, int n) => _mem.ReadBytes(addr, n) ?? throw new CheatException(Strings.Get("read.fail", addr));
        var origs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, site) in impl.Sites ?? new())
            origs[name] = Orig(sites[name], Math.Max(site.Overwrite, 16));

        var plannedWrites = new List<(ulong addr, byte[] bytes)>();
        try
        {
            if (impl.Type == "aobInject")
            {
                var near = impl.Alloc?.Near ?? impl.Hooks?.FirstOrDefault()?.Site ?? sites.Keys.First();
                if (!sites.TryGetValue(near, out var nearAddr)) throw new CheatException($"alloc.near '{near}' onbekend");
                uint size = (uint)(impl.Alloc?.Size ?? 0x1000);
                var cave = _mem.Alloc(size, nearAddr);
                if (cave == 0) throw new CheatException(Strings.Get("alloc.fail"));
                applied.Allocs.Add(cave);
                var ext = new Dictionary<string, ulong>(Symbols, StringComparer.OrdinalIgnoreCase);
                foreach (var m in _mem.GetModules()) ext.TryAdd(m.Name, m.Base);       // "Game.dll+1234" in asm
                foreach (var (name, a) in sites)
                {
                    ext[name] = a;
                    var ow = impl.Sites![name].Overwrite;
                    ext[name + "_ret"] = a + (ulong)ow;
                }
                ext["cave"] = cave;
                var lines = ExpandPlaceholders(impl.Asm ?? new(), sites, origs, impl.Sites!);
                AsmResult asm;
                try { asm = MiniAssembler.Assemble(lines, cave, ext); }
                catch (AsmException e) { throw new CheatException(Strings.Get("asm.fail", cheat.Name, e.Message)); }
                if (asm.Code.Length > size) throw new CheatException(Strings.Get("asm.fail", cheat.Name, $"code {asm.Code.Length} > alloc {size}"));
                plannedWrites.Add((cave, asm.Code));
                foreach (var h in impl.Hooks ?? new())
                {
                    if (!sites.TryGetValue(h.Site, out var sa)) throw new CheatException($"hook site '{h.Site}' onbekend");
                    if (!asm.Labels.TryGetValue(h.Label, out var target)) throw new CheatException($"hook label '{h.Label}' onbekend");
                    int ow = impl.Sites![h.Site].Overwrite;
                    long dist = (long)target - (long)(sa + 5);
                    byte[] jmp = dist is >= int.MinValue and <= int.MaxValue ? MiniAssembler.JmpRel32(sa, target, ow)
                        : ow >= 14 ? MiniAssembler.JmpAbs64(target, ow) : throw new CheatException(Strings.Get("alloc.fail"));
                    plannedWrites.Add((sa, jmp));
                }
                foreach (var ex in impl.Exports ?? new())
                {
                    if (!asm.Labels.TryGetValue(ex, out var a)) throw new CheatException($"export '{ex}' is geen label");
                    applied.Exports.Add(ex);
                    Symbols[ex] = a;
                }
            }
            foreach (var p in impl.Patches ?? new())
            {
                if (!sites.TryGetValue(p.Site, out var sa)) throw new CheatException($"patch site '{p.Site}' onbekend");
                plannedWrites.Add((sa + (ulong)p.Offset, AobScanner.ParseHex(p.Bytes)));
            }

            // write: cave first, then patches/hooks (so a hook never points to empty memory)
            foreach (var (addr, bytes) in plannedWrites)
            {
                var orig = Orig(addr, bytes.Length);
                if (!_mem.Write(addr, bytes)) throw new CheatException(Strings.Get("write.fail", addr));
                // cave contents are not restored (the cave is freed) and its data area legitimately changes at runtime
                if (!applied.Allocs.Contains(addr)) applied.Writes.Add((addr, orig, bytes));
                Trace?.Invoke($"{cheat.Id}: wrote {bytes.Length} bytes at {addr:X}");
            }
            return applied;
        }
        catch
        {
            Restore(applied, immediateFree: true);
            throw;
        }
    }

    /// <summary>Restores all bytes in reverse order and frees caves. Returns warnings (empty = clean).</summary>
    public List<string> Restore(AppliedCheat a, bool immediateFree = false)
    {
        var warn = new List<string>();
        for (int i = a.Writes.Count - 1; i >= 0; i--)
        {
            var (addr, orig, now) = a.Writes[i];
            var cur = _mem.ReadBytes(addr, now.Length);
            if (cur != null && !cur.AsSpan().SequenceEqual(now)) warn.Add($"bytes op {addr:X} waren gewijzigd");
            if (!_mem.Write(addr, orig)) warn.Add($"terugzetten op {addr:X} mislukt");
        }
        a.Writes.Clear();
        foreach (var ex in a.Exports) Symbols.Remove(ex);
        a.Exports.Clear();
        var allocs = a.Allocs.ToList();
        a.Allocs.Clear();
        if (allocs.Count > 0)
        {
            // give threads that were inside the cave a moment to leave it
            if (immediateFree || FreeDelay <= TimeSpan.Zero) foreach (var c in allocs) _mem.Free(c);
            else { Thread.Sleep(FreeDelay); foreach (var c in allocs) _mem.Free(c); }
        }
        return warn;
    }

    // ${orig:site} ${orig:site:start:len} ${i32:site:off} ${i32:site:off:+4}
    private static readonly Regex Ph = new(@"\$\{(orig|i32|u8):([A-Za-z_]\w*)(?::(-?(?:0x)?[0-9A-Fa-f]+))?(?::(-?(?:0x)?[0-9A-Fa-f]+))?(?::([+-](?:0x)?[0-9A-Fa-f]+))?\}");

    public static List<string> ExpandPlaceholders(List<string> lines, IReadOnlyDictionary<string, ulong> sites, IReadOnlyDictionary<string, byte[]> origs, IReadOnlyDictionary<string, SiteDef> defs)
    {
        static long H(string s) { bool n = s.StartsWith('-'); if (n || s.StartsWith('+')) s = s[1..]; if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..]; var v = long.Parse(s, NumberStyles.HexNumber); return n ? -v : v; }
        return lines.Select(line => Ph.Replace(line, m =>
        {
            var kind = m.Groups[1].Value; var site = m.Groups[2].Value;
            if (!origs.TryGetValue(site, out var o)) throw new CheatException($"placeholder: site '{site}' onbekend");
            if (kind == "orig")
            {
                int start = m.Groups[3].Success ? (int)H(m.Groups[3].Value) : 0;
                int len = m.Groups[4].Success ? (int)H(m.Groups[4].Value) : defs[site].Overwrite;
                if (start < 0 || len <= 0 || start + len > o.Length) throw new CheatException($"placeholder {m.Value}: buiten bereik");
                return AobScanner.ToHex(o.AsSpan(start, len));
            }
            int off = m.Groups[3].Success ? (int)H(m.Groups[3].Value) : 0;
            long v = kind == "u8" ? o[off] : BitConverter.ToInt32(o, off);
            if (m.Groups[4].Success) v += H(m.Groups[4].Value);
            if (m.Groups[5].Success) v += H(m.Groups[5].Value);
            return v < 0 ? "-" + (-v).ToString("X") : v.ToString("X");
        })).ToList();
    }

    // ---------------- pointer values ----------------
    /// <summary>Final address of a pointer cheat, or null when a pointer in the chain is 0/unreadable (not available yet).</summary>
    public ulong? ResolveAddress(CheatDef cheat)
    {
        var impl = cheat.Impl;
        ulong addr = ResolveBase(cheat, impl.Base ?? throw new CheatException("base ontbreekt"));
        foreach (var off in impl.Offsets ?? new())
        {
            if (!_mem.TryReadU64(addr, out var p) || p == 0 || p < 0x10000) return null;
            addr = p + (ulong)off;
        }
        return addr;
    }

    private static readonly Regex ModOff = new(@"^(?<mod>[^+]+\.(exe|dll))\+(0x)?(?<off>[0-9A-Fa-f]+)$", RegexOptions.IgnoreCase);
    private static readonly Regex SymOff = new(@"^sym:(?<sym>[A-Za-z_]\w*)(?<adj>[+-](0x)?[0-9A-Fa-f]+)?$", RegexOptions.IgnoreCase);

    private ulong ResolveBase(CheatDef cheat, JsonElement b)
    {
        if (b.ValueKind == JsonValueKind.String)
        {
            var s = b.GetString()!.Trim();
            var m = SymOff.Match(s);
            if (m.Success)
            {
                if (!Symbols.TryGetValue(m.Groups["sym"].Value, out var a)) throw new CheatException(Strings.Get("ptr.sym", m.Groups["sym"].Value));
                if (m.Groups["adj"].Success) { var t = m.Groups["adj"].Value; long v = long.Parse(t[1..].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber); a = t[0] == '-' ? a - (ulong)v : a + (ulong)v; }
                return a;
            }
            m = ModOff.Match(s);
            if (m.Success) return Module(m.Groups["mod"].Value).Base + ulong.Parse(m.Groups["off"].Value, NumberStyles.HexNumber);
            throw new CheatException($"ongeldige base '{s}'");
        }
        // { aob, patternOffset, ripOffset, insnLength, module }
        string key = cheat.Id + "/base";
        if (!_siteCache.TryGetValue(key, out var insn))
        {
            var site = new SiteDef { Patterns = new() { new PatternDef { Aob = b.GetProperty("aob").GetString()!, Offset = b.TryGetProperty("patternOffset", out var po) ? po.GetInt64() : 0 } } };
            insn = ResolveSite(cheat, "base", site, b.TryGetProperty("module", out var mo) ? mo.GetString() : cheat.Impl.Module);
            _siteCache[key] = insn;
        }
        int ripOff = b.GetProperty("ripOffset").GetInt32(), len = b.GetProperty("insnLength").GetInt32();
        if (!_mem.TryReadI32(insn + (ulong)ripOff, out var rel)) throw new CheatException(Strings.Get("read.fail", insn));
        return (ulong)((long)insn + len + rel);
    }

    /// <summary>Resolves only the base of a pointer cheat (no dereferencing): used by the verifier.</summary>
    public ulong ResolveAddressBaseOnly(CheatDef cheat) => ResolveBase(cheat, cheat.Impl.Base ?? throw new CheatException("geen base"));

    public static int SizeOf(string? vt) => vt switch { "byte" => 1, "int16" => 2, "int64" or "double" => 8, _ => 4 };

    public double? ReadValue(ulong addr, string? vt)
    {
        var b = _mem.ReadBytes(addr, SizeOf(vt));
        if (b == null) return null;
        return vt switch
        {
            "byte" => b[0], "int16" => BitConverter.ToInt16(b), "int64" => BitConverter.ToInt64(b),
            "float" => BitConverter.ToSingle(b), "double" => BitConverter.ToDouble(b), _ => BitConverter.ToInt32(b),
        };
    }

    public static byte[] Encode(double v, string? vt) => vt switch
    {
        "byte" => new[] { (byte)Math.Clamp(Math.Round(v), 0, 255) },
        "int16" => BitConverter.GetBytes((short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)),
        "int64" => BitConverter.GetBytes((long)Math.Round(v)),
        "float" => BitConverter.GetBytes((float)v),
        "double" => BitConverter.GetBytes(v),
        _ => BitConverter.GetBytes((int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue)),
    };

    public bool WriteValue(ulong addr, double v, string? vt) => _mem.Write(addr, Encode(v, vt));
}
