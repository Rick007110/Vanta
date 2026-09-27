using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vanta.Core.Stores;

/// <summary>A detected installation (or a Steam "owned only" record) of one game in one store.</summary>
public sealed record StoreInstall(string Store, string StoreName, bool Installed, string? InstallDir, string? LaunchUri,
    string? Exe, string? Note = null, string? Build = null, bool PreferDirect = false, bool Protected = false)
{
    public object ToUi() => new { store = Store, storeName = StoreName, installed = Installed, dir = InstallDir, note = Note, build = Build, launch = PreferDirect && Exe != null ? "exe" : LaunchUri };
}

public interface IStoreProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Null when the game def has no id for this store or nothing was found.</summary>
    StoreInstall? Detect(GameDef g, IStoreEnv env);
}

/// <summary>Windows path helpers that behave identically on every OS (store data always uses Windows paths).</summary>
public static class WinPath
{
    public static string Norm(string p) => p.Replace('/', '\\');
    public static string Combine(string a, params string[] parts)
    {
        var s = Norm(a).TrimEnd('\\');
        foreach (var p in parts) s += "\\" + Norm(p).Trim('\\');
        return s;
    }
    public static string? Parent(string? p)
    {
        if (p == null) return null;
        p = Norm(p).TrimEnd('\\');
        var i = p.LastIndexOf('\\');
        return i <= 0 ? null : p[..i];
    }
    public static string FileName(string p) { p = Norm(p); var i = p.LastIndexOf('\\'); return i < 0 ? p : p[(i + 1)..]; }
}

public static class StoreUtil
{
    public static string Combine(string dir, string? rel) => rel == null ? dir : WinPath.Combine(dir, rel);
    public static string NormDir(string d) => d.Replace('/', '\\').TrimEnd('\\');
    /// <summary>The install counts as present when the folder exists and (if the def names one) the launch exe too.</summary>
    public static bool Present(IStoreEnv env, string dir, GameDef g) =>
        env.DirExists(dir) && (g.LaunchExe == null || env.FileExists(Combine(dir, g.LaunchExe)));
    public static string? ExeIn(IStoreEnv env, string dir, GameDef g)
    {
        if (g.LaunchExe != null) { var p = Combine(dir, g.LaunchExe); return env.FileExists(p) ? p : null; }
        return null;
    }
}

// ---------------------------------------------------------------- Steam
public sealed class SteamProvider : IStoreProvider
{
    public string Id => "steam";
    public string DisplayName => "Steam";

