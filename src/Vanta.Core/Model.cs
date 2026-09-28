using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vanta.Core;

public sealed class GameDef
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Short { get; set; }
    public long? SteamAppId { get; set; }
    public List<string> ProcessNames { get; set; } = new();
    public List<string>? LauncherProcesses { get; set; }
    /// <summary>Per-store ids used to detect installs and launch through the right launcher.</summary>
    public StoresDef? Stores { get; set; }
    /// <summary>Launch exe relative to the install folder (e.g. "bin/FarCry6.exe"); also used for direct-exe fallback.</summary>
    public string? LaunchExe { get; set; }
    /// <summary>Files (relative to the install folder) whose presence means an anti-cheat is installed: Vanta then refuses.</summary>
    public List<string>? AntiCheatFiles { get; set; }
    /// <summary>Modules whose presence in the game process means an anti-cheat is running: Vanta then refuses.</summary>
    public List<string>? AntiCheatModules { get; set; }
    /// <summary>Short scope note shown in the UI (e.g. solo campaign only).</summary>
    public string? Scope { get; set; }
    /// <summary>Default module for AOB scans (defaults to processNames[0]).</summary>
    public string? Module { get; set; }
    public List<VersionDef> SupportedVersions { get; set; } = new();
    public bool AntiCheat { get; set; }
    public bool OnlineOnly { get; set; }
    public List<string> Categories { get; set; } = new();
    public string? Badge { get; set; }
    public string? Author { get; set; }
    public string? Source { get; set; }
    public List<string>? Notes { get; set; }
    /// <summary>Translations of the texts shown in the UI; the plain fields are English. { "nl": { "notes": [...], "scope": "...", "version": "..." } }</summary>
    public Dictionary<string, GameText>? I18n { get; set; }
    public ArtDef? Art { get; set; }
    public List<CheatDef> Cheats { get; set; } = new();

    [JsonIgnore] public string? Folder { get; set; }
    [JsonIgnore] public string MainModule => Module ?? ProcessNames.FirstOrDefault() ?? "";
    [JsonIgnore] public bool Blocked => AntiCheat || OnlineOnly;
}

public sealed class VersionDef
{
    public string Label { get; set; } = "";
    public string? FileVersion { get; set; }
    public string? ProductVersion { get; set; }
    public uint? PeTimestamp { get; set; }
    public uint? ModuleSize { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>File fingerprint of the code module on disk (as printed by --verify).</summary>
    public long? FileSize { get; set; }
    public string? HeadSha256 { get; set; }
    public string? TailSha256 { get; set; }
    public string? Build { get; set; }
}

public sealed class StoresDef
{
    public SteamStoreDef? Steam { get; set; }
    public UbisoftStoreDef? Ubisoft { get; set; }
    public EpicStoreDef? Epic { get; set; }
    public GogStoreDef? Gog { get; set; }
    public EaStoreDef? Ea { get; set; }
    public XboxStoreDef? Xbox { get; set; }
}
public sealed class SteamStoreDef { public long AppId { get; set; } }
public sealed class UbisoftStoreDef { public List<long> Ids { get; set; } = new(); }
public sealed class EpicStoreDef { public List<string> AppNames { get; set; } = new(); }
public sealed class GogStoreDef { public List<string> Ids { get; set; } = new(); }
public sealed class EaStoreDef
{
    public List<string> OfferIds { get; set; } = new();
    public List<EaRegKey>? RegistryKeys { get; set; }
}
public sealed class EaRegKey { public string Key { get; set; } = ""; public string? Value { get; set; } }
public sealed class XboxStoreDef { public string PackageFamilyName { get; set; } = ""; public string? AppId { get; set; } }

public sealed class ArtDef
{
    public string? Tint { get; set; }
    public string? Motif { get; set; }
    public List<string>? Sky { get; set; }
    public string? Horizon { get; set; }
    public string? Ground { get; set; }
}

public sealed class CheatDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string>? Names { get; set; }     // optional translations: { "en": "...", "nl": "..." }
    public string Section { get; set; } = "extra";
    public string Type { get; set; } = "toggle";                // toggle | number | slider | button
    public string? Icon { get; set; }
    public string? Hotkey { get; set; }
    public string? HotkeyInc { get; set; }
    public string? HotkeyDec { get; set; }
    public string? Description { get; set; }
    public string? Hint { get; set; }                           // shown while a value isn't readable yet
    public string? Sub { get; set; }
    public string Confidence { get; set; } = "untested";        // confirmed | untested | experimental | broken
    public string? ConfidenceNote { get; set; }
    public List<string>? Requires { get; set; }
    public bool Hidden { get; set; }
    public bool AutoEnable { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public string? Format { get; set; }
    public double? ResetValue { get; set; }                     // written by "Alles uit" for value cheats
    public string? ButtonLabel { get; set; }
    /// <summary>Translations of the texts shown in the UI (the plain fields are English); names also via <see cref="Names"/>.</summary>
    public Dictionary<string, CheatText>? I18n { get; set; }
    public ImplDef Impl { get; set; } = new();
}

public sealed class GameText
{
    public List<string>? Notes { get; set; }
    public string? Scope { get; set; }
    /// <summary>Translated label of supportedVersions[0].</summary>
    public string? Version { get; set; }
}

public sealed class CheatText
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Hint { get; set; }
    public string? Sub { get; set; }
    public string? ButtonLabel { get; set; }
}

