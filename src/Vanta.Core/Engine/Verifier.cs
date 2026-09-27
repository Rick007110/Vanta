using System.Text.Json;

namespace Vanta.Core.Engine;

/// <summary>Checks every AOB site of a game definition against module files on disk (or a live process, read-only)
/// and reports hit counts. Nothing is ever written.</summary>
public sealed class Verifier
{
    public sealed record SiteResult(string CheatId, string Site, bool Ok, string Detail, string? Rva = null);
    public List<SiteResult> Results { get; } = new();
    public int Ok => Results.Count(r => r.Ok);
    public int Failed => Results.Count(r => !r.Ok);

    /// <summary>Module names a definition needs (main module, per-cheat modules, "mod+off" bases).</summary>
    public static List<string> RequiredModules(GameDef g)
    {
        var set = new List<string> { g.MainModule };
        foreach (var c in g.Cheats)
        {
            if (c.Impl.Module != null) set.Add(c.Impl.Module);
            if (c.Impl.Base is JsonElement b)
            {
                if (b.ValueKind == JsonValueKind.String && b.GetString() is string s && s.Contains('+') && !s.StartsWith("sym:", StringComparison.OrdinalIgnoreCase)) set.Add(s[..s.IndexOf('+')]);
                if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("module", out var m) && m.GetString() is string ms) set.Add(ms);
            }
        }
        return set.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Finds module files: 'path' may be the main module file itself or a folder (searched 3 levels deep).</summary>
    public static Dictionary<string, string> LocateModules(GameDef g, string path, Action<string>? warn = null)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var need = RequiredModules(g);
        string dir;
        if (File.Exists(path)) { found[g.MainModule] = Path.GetFullPath(path); dir = Path.GetDirectoryName(Path.GetFullPath(path))!; }
        else if (Directory.Exists(path)) dir = path;
        else throw new FileNotFoundException("bestand of map niet gevonden: " + path);
        foreach (var n in need.Where(n => !found.ContainsKey(n)))
        {
            var f = Search(dir, n, 3) ?? (Path.GetDirectoryName(dir) is string up ? Search(up, n, 2) : null);
            if (f != null) found[n] = f; else warn?.Invoke($"module {n} niet gevonden onder {dir}");
        }
        return found;
    }

    private static string? Search(string dir, string name, int depth)
    {
        try
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f)) return f;
            if (depth <= 0) return null;
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (Search(d, name, depth - 1) is string r) return r;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return null;
    }

    public void Run(GameDef g, IProcessMemory mem)
    {
        var rt = new CheatRuntime(mem, g);
        string? current = null;
        var patternLog = new List<string>();
        rt.Trace = s => { if (current != null && s.StartsWith(current + ":", StringComparison.Ordinal)) patternLog.Add(s[(current.Length + 1)..].Trim()); };
        foreach (var c in g.Cheats)
        {
            foreach (var (name, site) in c.Impl.Sites ?? new())
            {
                current = c.Id + "/" + name; patternLog.Clear();
                try
                {
                    var a = rt.ResolveSite(c, name, site, c.Impl.Module);
                    Results.Add(new SiteResult(c.Id, name, true, string.Join(", ", patternLog), Rva(mem, a)));
                }
                catch (CheatException e) { Results.Add(new SiteResult(c.Id, name, false, Tail(e.Message))); }
            }
            if (c.Impl.Type == "pointer" && c.Impl.Base is JsonElement b)
            {
                if (b.ValueKind == JsonValueKind.Object)
                {
                    current = c.Id + "/base"; patternLog.Clear();
                    try
                    {
                        var addr = rt.ResolveAddressBaseOnly(c);
                        Results.Add(new SiteResult(c.Id, "base", true, string.Join(", ", patternLog) + $" -> statisch adres {Rva(mem, addr)}", Rva(mem, addr)));
                    }
                    catch (CheatException e) { Results.Add(new SiteResult(c.Id, "base", false, Tail(e.Message))); }
                }
            }
        }
    }

    private static string Tail(string msg)
    {
        var i = msg.IndexOf('('); var s = i >= 0 ? msg[i..] : msg;
        return s.Replace(" Niets gepatcht.", "").Replace(" Nothing patched.", "").Trim();
    }

    private static string Rva(IProcessMemory mem, ulong a)
    {
        var m = mem.GetModules().FirstOrDefault(x => a >= x.Base && a < x.Base + x.Size);
        return m == null ? $"0x{a:X}" : $"{m.Name}+0x{a - m.Base:X}";
    }

    /// <summary>Full --verify report as text lines (Dutch).</summary>
    public static (List<string> lines, bool allOk) Report(GameDef g, IProcessMemory mem, IReadOnlyDictionary<string, string>? files, string? fileVersion)
    {
        var L = new List<string> { $"{Branding.Name} verify: {g.Name} ({g.Id})" };
        if (files != null)
            foreach (var (n, f) in files)
            {
                L.Add($"Module {n}: {f}");
                try
                {
                    var fp = FileFingerprint.Compute(f);
                    L.Add($"  vingerafdruk: {fp}");
                    if (n.Equals(g.MainModule, StringComparison.OrdinalIgnoreCase))
                    {
                        if (fileVersion != null) L.Add($"  fileVersion: {fileVersion}");
                        L.Add($"  supportedVersions: {fp.ToJson("<label>", fileVersion)}");
                        var match = g.SupportedVersions.FirstOrDefault(v => v.FileSize != null && v.FileSize == fp.Size && (v.HeadSha256 == null || v.HeadSha256.Equals(fp.HeadSha256, StringComparison.OrdinalIgnoreCase)));
                        L.Add(match != null ? $"  versie: OK ({match.Label})" : g.SupportedVersions.Any(v => v.FileSize != null) ? "  versie: ANDERS dan ondersteund" : "  versie: nog geen vingerafdruk in game.json");
                    }
                }
                catch (Exception e) { L.Add("  vingerafdruk mislukt: " + e.Message); }
            }
        var v = new Verifier();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        v.Run(g, mem);
        foreach (var r in v.Results)
            L.Add($"[{(r.Ok ? "OK  " : "FOUT")}] {r.CheatId}/{r.Site}: {r.Detail}{(r.Ok && r.Rva != null && !r.Detail.Contains("statisch") ? " @ " + r.Rva : "")}");
        foreach (var c in g.Cheats.Where(c => c.Impl.Type == "pointer" && c.Impl.Base is JsonElement b && b.ValueKind == JsonValueKind.String))
            L.Add($"[INFO] {c.Id}: pointer via {c.Impl.Base!.Value.GetString()} (alleen live te controleren)");
        L.Add("");
        L.Add($"RESULTAAT: {v.Ok}/{v.Results.Count} sites uniek gevonden, {v.Failed} fout ({sw.Elapsed.TotalSeconds:0.0} s)");
        if (v.Results.Count > 0 && v.Ok == 0)
            L.Add("Alle patronen 0 treffers: de code is mogelijk versleuteld op schijf, of het is een andere build. Probeer: --verify " + g.Id + " --live (game in het hoofdmenu).");
        return (L, v.Failed == 0);
    }
}