    public static string? SteamRoot(IStoreEnv env)
    {
        var p = env.RegValue(@"HKCU\Software\Valve\Steam", "SteamPath") ?? env.RegValue(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (p == null && env.Folder("ProgramFiles(x86)") is string pf) p = WinPath.Combine(pf, "Steam");
        return p == null ? null : StoreUtil.NormDir(p);
    }

    public static List<string> Libraries(IStoreEnv env)
    {
        var libs = new List<string>();
        var root = SteamRoot(env);
        if (root == null) return libs;
        libs.Add(root);
        var vdf = env.ReadText(WinPath.Combine(root, "steamapps", "libraryfolders.vdf"));
        if (vdf != null)
        {
            var kv = Vdf.Parse(vdf);
            if (kv.TryGetValue("libraryfolders", out var lf) && lf is Dictionary<string, object> folders)
                foreach (var (_, v) in folders)
                {
                    if (v is Dictionary<string, object> f && f.TryGetValue("path", out var p) && p is string ps) libs.Add(StoreUtil.NormDir(ps.Replace(@"\\", @"\")));
                    else if (v is string s) libs.Add(StoreUtil.NormDir(s.Replace(@"\\", @"\")));   // very old format: "1" "D:\\Steam"
                }
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public StoreInstall? Detect(GameDef g, IStoreEnv env)
    {
        var appId = g.Stores?.Steam?.AppId ?? (g.Stores == null ? g.SteamAppId : null);
        if (appId == null) return null;
        StoreInstall? ownedOnly = null;
        foreach (var lib in Libraries(env))
        {
            var acf = env.ReadText($@"{lib}\steamapps\appmanifest_{appId}.acf");
            if (acf == null) continue;
            var st = Vdf.Parse(acf).TryGetValue("AppState", out var a) && a is Dictionary<string, object> d ? d : new();
            string Get(string k) => st.TryGetValue(k, out var v) && v is string s ? s : "";
            var dir = $@"{lib}\steamapps\common\{Get("installdir")}";
            long.TryParse(Get("SizeOnDisk"), out var size);
            int.TryParse(Get("StateFlags"), out var flags);
            var build = Get("buildid");
            if (size > 0 && (flags & 4) != 0 && Get("installdir") != "" && StoreUtil.Present(env, dir, g))
                return new StoreInstall(Id, DisplayName, true, dir, $"steam://run/{appId}", StoreUtil.ExeIn(env, dir, g), null, build);
            ownedOnly = new StoreInstall(Id, DisplayName, false, null, $"steam://run/{appId}", null,
                Strings.Get("store.steamStub", build, size), build);
        }
        return ownedOnly;
    }
}

/// <summary>Minimal Valve KeyValues (VDF/ACF) text parser: nested { } objects of quoted strings.</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int i = 0;
        return ParseObject(text, ref i);
    }
    private static Dictionary<string, object> ParseObject(string t, ref int i)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = Token(t, ref i);
            if (key == null || key == "}") return d;
            var val = Token(t, ref i);
            if (val == null) return d;
            if (val == "{") d[key] = ParseObject(t, ref i); else d[key] = val;
        }
    }
    private static string? Token(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) { i++; continue; }
            if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            break;
        }
        if (i >= t.Length) return null;
        if (t[i] is '{' or '}') return t[i++].ToString();
        var sb = new System.Text.StringBuilder();
        if (t[i] == '"')
        {
            i++;
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length) { var n = t[i + 1]; sb.Append(n switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => n }); i += 2; continue; }
                sb.Append(t[i++]);
            }
            i++;
            return sb.ToString();
        }
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not '{' and not '}') sb.Append(t[i++]);
        return sb.ToString();
    }
}

// ---------------------------------------------------------------- Ubisoft Connect
public sealed class UbisoftProvider : IStoreProvider
{
    public const string InstallsKey = @"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
    public string Id => "ubisoft";
    public string DisplayName => "Ubisoft Connect";
    public StoreInstall? Detect(GameDef g, IStoreEnv env)
    {
        var ids = g.Stores?.Ubisoft?.Ids;
        if (ids == null || ids.Count == 0) return null;
        foreach (var id in ids)
        {
            var dir = env.RegValue($@"{InstallsKey}\{id}", "InstallDir");
            if (dir == null) continue;
            dir = StoreUtil.NormDir(dir);
            if (!StoreUtil.Present(env, dir, g)) continue;
            return new StoreInstall(Id, DisplayName, true, dir, $"uplay://launch/{id}/0", StoreUtil.ExeIn(env, dir, g), null, id.ToString());
        }
        return null;
    }
}

// ---------------------------------------------------------------- Epic Games
public sealed class EpicProvider : IStoreProvider
{
    public string Id => "epic";
    public string DisplayName => "Epic Games";
    public StoreInstall? Detect(GameDef g, IStoreEnv env)
    {
        var names = g.Stores?.Epic?.AppNames;
        if (names == null || names.Count == 0) return null;
        var pd = env.Folder("ProgramData");
        if (pd == null) return null;
        foreach (var f in env.Files($@"{pd}\Epic\EpicGamesLauncher\Data\Manifests", "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(env.ReadText(f) ?? "{}");
                var r = doc.RootElement;
                var app = r.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                if (app == null || !names.Contains(app, StringComparer.OrdinalIgnoreCase)) continue;
                var dir = r.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                if (dir == null) continue;
                dir = StoreUtil.NormDir(dir);
                if (!StoreUtil.Present(env, dir, g)) continue;
                var exe = StoreUtil.ExeIn(env, dir, g) ?? (r.TryGetProperty("LaunchExecutable", out var le) && le.GetString() is string ls ? StoreUtil.Combine(dir, ls) : null);
                var ver = r.TryGetProperty("AppVersionString", out var av) ? av.GetString() : null;
                return new StoreInstall(Id, DisplayName, true, dir, $"com.epicgames.launcher://apps/{Uri.EscapeDataString(app)}?action=launch&silent=true", exe, null, ver);
            }
            catch (JsonException) { }
        }
        return null;
    }
}