public sealed class ImplDef
{
    public string Type { get; set; } = "";                      // aobPatch | aobInject | pointer
    public string? Module { get; set; }
    public Dictionary<string, SiteDef>? Sites { get; set; }
    public List<PatchDef>? Patches { get; set; }
    public AllocDef? Alloc { get; set; }
    public List<string>? Asm { get; set; }
    public List<HookDef>? Hooks { get; set; }
    public List<string>? Exports { get; set; }
    // pointer
    public JsonElement? Base { get; set; }                      // "module+0x10" | "sym:NAME" | { aob, ripOffset, insnLength, module? }
    public List<long>? Offsets { get; set; }
    public string? ValueType { get; set; }                      // int32 | int64 | float | double | byte | int16
    public double? OnValue { get; set; }
    public double? OffValue { get; set; }
    public bool Freeze { get; set; }
    public PointerSourceDef? FreezeFrom { get; set; }           // toggle + freeze: each tick write the value read from this chain (e.g. max health)
    public List<long>? Mirror { get; set; }                     // extra byte offsets from the final address that get the same value (e.g. BaseValue next to CurrentValue)
    public bool? Restore { get; set; }                          // toggle off: write the original value back (default true)
    public string? Action { get; set; }                         // button: set | add
    public double? Amount { get; set; }
}

/// <summary>A second pointer chain that a freeze toggle copies its value from. Base defaults to the cheat's base.</summary>
public sealed class PointerSourceDef
{
    public JsonElement? Base { get; set; }
    public List<long>? Offsets { get; set; }
    public string? ValueType { get; set; }                      // default: the cheat's valueType
    public double? Scale { get; set; }                          // multiply the source value (default 1)
}

public sealed class SiteDef
{
    public List<PatternDef> Patterns { get; set; } = new();
    public string? Expect { get; set; }
    public int Overwrite { get; set; }
    public List<CheckDef>? Checks { get; set; }
    public bool AnyMemory { get; set; }                          // default: executable regions only
}

public sealed class PatternDef
{
    public string Aob { get; set; } = "";
    public long Offset { get; set; }
}

public sealed class CheckDef
{
    public int? I32At { get; set; }
    public int? U8At { get; set; }
    public List<long>? Not { get; set; }
    public long? Min { get; set; }
    public long? Max { get; set; }
}

public sealed class PatchDef
{
    public string Site { get; set; } = "";
    public long Offset { get; set; }
    public string Bytes { get; set; } = "";
}

public sealed class AllocDef
{
    public int Size { get; set; } = 0x1000;
    public string? Near { get; set; }
}

public sealed class HookDef
{
    public string Site { get; set; } = "";
    public string Label { get; set; } = "";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static readonly string[] Confidences = { "confirmed", "untested", "experimental", "broken" };

    /// <summary>Loader tolerance: confidence is case-insensitive; unknown values count as "untested".</summary>
    public static void Normalize(GameDef g)
    {
        foreach (var c in g.Cheats)
        {
            var v = (c.Confidence ?? "").Trim().ToLowerInvariant();
            c.Confidence = Array.IndexOf(Confidences, v) >= 0 ? v : "untested";
        }
    }

    public static GameDef LoadGame(string file)
    {
        var g = JsonSerializer.Deserialize<GameDef>(File.ReadAllText(file), Options) ?? throw new InvalidDataException("leeg bestand");
        g.Folder = Path.GetDirectoryName(Path.GetFullPath(file));
        Normalize(g);
        return g;
    }
}
