using System.Text.Json;

namespace Vanta.Core;

public sealed class Settings
{
    public string Language { get; set; } = "nl";
    public string? CatalogDir { get; set; }
    public string? CatalogUrl { get; set; }
    public Dictionary<string, Dictionary<string, string>> Hotkeys { get; set; } = new();   // gameId -> cheatId(/inc|/dec) -> combo ("" = none)
    public List<string> Recent { get; set; } = new();
    public double AttachDelaySec { get; set; } = 4;
    public bool AutoAttach { get; set; } = true;
    /// <summary>Community reports: Supabase project URL and publishable key; null = the built-in <see cref="Branding.SupabaseUrl"/>.</summary>
    public string? SupabaseUrl { get; set; }
    public string? SupabaseKey { get; set; }
    /// <summary>Opt-in: send anonymous per-day counts of enabled cheats. Off by default.</summary>
    public bool ShareUsage { get; set; }

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.DataFolder);
    public static string DefaultFile => Path.Combine(DataDir, "settings.json");

    public static Settings Load(string? file = null)
    {
        file ??= DefaultFile;
        try { if (File.Exists(file)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json.Options) ?? new Settings(); }
        catch { /* corrupt: defaults */ }
        return new Settings();
    }

    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
        File.Move(tmp, file, true);
    }

    public string? HotkeyFor(string gameId, CheatDef c, string kind = "")
    {
        var key = kind.Length == 0 ? c.Id : c.Id + "/" + kind;
        if (Hotkeys.TryGetValue(gameId, out var map) && map.TryGetValue(key, out var hk)) return hk.Length == 0 ? null : hk;
        return kind switch { "inc" => c.HotkeyInc, "dec" => c.HotkeyDec, _ => c.Hotkey };
    }
}