// ---------------------------------------------------------------- GOG
public sealed class GogProvider : IStoreProvider
{
    public const string GamesKey = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";
    public string Id => "gog";
    public string DisplayName => "GOG";
    public StoreInstall? Detect(GameDef g, IStoreEnv env)
    {
        var ids = g.Stores?.Gog?.Ids;
        if (ids == null || ids.Count == 0) return null;
        foreach (var id in ids)
        {
            var key = $@"{GamesKey}\{id}";
            var dir = env.RegValue(key, "path");
            if (dir == null) continue;
            dir = StoreUtil.NormDir(dir);
            if (!StoreUtil.Present(env, dir, g)) continue;
            var exe = StoreUtil.ExeIn(env, dir, g) ?? env.RegValue(key, "exe");
            if (exe != null && !env.FileExists(exe)) exe = null;
            // GOG games are DRM-free: start the exe directly; Galaxy's URI only opens the game page.
            return new StoreInstall(Id, DisplayName, true, dir, $"goggalaxy://openGameView/{id}", exe, null, env.RegValue(key, "ver"), PreferDirect: exe != null);
        }
        return null;
    }
}

// ---------------------------------------------------------------- EA app / Origin
public sealed class EaProvider : IStoreProvider
{
    public string Id => "ea";
    public string DisplayName => "EA app";
    public StoreInstall? Detect(GameDef g, IStoreEnv env)
    {
        var ea = g.Stores?.Ea;
        if (ea == null || ea.OfferIds.Count == 0) return null;
        var launch = $"origin2://game/launch?offerIds={Uri.EscapeDataString(string.Join(",", ea.OfferIds))}";
        // 1) game-specific registry keys written by the EA installer ("Install Dir")
        foreach (var k in ea.RegistryKeys ?? new())
        {
            var dir = env.RegValue(k.Key, k.Value ?? "Install Dir");
            if (dir == null) continue;
            dir = StoreUtil.NormDir(dir);
            if (StoreUtil.Present(env, dir, g)) return new StoreInstall(Id, DisplayName, true, dir, launch, StoreUtil.ExeIn(env, dir, g));
        }
        // 2) Origin/EA LocalContent manifests (*.mfst: URL-encoded "id=OFFER&dipinstallpath=...")
        var pd = env.Folder("ProgramData");
        if (pd != null)
            foreach (var sub in new[] { "Origin", "EA Desktop" })
            {
                var lc = $@"{pd}\{sub}\LocalContent";
                foreach (var gameDir in DirsUnder(env, lc))
                    foreach (var f in env.Files(gameDir, "*.mfst"))
                    {
                        var q = Uri.UnescapeDataString(env.ReadText(f) ?? "");
                        if (!ea.OfferIds.Any(o => Regex.IsMatch(q, $@"(^|[?&])id={Regex.Escape(o)}(&|$)", RegexOptions.IgnoreCase))) continue;
                        var m = Regex.Match(q, @"dipinstallpath=([^&]+)", RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        var dir = StoreUtil.NormDir(m.Groups[1].Value);
                        if (StoreUtil.Present(env, dir, g)) return new StoreInstall(Id, DisplayName, true, dir, launch, StoreUtil.ExeIn(env, dir, g));
                    }
            }
        return null;
    }
    private static IEnumerable<string> DirsUnder(IStoreEnv env, string dir) => env.SubDirs(dir).Prepend(dir);
}

// ---------------------------------------------------------------- Xbox app / Game Pass (MSIX/UWP packages)
public sealed class XboxProvider : IStoreProvider
{
    public const string PackagesKey = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    public string Id => "xbox";
    public string DisplayName => "Xbox / Game Pass";

    /// <summary>"Name_1.2.3.0_x64__8wekyb3d8bbwe" -> "Name_8wekyb3d8bbwe".</summary>
    public static string? FamilyName(string fullName)
    {
        var p = fullName.Split('_');
        return p.Length >= 5 ? p[0] + "_" + p[^1] : null;
    }

