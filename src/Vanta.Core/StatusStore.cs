using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vanta.Core;

/// <summary>
/// Local test status per game + game version + cheat id ("works" | "broken" | "untested"),
/// stored in %LOCALAPPDATA%\Vanta\status.json. A local status overrides the game.json confidence in the UI.
/// </summary>
public sealed class StatusStore
{
    public const string Works = "works", Broken = "broken", Untested = "untested";
    public static readonly string[] Values = { Works, Broken, Untested };

    public sealed class VersionEntry
    {
        public string? Label { get; set; }
        public string? Updated { get; set; }
        public Dictionary<string, string> Cheats { get; set; } = new();
    }
    public sealed class GameEntry
    {
        public string? Name { get; set; }
        public string? LastVersion { get; set; }
        public Dictionary<string, VersionEntry> Versions { get; set; } = new();
    }
    public sealed class Doc
    {
        public int Schema { get; set; } = 1;
        public Dictionary<string, GameEntry> Games { get; set; } = new();
    }

    public static string DefaultFile => Path.Combine(Settings.DataDir, "status.json");

    public string File { get; }
    public Doc Data { get; private set; } = new();

    public StatusStore(string? file = null)
    {
        File = file ?? DefaultFile;
        Load();
    }

    public void Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                Data = JsonSerializer.Deserialize<Doc>(System.IO.File.ReadAllText(File), Json.Options) ?? new Doc();
        }
        catch { Data = new Doc(); /* corrupt: start empty, keep the old file as .bad */ TryBackup(); }
        Data.Games ??= new();
    }

    private void TryBackup() { try { System.IO.File.Copy(File, File + ".bad", true); } catch { } }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json.Options));
        System.IO.File.Move(tmp, File, true);
    }

    /// <summary>Version key: fingerprint of the attached exe (without fileSize), else "label:&lt;label&gt;".</summary>
    public static string VersionKey(string? fingerprint, string? label)
    {
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            var parts = fingerprint.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => !p.StartsWith("fileSize=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("path=", StringComparison.OrdinalIgnoreCase));
            var k = string.Join(", ", parts);
            if (k.Length > 0) return k;
        }
        return "label:" + (string.IsNullOrWhiteSpace(label) ? "?" : label.Trim());
    }

    /// <summary>The version key to use when the game isn't attached: the last one seen, else the first supported version label.</summary>
    public string DefaultKey(GameDef g) =>
        Data.Games.TryGetValue(g.Id, out var ge) && ge.LastVersion != null ? ge.LastVersion
        : VersionKey(null, g.SupportedVersions.FirstOrDefault()?.Label);

    public void SetLastVersion(GameDef g, string key, string? label)
    {
        var ge = GetGame(g);
        if (ge.LastVersion == key && (label == null || (ge.Versions.TryGetValue(key, out var v0) && v0.Label == label))) return;
        ge.LastVersion = key;
        if (label != null) { var v = GetVersion(ge, key); v.Label = label; }
    }

    public string? Get(string gameId, string versionKey, string cheatId) =>
        Data.Games.TryGetValue(gameId, out var ge) && ge.Versions.TryGetValue(versionKey, out var v) && v.Cheats.TryGetValue(cheatId, out var s) ? s : null;

    /// <summary>Set (status = works/broken/untested) or clear (status = null) a local status.</summary>
    public void Set(GameDef g, string versionKey, string cheatId, string? status, string? label = null)
    {
        if (status != null && Array.IndexOf(Values, status) < 0) throw new ArgumentException("onbekende status: " + status);
        var ge = GetGame(g);
        ge.LastVersion ??= versionKey;
        if (status == null)
        {
            if (ge.Versions.TryGetValue(versionKey, out var v) && v.Cheats.Remove(cheatId))
            {
                if (v.Cheats.Count == 0) ge.Versions.Remove(versionKey);
                v.Updated = Now();
            }
            if (ge.Versions.Count == 0) Data.Games.Remove(g.Id);
            return;
        }
        var ve = GetVersion(ge, versionKey);
        if (label != null) ve.Label = label;
        ve.Cheats[cheatId] = status;
        ve.Updated = Now();
    }

    private GameEntry GetGame(GameDef g)
    {
        if (!Data.Games.TryGetValue(g.Id, out var ge)) Data.Games[g.Id] = ge = new GameEntry();
        ge.Name = g.Name;
        return ge;
    }
    private static VersionEntry GetVersion(GameEntry ge, string key)
    {
        if (!ge.Versions.TryGetValue(key, out var v)) ge.Versions[key] = v = new VersionEntry();
        return v;
    }
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>Effective confidence: a local status overrides the game.json value.</summary>
    public static string Effective(string baseConfidence, string? local) => local switch
    {
        Works => "confirmed",
        Broken => "broken",
        Untested => "untested",
        _ => baseConfidence,
    };

    /// <summary>Export for folding into game.json: only games/versions with at least one status, plus the game.json value for comparison.</summary>
    public string Export(Func<string, GameDef?>? lookup = null)
    {
        var root = new JsonObject
        {
            ["vanta"] = Branding.Version,
            ["exported"] = Now(),
            ["schema"] = 1,
        };
        var games = new JsonObject();
        foreach (var (gid, ge) in Data.Games.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var g = lookup?.Invoke(gid);
            var versions = new JsonObject();
            foreach (var (vk, ve) in ge.Versions.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (ve.Cheats.Count == 0) continue;
                var cheats = new JsonObject();
                foreach (var (cid, st) in ve.Cheats.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var bc = g?.Cheats.FirstOrDefault(c => c.Id == cid)?.Confidence;
                    cheats[cid] = bc == null ? new JsonObject { ["status"] = st }
                                             : new JsonObject { ["status"] = st, ["gameJson"] = bc };
                }
                versions[vk] = new JsonObject { ["label"] = ve.Label, ["updated"] = ve.Updated, ["cheats"] = cheats };
            }
            if (versions.Count == 0) continue;
            games[gid] = new JsonObject { ["name"] = ge.Name ?? g?.Name, ["versions"] = versions };
        }
        root["games"] = games;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
