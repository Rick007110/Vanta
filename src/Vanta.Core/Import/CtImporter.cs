using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vanta.Core.Engine;

namespace Vanta.Core.Import;

public sealed class ImportOptions { public string? Id, Name, Process; public long? SteamAppId; }

public sealed class ImportResult
{
    public GameDef Game { get; init; } = new();
    public List<(string entry, string status, string detail)> Items { get; } = new();
    public int Converted => Items.Count(i => i.status == "ok");
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Import: {Game.Name} ({Game.Id})");
        sb.AppendLine($"Omgezet: {Converted} / {Items.Count} entries");
        foreach (var (e, s, d) in Items) sb.AppendLine($"  [{(s == "ok" ? "OK " : s == "skip" ? "-- " : "!! ")}] {e}{(d.Length > 0 ? ": " + d : "")}");
        return sb.ToString();
    }
}

/// <summary>
/// Converts Cheat Engine .CT tables to game.json as far as possible:
/// AA scripts with aobscanmodule/aobscan + alloc/label/registersymbol + code caves (jmp/nop) or byte patches,
/// and pointer memory records (Module+offset / registered symbol + offsets). Lua and advanced AA are flagged.
/// </summary>
public static class CtImporter
{
    private sealed class Script
    {
        public Dictionary<string, (string module, string aob)> Scans = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Defines = new(StringComparer.OrdinalIgnoreCase);
        public List<(string name, int size, string? near)> Allocs = new();
        public List<string> Registered = new();
        public Dictionary<string, List<string>> Blocks = new(StringComparer.OrdinalIgnoreCase);  // "site" or "site+off" or cave label -> lines
        public List<string> BlockOrder = new();
        public Dictionary<string, byte[]> DisableBytes = new(StringComparer.OrdinalIgnoreCase);
    }

