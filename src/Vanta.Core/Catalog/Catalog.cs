using System.Text.Json;

namespace Vanta.Core.Catalog;

/// <summary>Small per-game record kept in games/index.json so 1000+ games load without parsing every game.json.</summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Short { get; set; }
    public long? SteamAppId { get; set; }
    public List<string> ProcessNames { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public string? Badge { get; set; }
    public string? Version { get; set; }
    public int CheatCount { get; set; }
    public bool AntiCheat { get; set; }
    public bool OnlineOnly { get; set; }
    public string Path { get; set; } = "";          // relative folder (local) or URL path (remote)
    public long Stamp { get; set; }                 // game.json mtime ticks (local) / version (remote)
    public ArtDef? Art { get; set; }
}

public sealed class CatalogIndex
{
    public int Version { get; set; } = 1;
    public DateTime Generated { get; set; }
    public List<CatalogEntry> Games { get; set; } = new();
}

public interface ICatalogSource
{
    string Describe { get; }
    IReadOnlyList<CatalogEntry> Entries { get; }
    GameDef Load(string id);
}

public static class CatalogBuilder
{
    public const string IndexFile = "index.json";

    public static CatalogEntry ToEntry(GameDef g, string relPath, long stamp) => new()
    {
        Id = g.Id, Name = g.Name, Short = g.Short, SteamAppId = g.SteamAppId, ProcessNames = g.ProcessNames.ToList(),
        Categories = g.Categories.ToList(), Badge = g.Badge, Version = g.SupportedVersions.FirstOrDefault()?.Label,
        CheatCount = g.Cheats.Count(c => !c.Hidden), AntiCheat = g.AntiCheat, OnlineOnly = g.OnlineOnly,
        Path = relPath.Replace('\\', '/'), Stamp = stamp, Art = g.Art,
    };

    /// <summary>Scans dir/*/game.json and builds the index (bad files are reported, not fatal).</summary>
    public static CatalogIndex Build(string dir, List<string>? problems = null)
    {
        var idx = new CatalogIndex { Generated = DateTime.UtcNow };
        if (!Directory.Exists(dir)) return idx;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var f = System.IO.Path.Combine(sub, "game.json");
            if (!File.Exists(f)) continue;
            try
            {
                var g = Json.LoadGame(f);
                if (idx.Games.Any(x => x.Id == g.Id)) { problems?.Add($"{f}: duplicate id '{g.Id}'"); continue; }
                idx.Games.Add(ToEntry(g, System.IO.Path.GetFileName(sub), File.GetLastWriteTimeUtc(f).Ticks));
            }
            catch (Exception e) { problems?.Add($"{f}: {e.Message}"); }
        }
        return idx;
    }

    public static void Save(CatalogIndex idx, string dir) =>
        File.WriteAllText(System.IO.Path.Combine(dir, IndexFile), JsonSerializer.Serialize(idx, Json.Compact));

    /// <summary>index.json is stale when a game folder was added/removed or a game.json is newer than its entry.</summary>
    public static bool IsStale(CatalogIndex idx, string dir)
    {
        var folders = Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Where(d => File.Exists(System.IO.Path.Combine(d, "game.json"))).ToList() : new();
        if (folders.Count != idx.Games.Count) return true;
        var byPath = idx.Games.ToDictionary(g => g.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var d in folders)
        {
            if (!byPath.TryGetValue(System.IO.Path.GetFileName(d), out var e)) return true;
            if (File.GetLastWriteTimeUtc(System.IO.Path.Combine(d, "game.json")).Ticks != e.Stamp) return true;
        }
        return false;
    }
}

/// <summary>games/ folder next to the exe (or a folder from settings). Full definitions are loaded lazily and cached.</summary>
public sealed class LocalCatalog : ICatalogSource
{
    private readonly string _dir;
    private readonly Dictionary<string, GameDef> _cache = new();
    private readonly Dictionary<string, CatalogEntry> _byId;
    public IReadOnlyList<CatalogEntry> Entries { get; }
    public List<string> Problems { get; } = new();
    public string Describe => _dir;
    public bool IndexRebuilt { get; }

    public LocalCatalog(string dir, bool writeIndex = true)
    {
        _dir = dir;
        CatalogIndex? idx = null;
        var file = Path.Combine(dir, CatalogBuilder.IndexFile);
        try { if (File.Exists(file)) idx = JsonSerializer.Deserialize<CatalogIndex>(File.ReadAllText(file), Json.Options); } catch { idx = null; }
        if (idx == null || CatalogBuilder.IsStale(idx, dir))
        {
            idx = CatalogBuilder.Build(dir, Problems);
            IndexRebuilt = true;
            if (writeIndex) try { CatalogBuilder.Save(idx, dir); } catch { /* read-only folder: keep in memory */ }
        }
        Entries = idx.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _byId = Entries.ToDictionary(e => e.Id);
    }

    public GameDef Load(string id)
    {
        if (_cache.TryGetValue(id, out var g)) return g;
        if (!_byId.TryGetValue(id, out var e)) throw new KeyNotFoundException(id);
        g = Json.LoadGame(Path.Combine(_dir, e.Path, "game.json"));
        _cache[id] = g;
        return g;
    }
}

/// <summary>
/// Remote catalog (design for later versions): {url}/index.json + {url}/{path}/game.json, cached under
/// %LOCALAPPDATA%\Vanta\catalog-cache so the app keeps working offline. Remote definitions are data only (no code).
/// </summary>
public sealed class RemoteCatalog : ICatalogSource
{
    private readonly string _url, _cacheDir;
    private readonly HttpClient _http;
    private readonly Dictionary<string, CatalogEntry> _byId;
    public IReadOnlyList<CatalogEntry> Entries { get; }
    public string Describe => _url;

    public RemoteCatalog(string url, string cacheDir, HttpClient? http = null)
    {
        _url = url.TrimEnd('/'); _cacheDir = cacheDir; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Directory.CreateDirectory(cacheDir);
        var cached = Path.Combine(cacheDir, "index.json");
        string json;
        try { json = _http.GetStringAsync(_url + "/index.json").GetAwaiter().GetResult(); File.WriteAllText(cached, json); }
        catch when (File.Exists(cached)) { json = File.ReadAllText(cached); }
        var idx = JsonSerializer.Deserialize<CatalogIndex>(json, Json.Options) ?? new CatalogIndex();
        Entries = idx.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _byId = Entries.ToDictionary(e => e.Id);
    }

    public GameDef Load(string id)
    {
        var e = _byId[id];
        var local = Path.Combine(_cacheDir, e.Id + "." + e.Stamp + ".json");
        if (!File.Exists(local)) File.WriteAllText(local, _http.GetStringAsync($"{_url}/{e.Path}/game.json").GetAwaiter().GetResult());
        return Json.LoadGame(local);
    }
}