    public StoreInstall? Detect(GameDef g, IStoreEnv env)
    {
        var x = g.Stores?.Xbox;
        if (x == null || string.IsNullOrEmpty(x.PackageFamilyName)) return null;
        foreach (var full in env.RegSubKeys(PackagesKey))
        {
            if (!string.Equals(FamilyName(full), x.PackageFamilyName, StringComparison.OrdinalIgnoreCase)) continue;
            var dir = env.RegValue($@"{PackagesKey}\{full}", "PackageRootFolder");
            if (dir == null) continue;
            dir = StoreUtil.NormDir(dir);
            var appId = x.AppId;
            if (appId == null && env.ReadText(WinPath.Combine(dir, "AppxManifest.xml")) is string manifest)
            {
                var m = Regex.Match(manifest, @"<Application\s[^>]*\bId=""([^""]+)""");
                if (m.Success) appId = m.Groups[1].Value;
            }
            appId ??= "App";
            var ver = full.Split('_').ElementAtOrDefault(1);
            return new StoreInstall(Id, DisplayName, true, dir, $@"shell:AppsFolder\{x.PackageFamilyName}!{appId}", null,
                Strings.Get("store.xboxProtected"), ver, Protected: true);
        }
        return null;
    }
}

// ---------------------------------------------------------------- detector
public sealed class StoreDetector
{
    public static IReadOnlyList<IStoreProvider> DefaultProviders { get; } = new IStoreProvider[]
        { new SteamProvider(), new UbisoftProvider(), new EpicProvider(), new GogProvider(), new EaProvider(), new XboxProvider() };

    private readonly IStoreEnv _env;
    private readonly IReadOnlyList<IStoreProvider> _providers;
    private readonly Dictionary<string, (DateTime at, StoreResult r)> _cache = new();
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(20);
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public IStoreEnv Env => _env;
    public StoreDetector(IStoreEnv env, IReadOnlyList<IStoreProvider>? providers = null) { _env = env; _providers = providers ?? DefaultProviders; }

    public StoreResult Detect(GameDef g, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(g.Id, out var c) && Now() - c.at < CacheFor) return c.r;
        var all = new List<StoreInstall>();
        foreach (var p in _providers)
        {
            try { if (p.Detect(g, _env) is StoreInstall i) all.Add(i); }
            catch (Exception) { /* a broken store must never break detection of the others */ }
        }
        var r = new StoreResult(all);
        _cache[g.Id] = (Now(), r);
        return r;
    }
    public void Invalidate() => _cache.Clear();
}

public sealed class StoreResult
{
    public IReadOnlyList<StoreInstall> All { get; }
    /// <summary>First real installation in provider order (Steam, Ubisoft, Epic, GOG, EA, Xbox).</summary>
    public StoreInstall? Primary => All.FirstOrDefault(i => i.Installed);
    public StoreInstall? OwnedOnly => All.FirstOrDefault(i => !i.Installed);
    public StoreResult(IReadOnlyList<StoreInstall> all) { All = all; }
    public object ToUi() => new { primary = Primary?.ToUi(), owned = All.Where(i => !i.Installed).Select(i => i.ToUi()).ToList(), all = All.Where(i => i.Installed).Select(i => i.ToUi()).ToList() };
}

public static class AntiCheatGuard
{
    /// <summary>Returns the first anti-cheat file (relative name) that exists in the install, or null.</summary>
    public static string? FoundFile(GameDef g, IStoreEnv env, string? installDir)
    {
        if (installDir == null || g.AntiCheatFiles == null) return null;
        foreach (var f in g.AntiCheatFiles)
            if (env.FileExists(StoreUtil.Combine(installDir, f)) || env.DirExists(StoreUtil.Combine(installDir, f))) return f;
        return null;
    }
    /// <summary>Install root from the running exe path: strips the launchExe's folder part ("bin/FarCry5.exe" -> parent of bin).</summary>
    public static string? RootFromExe(GameDef g, string? exePath)
    {
        if (exePath == null) return null;
        var dir = WinPath.Parent(exePath);
        var rel = g.LaunchExe == null ? null : WinPath.Parent(g.LaunchExe);
        if (dir == null) return null;
        if (!string.IsNullOrEmpty(rel))
            foreach (var _ in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries)) dir = WinPath.Parent(dir) ?? dir;
        return dir;
    }
    public static string? FoundModule(GameDef g, IEnumerable<string> moduleNames)
    {
        if (g.AntiCheatModules == null) return null;
        return moduleNames.FirstOrDefault(m => g.AntiCheatModules.Any(a => a.Equals(m, StringComparison.OrdinalIgnoreCase)));
    }
}
