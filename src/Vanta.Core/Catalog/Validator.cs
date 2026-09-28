using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Vanta.Core.Engine;

namespace Vanta.Core.Catalog;

public sealed record Finding(string Level, string Path, string Message)
{
    public override string ToString() => $"{Level.ToUpperInvariant(),-5} {Path}: {Message}";
}

/// <summary>JSON Schema (schema/game.schema.json, embedded) + semantic checks (AOBs parse, asm assembles, references exist).</summary>
public static class GameValidator
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        using var s = typeof(GameValidator).Assembly.GetManifestResourceStream("game.schema.json")!;
        using var r = new StreamReader(s);
        return JsonSchema.FromText(r.ReadToEnd());
    });

    public static readonly HashSet<string> KnownIcons = new(StringComparer.Ordinal)
    {
        "battery","heart","weight","ammo","grenade","bolt","multiply","gem","jump","hammer","fuel","cube","copy","user","crosshair","star","sparkles",
        "shield","clock","coin","speed","eye","key","skull","car","map","box","flask","snow","fire","wand","wrench","zap","target","plus","minus","refresh","link",
    };

    public static List<Finding> ValidateFile(string file)
    {
        var text = File.ReadAllText(file);
        var list = ValidateJson(text);
        if (list.Any(f => f.Level == "error" && f.Path == "$")) return list;
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file)));
        try
        {
            var g = JsonSerializer.Deserialize<GameDef>(text, Json.Options)!;
            if (folder != null && folder != g.Id && folder != "games") list.Add(new Finding("warn", "id", $"folder name '{folder}' differs from id '{g.Id}'"));
        }
        catch { }
        return list;
    }

    public static List<Finding> ValidateJson(string text)
    {
        var list = new List<Finding>();
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { list.Add(new Finding("error", "$", "not valid JSON: " + e.Message)); return list; }
        var res = Schema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!res.IsValid)
            foreach (var d in res.Details.Where(d => d.HasErrors && !IsBranchNoise(d.EvaluationPath.ToString())))
                foreach (var (k, v) in d.Errors!)
                    list.Add(new Finding("error", d.InstanceLocation.ToString() is { Length: > 0 } p ? p : "$", $"schema ({k}): {v}"));
        GameDef g;
        try { g = JsonSerializer.Deserialize<GameDef>(text, Json.Options)!; }
        catch (Exception e) { list.Add(new Finding("error", "$", "cannot load: " + e.Message)); return list; }
        list.AddRange(Semantic(g));
        // de-duplicate (schema output can repeat)
        return list.Distinct().ToList();
    }

    // errors inside "if" conditions or individual oneOf/anyOf branches are not failures by themselves
    private static bool IsBranchNoise(string evalPath) => evalPath.Contains("/if") || Regex.IsMatch(evalPath, @"/(oneOf|anyOf)/\d+");

    public static List<Finding> Semantic(GameDef g)
    {
        var f = new List<Finding>();
        void E(string p, string m) => f.Add(new Finding("error", p, m));
        void W(string p, string m) => f.Add(new Finding("warn", p, m));
        if (g.Blocked) f.Add(new Finding("info", "antiCheat", "online/anti-cheat: Vanta refuses to attach (listed in the catalog for information only)"));
        if (g.SupportedVersions.Count == 0) W("supportedVersions", "no supported version given");
        else if (!g.SupportedVersions.Any(v => v.FileVersion != null || v.PeTimestamp != null || v.ModuleSize != null || v.ProductVersion != null || v.FileSize != null || v.HeadSha256 != null))
            f.Add(new Finding("info", "supportedVersions", "no fingerprint (fileVersion/peTimestamp/moduleSize/fileSize): the version is not checked"));
        var ids = new HashSet<string>();
        var hotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < g.Cheats.Count; i++)
        {
            var c = g.Cheats[i];
            var p = $"cheats[{i}]({c.Id})";
            if (!ids.Add(c.Id)) E(p, "duplicate id");
            foreach (var hk in new[] { c.Hotkey, c.HotkeyInc, c.HotkeyDec }.Where(h => h != null))
            {
                if (c.Hidden) W(p, "hidden cheat with a hotkey");
                if (hotkeys.TryGetValue(hk!, out var other)) W(p, $"hotkey {hk} also used by {other}");
                else hotkeys[hk!] = c.Id;
            }
            if (c.Icon != null && !KnownIcons.Contains(c.Icon)) W(p, $"unknown icon '{c.Icon}'");
            if (c.Confidence == "broken" && c.AutoEnable) W(p, "a cheat with confidence 'broken' cannot be autoEnable (ignored)");
            if (c.Confidence == "broken" && g.Cheats.Any(x => x.Requires?.Contains(c.Id) == true && x.Confidence != "broken"))
                W(p, "other cheats require this 'broken' cheat");
            var impl = c.Impl;
            switch (c.Type)
            {
                case "number" or "slider":
                    if (impl.Type != "pointer") E(p, "number/slider requires impl.type pointer");
                    if (c.Min.HasValue && c.Max.HasValue && c.Min >= c.Max) E(p, "min must be smaller than max");
                    if (c.Type == "slider" && (!c.Min.HasValue || !c.Max.HasValue)) E(p, "slider requires min and max");
                    break;
                case "button":
                    if (impl.Type != "pointer" || impl.Action == null) E(p, "button requires pointer + action (set/add)");
                    break;
            }
            try { CheckImpl(c, p, E, W, g.Cheats.SelectMany(x => x.Impl.Exports ?? new()).ToList()); }
            catch (Exception ex) { E(p, ex.Message); }
        }
        foreach (var c in g.Cheats)
            foreach (var r in c.Requires ?? new())
                if (!ids.Contains(r)) E($"cheats({c.Id}).requires", $"'{r}' does not exist");
                else if (r == c.Id) E($"cheats({c.Id}).requires", "vereist zichzelf");
        foreach (var c in g.Cheats.Where(c => c.Impl.Type == "pointer" && c.Impl.Base is { ValueKind: JsonValueKind.String } b && b.GetString()!.StartsWith("sym:")))
        {
            var sym = Regex.Match(c.Impl.Base!.Value.GetString()!, @"^sym:(\w+)").Groups[1].Value;
            var exporters = g.Cheats.Where(x => x.Impl.Exports?.Contains(sym) == true).Select(x => x.Id).ToList();
            if (exporters.Count == 0) E($"cheats({c.Id}).impl.base", $"symbol '{sym}' is not exported by any cheat");
            else if (!(c.Requires ?? new()).Intersect(exporters).Any()) W($"cheats({c.Id}).requires", $"uses '{sym}' but does not require {string.Join("/", exporters)}");
        }
        return f;
    }

    private static void CheckImpl(CheatDef c, string p, Action<string, string> E, Action<string, string> W, List<string> gameExports)
    {
        var impl = c.Impl;
        var sites = impl.Sites ?? new();
        foreach (var (name, s) in sites)
        {
            if (s.Patterns.Count == 0) E($"{p}.sites.{name}", "no patterns");
            foreach (var pat in s.Patterns)
            {
                try
                {
                    var ap = new AobPattern(pat.Aob);
                    if (ap.Mask.Count(m => m == 0xFF) < 5) W($"{p}.sites.{name}", $"AOB '{pat.Aob}' is very short; likely to match several times");
                }
                catch (FormatException e) { E($"{p}.sites.{name}", e.Message); }
            }
            if (s.Expect != null) try { _ = new AobPattern(s.Expect); } catch (FormatException e) { E($"{p}.sites.{name}.expect", e.Message); }
        }
        if (impl.FreezeFrom != null || impl.Mirror != null || impl.Restore.HasValue)
        {
            if (impl.Type != "pointer") E(p, "freezeFrom/mirror/restore only apply to pointer cheats");
            if (impl.FreezeFrom != null && (c.Type != "toggle" || !impl.Freeze)) E($"{p}.freezeFrom", "freezeFrom requires a toggle with freeze: true");
            if (impl.FreezeFrom is { Base: null, Offsets: null or { Count: 0 } }) E($"{p}.freezeFrom", "freezeFrom needs offsets or its own base");
            if (impl.FreezeFrom?.Base is { ValueKind: JsonValueKind.Object } fb && !fb.TryGetProperty("aob", out _)) E($"{p}.freezeFrom.base", "object base needs aob");
        }
        if (impl.Type == "pointer" && impl.Base is { ValueKind: JsonValueKind.Object } ob && ob.TryGetProperty("aob", out var obAob))
            try { _ = new AobPattern(obAob.GetString()!); } catch (FormatException e) { E($"{p}.base", e.Message); }
        foreach (var pt in impl.Patches ?? new())
        {
            if (!sites.ContainsKey(pt.Site)) E($"{p}.patches", $"site '{pt.Site}' does not exist");
            try { AobScanner.ParseHex(pt.Bytes); } catch (FormatException e) { E($"{p}.patches", e.Message); }
        }
        if (impl.Type == "aobInject")
        {
            foreach (var h in impl.Hooks ?? new())
            {
                if (!sites.TryGetValue(h.Site, out var s)) { E($"{p}.hooks", $"site '{h.Site}' does not exist"); continue; }
                if (s.Overwrite < 5) E($"{p}.sites.{h.Site}", "overwrite must be at least 5 for a hook (jmp rel32)");
            }
            if (impl.Alloc?.Near != null && !sites.ContainsKey(impl.Alloc.Near)) E($"{p}.alloc.near", $"site '{impl.Alloc.Near}' does not exist");
            // assemble against fake addresses to catch syntax/label errors early
            ulong baseAddr = 0x140001000, cave = 0x13FFF0000;
            var addrs = sites.Keys.Select((k, i) => (k, a: baseAddr + (ulong)i * 0x1000)).ToDictionary(x => x.k, x => x.a);
            var origs = sites.ToDictionary(kv => kv.Key, kv => Enumerable.Range(0, Math.Max(kv.Value.Overwrite, 16)).Select(i => (byte)(0x40 + i)).ToArray());
            var ext = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { ["cave"] = cave };
            foreach (var (k, a) in addrs) { ext[k] = a; ext[k + "_ret"] = a + (ulong)sites[k].Overwrite; }
            ulong fake = 0x13FFE0000;
            foreach (var x in gameExports) ext.TryAdd(x, fake += 0x100);   // symbols exported by other cheats (runtime: requires)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(string.Join("\n", impl.Asm ?? new()), @"\b[\w.]+\.(?:dll|exe)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                ext.TryAdd(m.Value, 0x140000000);                              // "Game.dll+1234" references (module base at runtime)
            try
            {
                var lines = CheatRuntime.ExpandPlaceholders(impl.Asm ?? new(), addrs, origs, sites);
                var res = MiniAssembler.Assemble(lines, cave, ext);
                foreach (var h in impl.Hooks ?? new()) if (!res.Labels.ContainsKey(h.Label)) E($"{p}.hooks", $"label '{h.Label}' not defined in asm");
                foreach (var x in impl.Exports ?? new()) if (!res.Labels.ContainsKey(x)) E($"{p}.exports", $"label '{x}' not defined in asm");
                if (res.Code.Length > (impl.Alloc?.Size ?? 0x1000)) E($"{p}.alloc", "code larger than alloc.size");
            }
            catch (Exception e) when (e is AsmException or CheatException) { E($"{p}.asm", e.Message); }
        }
    }
}