    public static ImportResult Import(string ctXml, string fallbackName, ImportOptions? opt = null)
    {
        opt ??= new ImportOptions();
        var doc = XDocument.Parse(ctXml);
        var entries = doc.Descendants("CheatEntry").ToList();
        var name = opt.Name ?? Regex.Replace(fallbackName, @"[_\-]+", " ").Trim();
        var game = new GameDef
        {
            Id = opt.Id ?? Slug(name), Name = name, SteamAppId = opt.SteamAppId, AntiCheat = false,
            Author = "CE-import", Source = fallbackName + ".CT",
            Notes = new() { "Automatisch geïmporteerd uit een Cheat Engine-tabel; alle cheats zijn ongetest in Vanta." },
            SupportedVersions = new() { new VersionDef { Label = "onbekend (import)" } },
        };
        var res = new ImportResult { Game = game };
        var symbolOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modules = new List<string>();
        var ids = new HashSet<string>();
        var pending = new List<(XElement e, string desc, string section)>();

        foreach (var e in entries)
        {
            var desc = Clean((string?)e.Element("Description") ?? "entry");
            var section = SectionFor(e, desc);
            var aa = e.Element("AssemblerScript");
            if (aa != null)
            {
                var src = WebUtility.HtmlDecode(aa.Value);
                var id = UniqueId(desc, ids);
                try
                {
                    var (cheat, sc) = ConvertScript(src, id, desc, section, symbolOwner);
                    cheat.Hotkey = Hotkey(e);
                    game.Cheats.Add(cheat);
                    foreach (var s in cheat.Impl.Exports ?? new()) symbolOwner[s] = id;
                    foreach (var m in sc.Scans.Values.Select(v => v.module).Where(m => m.Length > 0)) modules.Add(m);
                    res.Items.Add((desc, "ok", cheat.Impl.Type));
                }
                catch (ImportException ex) { ids.Remove(id); res.Items.Add((desc, "fail", ex.Message)); }
                continue;
            }
            var addr = (string?)e.Element("Address");
            if (addr != null) pending.Add((e, desc, section));
            else res.Items.Add((desc, "skip", "groep/notitie"));
        }
        // pointer records after scripts so registered symbols are known
        foreach (var (e, desc, section) in pending)
        {
            try
            {
                var c = ConvertPointer(e, desc, section, UniqueId(desc, ids), symbolOwner);
                game.Cheats.Add(c);
                res.Items.Add((desc, "ok", "pointer"));
            }
            catch (ImportException ex) { res.Items.Add((desc, "fail", ex.Message)); }
        }
        var mod = opt.Process ?? modules.GroupBy(m => m, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(m => m.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        game.ProcessNames = mod != null ? new() { mod } : new() { "onbekend.exe" };
        game.Module = mod;
        game.Short = new string(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])).ToArray());
        return res;
    }

    private sealed class ImportException : Exception { public ImportException(string m) : base(m) { } }

    private static string Clean(string s) => Regex.Replace(s.Trim().Trim('"'), @"\s+", " ");
    private static string Slug(string s)
    {
        var t = Regex.Replace(s.ToLowerInvariant().Normalize(NormalizationForm.FormD), @"[^a-z0-9]+", "-").Trim('-');
        if (t.Length < 2) t = "game-" + t;
        return t.Length > 60 ? t[..60].Trim('-') : t;
    }
    private static string UniqueId(string desc, HashSet<string> ids)
    {
        var b = Regex.Replace(desc.ToLowerInvariant(), @"\([^)]*\)", "");
        b = Regex.Replace(b, @"[^a-z0-9]+", "_").Trim('_');
        if (b.Length < 2) b = "cheat";
        if (b.Length > 40) b = b[..40].Trim('_');
        var id = b; int n = 2;
        while (!ids.Add(id)) id = $"{b}_{n++}";
        return id;
    }

    private static string SectionFor(XElement e, string desc)
    {
        var text = (desc + " " + string.Join(" ", e.Ancestors("CheatEntry").Select(a => (string?)a.Element("Description") ?? ""))).ToLowerInvariant();
        if (Regex.IsMatch(text, @"ammo|weapon|grenade|munitie|wapen|gun|bullet|charge")) return "wapens";
        if (Regex.IsMatch(text, @"\bxp\b|exp|skill|point|punt|level|enhancer|money|geld|credit")) return "punten";
        if (Regex.IsMatch(text, @"health|hp\b|stamina|energy|battery|oxygen|weight|gewicht|player|speler|hunger|thirst")) return "speler";
        return "extra";
    }

    private static readonly Dictionary<int, string> Vk = BuildVk();
    private static Dictionary<int, string> BuildVk()
    {
        var d = new Dictionary<int, string> { [16] = "Shift", [17] = "Ctrl", [18] = "Alt", [107] = "Numpad+", [109] = "Numpad-", [106] = "Numpad*", [111] = "Numpad/", [110] = "Numpad.",
            [45] = "Insert", [46] = "Delete", [36] = "Home", [35] = "End", [33] = "PageUp", [34] = "PageDown", [38] = "Up", [40] = "Down", [37] = "Left", [39] = "Right", [32] = "Space", [9] = "Tab", [19] = "Pause" };
        for (int i = 0; i < 24; i++) d[112 + i] = "F" + (i + 1);
        for (int i = 0; i < 10; i++) { d[96 + i] = "Numpad" + i; d[48 + i] = i.ToString(); }
        for (int i = 0; i < 26; i++) d[65 + i] = ((char)('A' + i)).ToString();
        return d;
    }
    private static string? Hotkey(XElement e)
    {
        var hk = e.Element("Hotkeys")?.Elements("Hotkey").FirstOrDefault(h => ((string?)h.Element("Action") ?? "").Contains("Toggle", StringComparison.OrdinalIgnoreCase));
        if (hk == null) return null;
        var keys = hk.Element("Keys")?.Elements("Key").Select(k => int.TryParse(k.Value, out var v) ? v : -1).ToList() ?? new();
        if (keys.Count == 0 || keys.Any(k => !Vk.ContainsKey(k))) return null;
        var mods = keys.Where(k => k is 16 or 17 or 18).OrderBy(k => k == 17 ? 0 : k == 18 ? 1 : 2).Select(k => Vk[k]);
        var main = keys.Where(k => k is not (16 or 17 or 18)).Select(k => Vk[k]).ToList();
        if (main.Count != 1) return null;
        return string.Join("+", mods.Append(main[0]));
    }

    private static readonly Regex Cmd = new(@"^\s*(aobscanmodule|aobscan|alloc|globalalloc|label|registersymbol|unregistersymbol|dealloc|define)\s*\((.*)\)\s*$", RegexOptions.IgnoreCase);

    private static (CheatDef, Script) ConvertScript(string src, string id, string desc, string section, Dictionary<string, string> known)
    {
        if (Regex.IsMatch(src, @"\{\$lua\}", RegexOptions.IgnoreCase)) throw new ImportException("Lua-script: niet automatisch omzetbaar");
        foreach (var bad in new[] { "readmem", "reassemble", "aobscanregion", "createthread", "loadlibrary", "luacall", "{$try}", "fullaccess", "assert(" })
            if (src.Contains(bad, StringComparison.OrdinalIgnoreCase)) throw new ImportException($"'{bad}' wordt niet ondersteund");
        var m = Regex.Match(src, @"\[ENABLE\](.*?)\[DISABLE\](.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) throw new ImportException("geen [ENABLE]/[DISABLE]");
        var sc = new Script();
        string? cur = null;
        foreach (var raw in Lines(m.Groups[1].Value))
        {
            var line = ApplyDefines(raw, sc.Defines);
            var c = Cmd.Match(line);
            if (c.Success)
            {
                var args = c.Groups[2].Value.Split(',').Select(a => a.Trim()).ToList();
                switch (c.Groups[1].Value.ToLowerInvariant())
                {
                    case "aobscanmodule": sc.Scans[args[0]] = (args[1], string.Join(" ", args.Skip(2))); break;
                    case "aobscan": sc.Scans[args[0]] = ("", string.Join(" ", args.Skip(1))); break;
                    case "alloc": case "globalalloc":
                        sc.Allocs.Add((args[0], args.Count > 1 && MiniAssembler.TryNumber(args[1], out var sz) ? (int)sz : 0x1000, args.Count > 2 ? args[2] : null)); break;
                    case "registersymbol": sc.Registered.AddRange(args); break;
                    case "define": sc.Defines[args[0]] = args.Count > 1 ? args[1] : ""; break;
                }
                continue;
            }
            var lm = Regex.Match(line, @"^\s*([A-Za-z_][\w]*(\s*\+\s*[0-9A-Fa-f]+)?)\s*:\s*(.*)$");
            if (lm.Success && !Regex.IsMatch(line, @"^\s*(byte|word|dword|qword)\s+ptr", RegexOptions.IgnoreCase))
            {
                var label = Regex.Replace(lm.Groups[1].Value, @"\s", "");
                var baseName = label.Split('+')[0];
                bool isBlockStart = sc.Scans.ContainsKey(baseName) || sc.Allocs.Any(a => a.name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
                if (isBlockStart) { cur = label; if (!sc.Blocks.ContainsKey(cur)) { sc.Blocks[cur] = new(); sc.BlockOrder.Add(cur); } }
                else { if (cur == null) throw new ImportException($"label '{label}' buiten een blok"); sc.Blocks[cur].Add(label + ":"); }
                if (lm.Groups[3].Value.Trim().Length > 0) sc.Blocks[cur!].Add(lm.Groups[3].Value.Trim());
                continue;
            }
            if (cur == null) { if (line.Trim().Length > 0) throw new ImportException($"code buiten een blok: '{line.Trim()}'"); continue; }
            sc.Blocks[cur].Add(line.Trim());
        }
        // [DISABLE]: original bytes per site (db lines)
        string? dcur = null;
        foreach (var raw in Lines(m.Groups[2].Value))
        {
            var line = ApplyDefines(raw, sc.Defines);
            var lm = Regex.Match(line, @"^\s*([A-Za-z_][\w]*(\s*\+\s*[0-9A-Fa-f]+)?)\s*:\s*$");
            if (lm.Success) { dcur = Regex.Replace(lm.Groups[1].Value, @"\s", ""); continue; }
            var db = Regex.Match(line, @"^\s*db\s+([0-9A-Fa-f ]+)$", RegexOptions.IgnoreCase);
            if (db.Success && dcur != null)
            {
                var bytes = db.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => byte.Parse(b, NumberStyles.HexNumber)).ToArray();
                sc.DisableBytes[dcur] = sc.DisableBytes.TryGetValue(dcur, out var prev) ? prev.Concat(bytes).ToArray() : bytes;
            }
        }
        if (sc.Scans.Count == 0) throw new ImportException("geen aobscanmodule/aobscan (vaste adressen worden niet ondersteund)");

        var impl = new ImplDef { Sites = new(), Patches = new(), Hooks = new(), Asm = new(), Exports = new() };
        List<string>? crossRequires = null;
        var siteOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, (module, aob)) in sc.Scans)
        {
            var sid = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]", "_");
            siteOf[name] = sid;
            impl.Sites[sid] = new SiteDef { Patterns = new() { new PatternDef { Aob = NormAob(aob) } } };
            if (module.Length > 0 && impl.Module == null) impl.Module = module;
        }
        var caveNames = new HashSet<string>(sc.Allocs.Select(a => a.name), StringComparer.OrdinalIgnoreCase);
        var retRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var caveLines = new List<string>();
        foreach (var blk in sc.BlockOrder)
        {
            var lines = sc.Blocks[blk];
            var parts = blk.Split('+');
            var baseName = parts[0];
            long off = parts.Length > 1 ? long.Parse(parts[1], NumberStyles.HexNumber) : 0;
            if (caveNames.Contains(baseName))
            {
                if (off != 0) throw new ImportException("code op alloc+offset wordt niet ondersteund");
                if (caveLines.Count > 0) caveLines.Add("  db " + string.Join(" ", Enumerable.Repeat("CC", 16)));   // separator between alloc blocks
                caveLines.Add(baseName + ":"); caveLines.AddRange(lines); continue;
            }
            var sid = siteOf[baseName];
            var site = impl.Sites[sid];
            // hook: "jmp <cave label>" + nops, then "<ret>:" label
            var first = lines.FirstOrDefault(l => l.Length > 0);
            var jm = first == null ? Match.Empty : Regex.Match(first, @"^(jmp|call)\s+([A-Za-z_]\w*)$", RegexOptions.IgnoreCase);
            if (jm.Success && caveNames.Count > 0)
            {
                if (off != 0)
                {
                    // hook at site+offset: separate site with shifted pattern offsets
                    var nsid = $"{sid}_{off:x}";
                    impl.Sites[nsid] = new SiteDef { Patterns = site.Patterns.Select(p => new PatternDef { Aob = p.Aob, Offset = p.Offset + off }).ToList() };
                    sid = nsid; site = impl.Sites[nsid];
                    siteOf[blk] = nsid;
                }
                if (jm.Groups[1].Value.Equals("call", StringComparison.OrdinalIgnoreCase)) throw new ImportException("call-hook wordt niet ondersteund");
                int len = 5; string? ret = null;
                foreach (var l in lines.Skip(lines.IndexOf(first!) + 1))
                {
                    var nm = Regex.Match(l, @"^nop(\s+([0-9A-Fa-f]+))?$", RegexOptions.IgnoreCase);
                    var dbm = Regex.Match(l, @"^db\s+((90\s*)+)$", RegexOptions.IgnoreCase);
                    if (nm.Success) len += nm.Groups[2].Success ? int.Parse(nm.Groups[2].Value, NumberStyles.HexNumber) : 1;
                    else if (dbm.Success) len += dbm.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    else if (Regex.IsMatch(l, @"^[A-Za-z_]\w*:$")) { ret = l.TrimEnd(':'); break; }
                    else throw new ImportException($"onverwachte regel na hook-jmp: '{l}'");
                }
                if (sc.DisableBytes.TryGetValue(blk, out var ob) && ob.Length != len)
                    len = Math.Max(len, ob.Length);
                site.Overwrite = len;
                if (ret != null) retRenames[ret] = sid + "_ret";
                retRenames.TryAdd(blk.Replace("+", "_plus_"), sid);
                impl.Hooks.Add(new HookDef { Site = sid, Label = jm.Groups[2].Value });
                continue;
            }
            // byte patch: db / nop / simple instructions without labels
            var bytes = new List<byte>();
            foreach (var l in lines.Where(l => l.Length > 0))
            {
                if (Regex.IsMatch(l, @"^[A-Za-z_]\w*:$")) throw new ImportException($"label in patch-blok '{blk}'");
                try { bytes.AddRange(MiniAssembler.Assemble(new[] { l }, 0x140000000UL + (ulong)off + (ulong)bytes.Count).Code); }
                catch (AsmException e) { throw new ImportException($"patch '{l}': {e.Message}"); }
            }
            if (bytes.Count == 0) continue;
            impl.Patches.Add(new PatchDef { Site = sid, Offset = off, Bytes = AobScanner.ToHex(bytes.ToArray()) });
        }
        // drop sites that are only used as base for a shifted hook site
        var used = new HashSet<string>(impl.Hooks.Select(h => h.Site).Concat(impl.Patches.Select(p => p.Site)), StringComparer.OrdinalIgnoreCase);
        foreach (var k in impl.Sites.Keys.ToList())
            if (!used.Contains(k) && !(impl.Asm ?? new()).Any(l => Regex.IsMatch(l, $@"\b{Regex.Escape(k)}\b")) && !caveLines.Any(l => Regex.IsMatch(RenameWords(l, retRenames, siteOf), $@"\b{Regex.Escape(k)}\b")))
                impl.Sites.Remove(k);
        if (impl.Hooks.Count > 0)
        {
            impl.Type = "aobInject";
            var near = sc.Allocs.Select(a => a.near).FirstOrDefault(n => n != null && siteOf.ContainsKey(n));
            var nearSite = near != null && impl.Sites.ContainsKey(siteOf[near]) ? siteOf[near] : impl.Hooks[0].Site;
            impl.Alloc = new AllocDef { Size = Math.Clamp(sc.Allocs.Sum(a => Math.Max(a.size, 16)) + 64 * sc.Allocs.Count, 16, 65536), Near = nearSite };
            // rename: site names -> site ids, return labels -> <site>_ret
            var asm = caveLines.Select(l => RenameWords(l, retRenames, siteOf)).ToList();
            impl.Asm = asm;
            foreach (var r in sc.Registered)
                if (Regex.IsMatch(string.Join("\n", asm), $@"^{Regex.Escape(r)}:", RegexOptions.Multiline | RegexOptions.IgnoreCase)) impl.Exports.Add(r);
            // symbols from other scripts (CE "activate me first" tables) -> requires
            var text = string.Join("\n", asm);
            var reqs = known.Where(k => Regex.IsMatch(text, $@"\b{Regex.Escape(k.Key)}\b", RegexOptions.IgnoreCase)).Select(k => k.Value).Distinct().ToList();
            if (reqs.Count > 0) crossRequires = reqs;
            // verify it assembles
            var err = TryAssemble(impl, known.Keys);
            if (err != null) throw new ImportException("asm: " + err);
        }
        else if (impl.Patches.Count > 0) { impl.Type = "aobPatch"; impl.Asm = null; impl.Hooks = null; impl.Alloc = null; }
        else throw new ImportException("geen hook of patch gevonden");
        if (impl.Exports.Count == 0) impl.Exports = null;
        if (impl.Patches?.Count == 0) impl.Patches = null;
        var cheat = new CheatDef
        {
            Id = id, Name = Regex.Replace(desc, @"\s*\((F\d+|Ctrl[^)]*)\)", "").Trim(), Section = section, Type = "toggle", Icon = IconFor(desc),
            Confidence = "untested", ConfidenceNote = "Geïmporteerd uit CE-tabel; ongetest.", Impl = impl, Requires = crossRequires,
            Hint = crossRequires != null ? "Activeer eerst: " + string.Join(", ", crossRequires) : null,
        };
        return (cheat, sc);
    }

    private static string? TryAssemble(ImplDef impl, IEnumerable<string> extra)
    {
        var sites = impl.Sites!;
        var addrs = sites.Keys.Select((k, i) => (k, a: 0x140001000UL + (ulong)i * 0x1000)).ToDictionary(x => x.k, x => x.a);
        var ext = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { ["cave"] = 0x13FFF0000 };
        foreach (var (k, a) in addrs) { ext[k] = a; ext[k + "_ret"] = a + (ulong)sites[k].Overwrite; }
        ulong fake = 0x13FFE0000;
        foreach (var x in extra) ext.TryAdd(x, fake += 0x100);
        foreach (Match m in Regex.Matches(string.Join("\n", impl.Asm!), @"\b[\w.]+\.(?:dll|exe)\b", RegexOptions.IgnoreCase)) ext.TryAdd(m.Value, 0x140000000);
        try { MiniAssembler.Assemble(impl.Asm!, 0x13FFF0000, ext); return null; }
        catch (AsmException e) { return e.Message; }
    }

    private static string RenameWords(string line, Dictionary<string, string> a, Dictionary<string, string> b) =>
        Regex.Replace(line, @"\b[A-Za-z_]\w*\b", w => a.TryGetValue(w.Value, out var r) ? r : b.TryGetValue(w.Value, out var s) ? s : w.Value);

    private static string ApplyDefines(string line, Dictionary<string, string> d) =>
        d.Count == 0 ? line : Regex.Replace(line, @"\b[A-Za-z_]\w*\b", w => d.TryGetValue(w.Value, out var v) ? v : w.Value);

    private static IEnumerable<string> Lines(string s) =>
        s.Split('\n').Select(l => Regex.Replace(Regex.Replace(l, @"//.*$", ""), @"\{[^}]*\}", "").TrimEnd('\r').TrimEnd()).Where(l => l.Trim().Length > 0);

    private static string NormAob(string aob)
    {
        var t = aob.Trim().Replace(",", " ");
        if (!t.Contains(' ') && t.Length % 2 == 0) t = string.Join(" ", Enumerable.Range(0, t.Length / 2).Select(i => t.Substring(i * 2, 2)));
        return Regex.Replace(t.ToUpperInvariant(), @"\s+", " ").Replace("*", "??").Replace(" ? ", " ?? ");
    }

    private static string IconFor(string d)
    {
        var s = d.ToLowerInvariant();
        return s switch
        {
            _ when s.Contains("health") || s.Contains("hp") => "heart",
            _ when s.Contains("ammo") => "ammo",
            _ when s.Contains("grenade") => "grenade",
            _ when s.Contains("battery") || s.Contains("energy") => "battery",
            _ when s.Contains("weight") => "weight",
            _ when s.Contains("jump") => "jump",
            _ when s.Contains("build") => "hammer",
            _ when s.Contains("fuel") || s.Contains("petrol") => "fuel",
            _ when s.Contains("craft") => "cube",
            _ when s.Contains("xp") || s.Contains("exp") => "bolt",
            _ when s.Contains("dupe") || s.Contains("dup") => "copy",
            _ => "sparkles",
        };
    }

    private static CheatDef ConvertPointer(XElement e, string desc, string section, string id, Dictionary<string, string> symbolOwner)
    {
        var addr = ((string?)e.Element("Address") ?? "").Trim();
        var vt = ((string?)e.Element("VariableType") ?? "").Trim();
        string valueType = vt switch
        {
            "4 Bytes" => "int32", "8 Bytes" => "int64", "Float" => "float", "Double" => "double", "Byte" => "byte", "2 Bytes" => "int16",
            _ => throw new ImportException($"type '{vt}' niet ondersteund"),
        };
        var offsets = e.Element("Offsets")?.Elements("Offset").Select(o => MiniAssembler.TryNumber(o.Value.Trim(), out var ov) ? ov : throw new ImportException($"offset '{o.Value}' ongeldig")).Reverse().ToList();
        string baseStr; List<string>? requires = null;
        var mm = Regex.Match(addr, @"^""?([^""+]+\.(exe|dll))""?\s*\+\s*([0-9A-Fa-f]+)$", RegexOptions.IgnoreCase);
        var sm = Regex.Match(addr, @"^([A-Za-z_]\w*)\s*(\+\s*([0-9A-Fa-f]+))?$");
        var dm = Regex.Match(addr, @"^\[([A-Za-z_]\w*)\]\s*(\+\s*([0-9A-Fa-f]+))?$");
        if (dm.Success)
        {
            if (!symbolOwner.TryGetValue(dm.Groups[1].Value, out var own)) throw new ImportException($"symbool '{dm.Groups[1].Value}' komt niet uit een omgezet script");
            offsets ??= new();
            offsets.Insert(0, dm.Groups[3].Success ? long.Parse(dm.Groups[3].Value, NumberStyles.HexNumber) : 0);
            baseStr = "sym:" + dm.Groups[1].Value; requires = new() { own };
        }
        else if (mm.Success) baseStr = $"{mm.Groups[1].Value}+0x{mm.Groups[3].Value.ToUpperInvariant()}";
        else if (sm.Success && symbolOwner.TryGetValue(sm.Groups[1].Value, out var owner))
        {
            baseStr = "sym:" + sm.Groups[1].Value + (sm.Groups[3].Success ? "+0x" + sm.Groups[3].Value.ToUpperInvariant() : "");
            requires = new() { owner };
        }
        else if (sm.Success) throw new ImportException($"symbool '{sm.Groups[1].Value}' komt niet uit een omgezet script");
        else throw new ImportException($"adres '{addr}' niet ondersteund");
        return new CheatDef
        {
            Id = id, Name = desc.Length > 60 ? desc[..60] : desc, Section = section, Type = "number", Icon = IconFor(desc),
            Min = valueType is "float" or "double" ? -1e9 : 0, Max = valueType == "byte" ? 255 : 1e9, Step = 1, Requires = requires,
            Hint = requires != null ? "Activeer eerst het bijbehorende script" : null,
            Confidence = "untested", ConfidenceNote = "Geïmporteerd uit CE-tabel; ongetest.",
            Impl = new ImplDef { Type = "pointer", Base = JsonDocument.Parse(JsonSerializer.Serialize(baseStr)).RootElement.Clone(), Offsets = offsets is { Count: > 0 } ? offsets : null, ValueType = valueType },
        };
    }
}
