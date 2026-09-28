using System.Text.Json;
using Vanta.Core.Catalog;
using Vanta.Core.Stores;

namespace Vanta.Core.Engine;

public sealed record ProcInfo(int Pid, string Name);

public interface IProcessProvider
{
    IReadOnlyList<ProcInfo> List();
    /// <summary>Opens the process for memory access; throws with a readable message.</summary>
    IProcessMemory Open(int pid);
    (string? fileVersion, string? productVersion) FileVersion(string? path);
}

public sealed record HotkeyBinding(string Combo, string GameId, string CheatId, string Kind);

/// <summary>
/// App logic between the UI (JSON messages) and the engine: game detection, launch, attach/version check,
/// cheat state across game restarts, hotkey actions. Single-threaded: the host calls everything from one engine thread.
/// </summary>
public sealed class TrainerController
{
    private sealed class GameRt
    {
        public string Id = "";
        public int? Pid; public DateTime FirstSeen; public TrainerSession? Session;
        public bool Launching; public DateTime LaunchAt;
        public bool ForceVersion; public string? VersionState; public string? Fingerprint;
        public string? Error; public int AttachTries; public DateTime NextTry;
        public readonly HashSet<string> Desired = new();
        public readonly Dictionary<string, double> DesiredValues = new();
        public string? LastStatus;
    }

    private readonly ICatalogSource _catalog;
    private readonly IProcessProvider _procs;
    private readonly Settings _settings;
    private readonly Action<object> _send;
    private readonly Func<string, Task> _openUrl;
    private readonly Action<Settings> _saveSettings;
    private readonly StoreDetector? _stores;
    private readonly StatusStore _status;
    public StatusStore Status => _status;
    private readonly Dictionary<string, GameRt> _rt = new();
    private readonly Dictionary<string, List<string>> _byProcess = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectedId { get; private set; }
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
    public int LaunchTimeoutSec { get; set; } = 60;
    public Action<string>? HostLog { get; set; }
    public event Action? HotkeysChanged;
    /// <summary>Accounts, community reports and anonymous usage (null = feature off, e.g. in tests).</summary>
    public Account.AccountService? Account { get; set; }
    /// <summary>Steam store lookups for "Request a game" (replaceable in tests).</summary>
    public Account.SteamStore Steam { get => _steam ??= new Account.SteamStore(); set => _steam = value; }
    private Account.SteamStore? _steam;

    public TrainerController(ICatalogSource catalog, IProcessProvider procs, Settings settings, Action<object> send,
        Func<string, Task>? openUrl = null, Action<Settings>? saveSettings = null, StoreDetector? stores = null, StatusStore? status = null)
    {
        _stores = stores;
        _status = status ?? new StatusStore();
        _catalog = catalog; _procs = procs; _settings = settings; _send = send;
        _openUrl = openUrl ?? (_ => Task.CompletedTask); _saveSettings = saveSettings ?? (_ => { });
        Strings.Lang = settings.Language;
        foreach (var e in catalog.Entries)
            foreach (var p in e.ProcessNames)
            {
                var key = Path.GetFileNameWithoutExtension(p);
                if (!_byProcess.TryGetValue(key, out var l)) _byProcess[key] = l = new();
                l.Add(e.Id);
            }
        SelectedId = settings.Recent.FirstOrDefault(r => catalog.Entries.Any(e => e.Id == r)) ?? catalog.Entries.FirstOrDefault()?.Id;
    }

    private GameRt Rt(string id) { if (!_rt.TryGetValue(id, out var r)) _rt[id] = r = new GameRt { Id = id }; return r; }
    private GameDef Game(string id) => _catalog.Load(id);
    private CatalogEntry? Entry(string id) => _catalog.Entries.FirstOrDefault(e => e.Id == id);
    private static string Now5() => DateTime.Now.ToString("HH:mm");
    private void Log(string text, string level = "info") { _send(new { type = "log", time = Now5(), text, level }); HostLog?.Invoke($"[{level}] {text}"); }

    // ---------------- UI payloads ----------------
    public object Library() => new
    {
        type = "library",
        app = new { name = Branding.Name, version = Branding.Version, production = true, lang = _settings.Language, ackTimeout = 30000 },
        games = _catalog.Entries.Select(e => new
        {
            id = e.Id, name = e.Name, @short = e.Short ?? Abbrev(e.Name), badge = e.Badge ?? "", version = e.Version ?? "", cheatCount = e.CheatCount,
            steamAppId = e.SteamAppId, categories = e.Categories, antiCheat = e.AntiCheat, onlineOnly = e.OnlineOnly,
            group = _settings.Recent.Contains(e.Id) ? "recent" : "all", art = e.Art, process = e.ProcessNames.FirstOrDefault() ?? "",
            cheats = Array.Empty<object>(), lazy = true,
        }),
        selected = SelectedId,
    };

    private static string Abbrev(string n) => new string(n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])).ToArray());

    // ---------------- local test status ----------------
    /// <summary>Version key for local test status: the attached exe's fingerprint, else the last one seen, else the first supported label.</summary>
    public string StatusKey(GameDef g)
    {
        var r = Rt(g.Id);
        return r.Fingerprint != null ? StatusStore.VersionKey(r.Fingerprint, null) : _status.DefaultKey(g);
    }

    public string? LocalStatus(GameDef g, string cheatId) => _status.Get(g.Id, StatusKey(g), cheatId);
    public string EffectiveConfidence(GameDef g, CheatDef c) => StatusStore.Effective(c.Confidence, LocalStatus(g, c.Id));

    private static string? ConfidenceNote(string eff) => eff switch
    {
        "confirmed" => null,
        "experimental" => Strings.Get("conf.warn.experimental"),
        "broken" => Strings.Get("conf.warn.broken"),
        _ => Strings.Get("conf.warn.untested"),
    };

    private string? SetStatus(string gameId, string cheatId, string? status)
    {
        var g = Game(gameId);
        var c = g.Cheats.FirstOrDefault(x => x.Id == cheatId) ?? throw new CheatException(Strings.Get("cheat.unknown", cheatId));
        if (status != null && Array.IndexOf(StatusStore.Values, status) < 0) return "onbekende status " + status;
        var key = StatusKey(g);
        var label = key.StartsWith("label:") ? key[6..] : g.SupportedVersions.FirstOrDefault()?.Label;
        _status.Set(g, key, cheatId, status, label);
        try { _status.Save(); } catch (Exception e) { Log(e.Message, "error"); return e.Message; }
        // a cheat that is now marked broken but is still on stays on; the user can turn it off as usual
        Log(Strings.Get("status.saved", c.Names != null && c.Names.TryGetValue(_settings.Language, out var ln) ? ln : c.Name,
            status == null ? Strings.Get("status.default") : Strings.Get("status." + status)));
        _send(UiGame(gameId));
        PushState(gameId);
        return null;
    }

    // ---------------- community reports ----------------
    /// <summary>The report for the current game version: fingerprint (printable, hashed if needed) + version label.</summary>
    public (string fingerprint, string? label) ReportVersion(GameDef g)
    {
        var key = StatusKey(g);
        string? label = key.StartsWith("label:") ? key[6..] : null;
        if (label == null && _status.Data.Games.TryGetValue(g.Id, out var ge) && ge.Versions.TryGetValue(key, out var ve)) label = ve.Label;
        label ??= g.SupportedVersions.FirstOrDefault()?.Label;
        return (Vanta.Core.Account.AccountService.ReportFingerprint(key), label);
    }

    /// <summary>Sends (status works|broken) or withdraws (status null) the community report; the result arrives as "reportResult".</summary>
    private string? Report(string gameId, string cheatId, string? status, string? note)
    {
        if (Account == null) return "account niet beschikbaar";
        if (status != null && status is not ("works" or "broken")) return "onbekende status " + status;
        var g = Game(gameId);
        var c = g.Cheats.FirstOrDefault(x => x.Id == cheatId) ?? throw new CheatException(Strings.Get("cheat.unknown", cheatId));
        var (fp, label) = ReportVersion(g);
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > 300 }) note = note[..300];
        var item = new Vanta.Core.Account.ReportItem(g.Id, c.Id, fp, status ?? "works", status == "broken" ? note : null, Branding.Version, label, g.Name, c.Name);
        var shown = c.Names != null && c.Names.TryGetValue(_settings.Language, out var ln) ? ln : c.Name;
        _ = Task.Run(async () =>
        {
            var r = await Account.ReportAsync(item, withdraw: status == null).ConfigureAwait(false);
            _send(new { type = "reportResult", gameId, id = cheatId, status, r.ok, r.queued, r.error, fingerprint = fp,
                community = r.community == null ? null : new { works = r.community.Works, broken = r.community.Broken, status = r.community.Status, fixedInVersion = r.community.FixedInVersion } });
            if (status == null) return;
            if (r.ok) Log(Strings.Get("report.sent", shown));
            else if (r.queued) Log(Strings.Get("report.queued", shown), "warn");
            else Log(Strings.Get("report.failed", r.error ?? "?"), "warn");
        });
        return null;
    }

    private string? Community(string gameId, bool force)
    {
        if (Account == null) return "account niet beschikbaar";
        var g = Game(gameId);
        var (fp, _) = ReportVersion(g);
        _ = Task.Run(async () =>
        {
            var d = await Account.CommunityAsync(g.Id, fp, force).ConfigureAwait(false);
            _send(new { type = "community", gameId, fingerprint = fp, available = d != null,
                cheats = d?.ToDictionary(x => x.Key, x => (object)new { works = x.Value.Works, broken = x.Value.Broken, status = x.Value.Status, fixedInVersion = x.Value.FixedInVersion }) });
        });
        return null;
    }

    // ---------------- game requests ----------------
    private string? Requests()
    {
        if (Account == null) return "account niet beschikbaar";
        _ = Task.Run(async () =>
        {
            var (items, loggedIn, error) = await Account.GameRequestsAsync().ConfigureAwait(false);
            _send(new { type = "requests", ok = items != null, error, loggedIn, items = items?.Select(x => x.ToUi()).ToList() });
        });
        return null;
    }

    private void SteamSearch(string term)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var items = await Steam.SearchAsync(term).ConfigureAwait(false);
                _send(new { type = "steamSearch", term, ok = true, error = (string?)null, items = items.Select(x => x.ToUi()).ToList() });
            }
            catch (Exception e)
            {
                var code = e is Account.AccountException ae ? ae.Code : "steam_error";
                HostLog?.Invoke("[warn] steam search: " + e.Message);
                _send(new { type = "steamSearch", term, ok = false, error = code, items = Array.Empty<object>() });
            }
        });
    }

    private string? RequestVote(int appId, string? name, string? cover, bool unvote)
    {
        if (Account == null) return "account niet beschikbaar";
        _ = Task.Run(async () =>
        {
            var (ok, req, error) = await Account.VoteGameAsync(appId, name, cover, unvote).ConfigureAwait(false);
            _send(new { type = "requestVoteResult", appid = appId, unvote, ok, error, request = req?.ToUi() });
            if (ok) Requests();
        });
        return null;
    }

    private string ExportStatus()
    {
        var json = _status.Export(id => { try { return Game(id); } catch { return null; } });
        var dir = Path.GetDirectoryName(Path.GetFullPath(_status.File))!;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"teststatus-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, json);
        _send(new { type = "statusExport", json, path = file });
        Log(Strings.Get("status.exported", file));
        return file;
    }

    public object UiGame(string id)
    {
        var g = Game(id);
        var lang = _settings.Language;
        var skey = StatusKey(g);
        return new
        {
            type = "game",
            game = new
            {
                id = g.Id, name = g.Name, @short = g.Short ?? Abbrev(g.Name), badge = g.Badge ?? "", version = g.SupportedVersions.FirstOrDefault()?.Label ?? "",
                process = g.ProcessNames.FirstOrDefault() ?? "", steamAppId = g.SteamAppId, categories = g.Categories, antiCheat = g.AntiCheat, onlineOnly = g.OnlineOnly,
                cheatCount = g.Cheats.Count(c => !c.Hidden), notes = g.Notes ?? new(), scope = g.Scope, install = InstallUi(g), art = g.Art,
                statusVersion = skey,
                cheats = g.Cheats.Where(c => !c.Hidden).Select(c =>
                {
                    var local = _status.Get(g.Id, skey, c.Id);
                    var w = (local, eff: StatusStore.Effective(c.Confidence, local));
                    return (object)new
                {
                    id = c.Id, section = c.Section, type = c.Type,
                    name = c.Names != null && c.Names.TryGetValue(lang, out var ln) ? ln : c.Name,
                    icon = c.Icon ?? "bolt", hotkey = _settings.HotkeyFor(g.Id, c), hotkeyInc = _settings.HotkeyFor(g.Id, c, "inc"), hotkeyDec = _settings.HotkeyFor(g.Id, c, "dec"),
                    defaultHotkey = c.Hotkey, defaultHotkeyInc = c.HotkeyInc, defaultHotkeyDec = c.HotkeyDec,
                    min = c.Min ?? 0, max = c.Max ?? 999999, step = c.Step ?? 1, format = c.Format ?? "{v}", sub = c.Sub,
                    hint = c.Type is "number" or "slider" ? (c.Hint ?? Strings.Get("ptr.null")) : null,
                    note = ConfidenceNote(w.eff), description = c.Description, confidence = w.eff, baseConfidence = c.Confidence, localStatus = w.local, buttonLabel = c.ButtonLabel,
                    enabled = Rt(g.Id).Desired.Contains(c.Id) && Rt(g.Id).Session?.IsActive(c.Id) == true,
                    value = (double?)null,
                }; }),
            },
        };
    }

    private object? InstallUi(GameDef g)
    {
        if (_stores == null) return null;
        try { return _stores.Detect(g).ToUi(); } catch (Exception e) { HostLog?.Invoke("store detect: " + e.Message); return null; }
    }

    public string StatusOf(string id)
    {
        var e = Entry(id);
        if (e != null && (e.AntiCheat || e.OnlineOnly)) return "blocked";
        var r = Rt(id);
        if (r.Session != null) return "attached";
        if (r.Pid != null)
        {
            if (r.Error != null) return "error";
            if (r.VersionState == "mismatch" && !r.ForceVersion) return "wrongversion";
            return "attaching";
        }
        if (r.Launching) return "launching";
        return "notfound";
    }

    private void SendStatus(string id, bool force = false)
    {
        var r = Rt(id);
        var st = StatusOf(id);
        var detail = st switch
        {
            "error" => r.Error,
            "wrongversion" => r.Fingerprint,
            "attached" => r.VersionState == "mismatch" ? Strings.Get("version.mismatch", r.Fingerprint) : r.VersionState == "unverified" ? Strings.Get("version.unverified", r.Fingerprint) : null,
            "blocked" => Strings.Get("blocked", Entry(id)?.Name),
            _ => null,
        };
        var key = st + "|" + detail + "|" + r.Pid;
        if (!force && key == r.LastStatus) return;
        r.LastStatus = key;
        _send(new { type = "status", gameId = id, process = st, detail, pid = r.Pid });
        HostLog?.Invoke($"status {id}: {st} {detail}");
    }

    // ---------------- UI -> host ----------------
    public object HandleUi(JsonElement m)
    {
        int reqId = m.TryGetProperty("reqId", out var rq) && rq.ValueKind == JsonValueKind.Number ? rq.GetInt32() : 0;
        string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        string? gameId = m.TryGetProperty("gameId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : SelectedId;
        string? cheatId = m.TryGetProperty("id", out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : null;
        object Ack(bool ok, string? error = null) => new { type = "ack", reqId, ok, error };
        try
        {
            switch (type)
            {
                case "ready":
                    foreach (var e in _catalog.Entries.Where(e => e.AntiCheat || e.OnlineOnly || _rt.ContainsKey(e.Id))) SendStatus(e.Id, true);
                    if (SelectedId != null) { _send(UiGame(SelectedId)); SendStatus(SelectedId, true); PushState(SelectedId); }
                    _send(SettingsPayload());
                    if (Account != null) _send(Account.Payload());
                    return Ack(true);
                case "selectGame":
                case "getGame":
                    if (gameId == null || Entry(gameId) == null) return Ack(false, "onbekende game");
                    SelectedId = gameId;
                    _send(UiGame(gameId)); SendStatus(gameId, true); PushState(gameId);
                    HotkeysChanged?.Invoke();
                    return Ack(true);
                case "toggle":
                {
                    bool en = m.GetProperty("enabled").GetBoolean();
                    bool force = m.TryGetProperty("force", out var fo) && fo.ValueKind == JsonValueKind.True;
                    return Toggle(gameId!, cheatId!, en, force) is string err ? Ack(false, err) : Ack(true);
                }
                case "setValue":
                    return SetValue(gameId!, cheatId!, m.GetProperty("value").GetDouble()) is string e2 ? Ack(false, e2) : Ack(true);
                case "button":
                    return RunButton(gameId!, cheatId!) is string e3 ? Ack(false, e3) : Ack(true);
                case "disableAll":
                    DisableAll(gameId!); return Ack(true);
                case "launch":
                    return Launch(gameId!) is string e4 ? Ack(false, e4) : Ack(true);
                case "attach":
                {
                    var r = Rt(gameId!); r.ForceVersion = true; r.Error = null; r.AttachTries = 0; r.NextTry = default;
                    if (StatusOf(gameId!) == "notfound") return Launch(gameId!) is string e5 ? Ack(false, e5) : Ack(true);
                    Poll(); return Ack(true);
                }
                case "detach":
                    Detach(gameId!, user: true); return Ack(true);
                case "setStatus":
                {
                    string? st = m.TryGetProperty("status", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() : null;
                    if (st is "" or "default") st = null;
                    return SetStatus(gameId!, cheatId!, st) is string e6 ? Ack(false, e6) : Ack(true);
                }
                case "exportStatus":
                    ExportStatus(); return Ack(true);
                case "getSettings":
                    _send(SettingsPayload()); if (Account != null) _send(Account.Payload()); return Ack(true);
                case "accountLogin":
                    if (Account == null) return Ack(false, "account niet beschikbaar");
                    _ = Account.LoginAsync(); return Ack(true);
                case "accountCancel":
                    Account?.CancelLogin(); return Ack(true);
                case "accountLogout":
                    if (Account == null) return Ack(false, "account niet beschikbaar");
                    _ = Account.LogoutAsync(); return Ack(true);
                case "accountDelete":
                    if (Account == null) return Ack(false, "account niet beschikbaar");
                    _ = Account.DeleteAsync(); return Ack(true);
                case "report":
                case "withdraw":
                {
                    string? st = m.TryGetProperty("status", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
                    string? note = m.TryGetProperty("note", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString() : null;
                    return Report(gameId!, cheatId!, type == "withdraw" ? null : st, note) is string e7 ? Ack(false, e7) : Ack(true);
                }
                case "getCommunity":
                {
                    bool force = m.TryGetProperty("force", out var fc) && fc.ValueKind == JsonValueKind.True;
                    return Community(gameId!, force) is string e8 ? Ack(false, e8) : Ack(true);
                }
                case "getRequests":
                    return Requests() is string e9 ? Ack(false, e9) : Ack(true);
                case "steamSearch":
                {
                    string term = m.TryGetProperty("term", out var tm) && tm.ValueKind == JsonValueKind.String ? tm.GetString() ?? "" : "";
                    SteamSearch(term); return Ack(true);
                }
                case "requestVote":
                case "requestUnvote":
                {
                    int appid = m.TryGetProperty("appid", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.TryGetInt32(out var aid) ? aid : 0;
                    string? nm = m.TryGetProperty("name", out var rn2) && rn2.ValueKind == JsonValueKind.String ? rn2.GetString() : null;
                    string? cover = m.TryGetProperty("cover", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString() : null;
                    if (appid <= 0) return Ack(false, "invalid_appid");
                    return RequestVote(appid, nm, cover, type == "requestUnvote") is string e10 ? Ack(false, e10) : Ack(true);
                }
                case "saveSettings":
                    SaveSettings(m.GetProperty("settings")); return Ack(true);
                default:
                    return Ack(false, "onbekend bericht " + type);
            }
        }
        catch (Exception ex) when (ex is CheatException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Ack(false, ex.Message);
        }
    }

    public object SettingsPayload() => new
    {
        type = "settings",
        settings = new { language = _settings.Language, catalogDir = _settings.CatalogDir ?? "", catalogUrl = _settings.CatalogUrl ?? "", attachDelaySec = _settings.AttachDelaySec, autoAttach = _settings.AutoAttach, shareUsage = _settings.ShareUsage },
        catalog = new { source = _catalog.Describe, games = _catalog.Entries.Count },
        dataDir = Settings.DataDir, version = Branding.Version,
    };

    private void SaveSettings(JsonElement s)
    {
        if (s.TryGetProperty("language", out var l) && l.GetString() is "nl" or "en") { _settings.Language = l.GetString()!; Strings.Lang = _settings.Language; }
        if (s.TryGetProperty("catalogDir", out var cd)) _settings.CatalogDir = string.IsNullOrWhiteSpace(cd.GetString()) ? null : cd.GetString();
        if (s.TryGetProperty("catalogUrl", out var cu)) _settings.CatalogUrl = string.IsNullOrWhiteSpace(cu.GetString()) ? null : cu.GetString();
        if (s.TryGetProperty("shareUsage", out var su) && su.ValueKind is JsonValueKind.True or JsonValueKind.False) _settings.ShareUsage = su.GetBoolean();
        if (s.TryGetProperty("autoAttach", out var aa) && aa.ValueKind is JsonValueKind.True or JsonValueKind.False) _settings.AutoAttach = aa.GetBoolean();
        if (s.TryGetProperty("hotkeys", out var hk) && hk.ValueKind == JsonValueKind.Object && s.TryGetProperty("gameId", out var hg))
        {
            var map = new Dictionary<string, string>();
            foreach (var p in hk.EnumerateObject()) map[p.Name] = p.Value.GetString() ?? "";
            _settings.Hotkeys[hg.GetString()!] = map;
        }
        _saveSettings(_settings);
        _send(SettingsPayload());
        if (SelectedId != null) _send(UiGame(SelectedId));
        HotkeysChanged?.Invoke();
    }

    private void Touch(string id)
    {
        _settings.Recent.Remove(id); _settings.Recent.Insert(0, id);
        if (_settings.Recent.Count > 8) _settings.Recent.RemoveRange(8, _settings.Recent.Count - 8);
        try { _saveSettings(_settings); } catch { }
    }

    private string? Toggle(string gameId, string cheatId, bool enabled, bool force = false)
    {
        var r = Rt(gameId);
        var g = Game(gameId);
        var c = g.Cheats.FirstOrDefault(x => x.Id == cheatId) ?? throw new CheatException(Strings.Get("cheat.unknown", cheatId));
        if (enabled && !force && EffectiveConfidence(g, c) == "broken" && r.Session?.IsActive(cheatId) != true)
        {
            var msg = Strings.Get("cheat.broken", c.Name);
            _send(new { type = "state", gameId, cheats = new[] { new CheatStateDto { Id = cheatId, Enabled = false, Error = null }.ToUi() } });
            Log(msg, "warn");
            return msg;
        }
        if (enabled) r.Desired.Add(cheatId); else r.Desired.Remove(cheatId);
        if (r.Session == null)
        {
            _send(new { type = "state", gameId, cheats = new[] { new CheatStateDto { Id = cheatId, Enabled = false, Error = null }.ToUi() } });
            return Strings.Get("not.attached");
        }
        try
        {
            if (enabled) r.Session.Enable(cheatId); else r.Session.Disable(cheatId);
            if (enabled) Account?.CountUsage(gameId, cheatId);   // opt-in anonymous counts; no-op when off
            Log(Strings.Get(enabled ? "enabled" : "disabled", c.Name));
            return null;
        }
        catch (CheatException e)
        {
            r.Desired.Remove(cheatId);
            _send(new { type = "state", gameId, cheats = new[] { new CheatStateDto { Id = cheatId, Enabled = false, Error = e.Message }.ToUi() } });
            Log(e.Message, "error");
            return e.Message;
        }
    }

    private string? SetValue(string gameId, string cheatId, double v)
    {
        var r = Rt(gameId);
        if (r.Session == null) return Strings.Get("not.attached");
        try
        {
            var c = r.Session.Cheat(cheatId);
            var nv = r.Session.SetValue(cheatId, v);
            if (c.Impl.Freeze) r.DesiredValues[cheatId] = nv;
            Log(Strings.Get("value.set", c.Name, nv));
            return null;
        }
        catch (CheatException e) { Log(e.Message, "error"); PushState(gameId); return e.Message; }
    }

    private string? RunButton(string gameId, string cheatId)
    {
        var r = Rt(gameId);
        if (r.Session == null) return Strings.Get("not.attached");
        try { r.Session.Button(cheatId); Log(r.Session.Cheat(cheatId).Name); return null; }
        catch (CheatException e) { Log(e.Message, "error"); return e.Message; }
    }

    private void DisableAll(string gameId)
    {
        var r = Rt(gameId);
        r.Desired.Clear(); r.DesiredValues.Clear();
        if (r.Session != null)
        {
            r.Session.RestoreAll(resetValues: true);
            AutoEnableHooks(r);   // value hooks stay available (read-only helpers)
        }
        PushState(gameId, all: true);
        Log(Strings.Get("all.off"));
    }

    private void PushState(string gameId, bool all = true)
    {
        var r = Rt(gameId);
        GameDef g;
        try { g = Game(gameId); } catch { return; }
        var list = g.Cheats.Where(c => !c.Hidden && c.Type == "toggle")
            .Select(c => new CheatStateDto { Id = c.Id, Enabled = r.Session?.IsActive(c.Id) == true, Error = null }.ToUi()).ToList();
        _send(new { type = "state", gameId, cheats = list });
        if (r.Session != null) { r.Session.Tick(pollValues: false); ForceValuePush(r); }
    }

    private void ForceValuePush(GameRt r)
    {
        // re-emit every value on next tick
        r.Session!.ResetValueCache();
        r.Session!.Tick();
    }

    private string? Launch(string gameId)
    {
        var e = Entry(gameId) ?? throw new KeyNotFoundException(gameId);
        var r = Rt(gameId);
        if (e.AntiCheat || e.OnlineOnly) return Strings.Get("blocked", e.Name);
        if (r.Pid != null) { Poll(); return null; }               // already running: never launch twice
        if (r.Launching) return null;                               // debounce

        StoreInstall? inst = null;
        GameDef? g = null;
        if (_stores != null)
        {
            g = Game(gameId);
            inst = _stores.Detect(g, fresh: true).Primary;
            if (inst != null && AntiCheatGuard.FoundFile(g, _stores.Env, inst.InstallDir) is string ac)
            {
                var msg = Strings.Get("anticheat.files", ac);
                Log(msg, "error");
                return msg;
            }
        }
        string target, via;
        if (inst != null) { target = inst.PreferDirect && inst.Exe != null ? inst.Exe : inst.LaunchUri ?? inst.Exe ?? ""; via = inst.StoreName; }
        else if (e.SteamAppId != null && (g?.Stores == null || g.Stores.Steam != null)) { target = $"steam://run/{g?.Stores?.Steam?.AppId ?? e.SteamAppId}"; via = "Steam"; }
        else return Strings.Get("store.none");
        if (target.Length == 0) return Strings.Get("store.none");

        r.Launching = true; r.LaunchAt = Now();
        Touch(gameId);
        SendStatus(gameId);
        Log(via == "Steam" ? Strings.Get("launching", e.Name) : Strings.Get("launching.via", e.Name, via));
        HostLog?.Invoke($"launch {gameId}: {target}");
        try { _openUrl(target).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            if (inst?.Exe != null && target != inst.Exe)
            {
                Log(Strings.Get("launch.direct", via, ex.Message), "warn");
                try { _openUrl(inst.Exe).GetAwaiter().GetResult(); return null; }
                catch (Exception ex2) { ex = ex2; }
            }
            r.Launching = false; SendStatus(gameId); Log(Strings.Get("launch.fail", ex.Message), "error"); return ex.Message;
        }
        return null;
    }

    private void Detach(string gameId, bool user)
    {
        var r = Rt(gameId);
        if (r.Session == null) return;
        try { r.Session.RestoreAll(); } catch (Exception e) { HostLog?.Invoke("restore: " + e.Message); }
        r.Session.Dispose(); r.Session = null;
        if (user) { r.Desired.Clear(); r.ForceVersion = false; r.Error = "Ontkoppeld"; }
        PushState(gameId);
        SendStatus(gameId);
        HotkeysChanged?.Invoke();
    }

    // ---------------- detection / attach ----------------
    /// <summary>Call about every second.</summary>
    public void Poll()
    {
        var procs = _procs.List();
        var seen = new Dictionary<string, int>();
        foreach (var p in procs)
            if (_byProcess.TryGetValue(Path.GetFileNameWithoutExtension(p.Name), out var ids))
                foreach (var id in ids) if (!seen.ContainsKey(id)) seen[id] = p.Pid;

        foreach (var id in seen.Keys.Concat(_rt.Keys).Distinct().ToList())
        {
            var r = Rt(id);
            seen.TryGetValue(id, out var pid);
            if (pid != 0 && r.Pid != pid)
            {
                if (r.Pid != null) GameGone(r);
                r.Pid = pid; r.FirstSeen = Now(); r.Launching = false; r.Error = null; r.AttachTries = 0; r.NextTry = default; r.VersionState = null;
                HostLog?.Invoke($"process {id}: pid {pid}");
                if (SelectedId != id && r.Session == null) { SelectedId = id; _send(new { type = "focusGame", gameId = id }); _send(UiGame(id)); }
            }
            else if (pid == 0 && r.Pid != null) GameGone(r);

            if (r.Launching && (Now() - r.LaunchAt).TotalSeconds > LaunchTimeoutSec)
            {
                r.Launching = false;
                Log(Strings.Get("launch.timeout", Entry(id)?.ProcessNames.FirstOrDefault(), LaunchTimeoutSec), "error");
            }
            if (r.Pid != null && r.Session == null && ShouldAttach(id, r)) TryAttach(id, r);
            SendStatus(id);
        }
    }

    private bool ShouldAttach(string id, GameRt r)
    {
        var e = Entry(id);
        if (e == null || e.AntiCheat || e.OnlineOnly) return false;
        if (r.Error != null) return false;
        if (!_settings.AutoAttach && !r.ForceVersion) return false;
        if (r.VersionState == "mismatch" && !r.ForceVersion) return false;
        if ((Now() - r.FirstSeen).TotalSeconds < _settings.AttachDelaySec && !r.ForceVersion) return false;
        return Now() >= r.NextTry;
    }

    private void GameGone(GameRt r)
    {
        if (r.Session != null) { r.Session.Abandon(); r.Session.Dispose(); r.Session = null; Log(Strings.Get("exited", Entry(r.Id)?.Name)); PushState(r.Id); HotkeysChanged?.Invoke(); }
        r.Pid = null; r.Error = null; r.VersionState = null; r.ForceVersion = false;
    }

    private void TryAttach(string id, GameRt r)
    {
        var g = Game(id);
        IProcessMemory mem;
        try { mem = _procs.Open(r.Pid!.Value); }
        catch (Exception e)
        {
            var prot = _stores != null && _stores.Detect(g).Primary?.Protected == true;
            r.Error = Strings.Get(prot ? "open.fail.protected" : "open.fail", e.Message); Log(r.Error, "error"); return;
        }
        var mods = mem.GetModules();
        if (AntiCheatGuard.FoundModule(g, mods.Select(m => m.Name)) is string acm)
        {
            mem.Dispose(); r.Error = Strings.Get("anticheat.module", acm); Log(r.Error, "error"); return;
        }
        if (_stores != null)
        {
            var exe = mods.FirstOrDefault(m => g.ProcessNames.Any(p => p.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))?.Path;
            if (AntiCheatGuard.FoundFile(g, _stores.Env, AntiCheatGuard.RootFromExe(g, exe)) is string acf)
            {
                mem.Dispose(); r.Error = Strings.Get("anticheat.files", acf); Log(r.Error, "error"); return;
            }
        }
        var mod = mem.FindModule(g.MainModule);
        if (mod == null)
        {
            mem.Dispose();
            if (++r.AttachTries > 20) { r.Error = Strings.Get("module.missing", g.MainModule); Log(r.Error, "error"); }
            else r.NextTry = Now().AddSeconds(1);
            return;
        }
        var (state, fp) = VersionCheck.Check(g, mem, mod, _procs.FileVersion(mod.Path));
        r.VersionState = state; r.Fingerprint = fp;
        try
        {
            var fv = System.Text.RegularExpressions.Regex.Match(fp, @"fileVersion=([^,]+)").Groups[1].Value;
            var label = state == "ok" && g.SupportedVersions.Count == 1 ? g.SupportedVersions[0].Label : (fv is "" or "?" ? null : fv);
            _status.SetLastVersion(g, StatusStore.VersionKey(fp, null), label);
            if (_status.Data.Games.ContainsKey(g.Id)) { _status.Save(); if (SelectedId == id) _send(UiGame(id)); }
        }
        catch (Exception e) { HostLog?.Invoke("status: " + e.Message); }
        HostLog?.Invoke($"attach {id} pid {r.Pid}: version {state} ({fp})");
        if (state == "mismatch" && !r.ForceVersion) { mem.Dispose(); Log(Strings.Get("version.mismatch", fp), "warn"); return; }

        var s = new TrainerSession(g, mem, (lvl, text) => Log(text, lvl));
        s.Runtime.Trace = HostLog;
        s.Changed += list => _send(new { type = "state", gameId = id, cheats = list.Select(x => x.ToUi()).ToList() });
        r.Session = s;
        Touch(id);
        Log(Strings.Get("attached", g.ProcessNames.FirstOrDefault(), r.Pid));
        if (state == "unverified") Log(Strings.Get("version.unverified", fp));
        AutoEnableHooks(r);
        int re = 0;
        foreach (var cid in r.Desired.ToList())
        {
            try { s.Enable(cid); re++; }
            catch (CheatException e)
            {
                r.Desired.Remove(cid);
                _send(new { type = "state", gameId = id, cheats = new[] { new CheatStateDto { Id = cid, Enabled = false, Error = e.Message }.ToUi() } });
                Log(e.Message, "error");
            }
        }
        foreach (var (cid, v) in r.DesiredValues.ToList()) { try { s.SetValue(cid, v); } catch (CheatException) { } }
        if (re > 0) Log(Strings.Get("reapplied", re));
        PushState(id);
        HotkeysChanged?.Invoke();
    }

    private void AutoEnableHooks(GameRt r)
    {
        foreach (var c in r.Session!.Game.Cheats.Where(c => c.AutoEnable && c.Hidden && c.Confidence != "broken"))
        {
            try { r.Session.Enable(c.Id); }
            catch (CheatException e) { HostLog?.Invoke($"autoEnable {c.Id}: {e.Message}"); }
        }
    }

    /// <summary>Call every ~200 ms: freeze + value polling for attached games.</summary>
    public void Tick()
    {
        foreach (var r in _rt.Values.Where(x => x.Session != null).ToList())
        {
            if (!r.Session!.Memory.IsAlive) { GameGone(r); SendStatus(r.Id); continue; }
            try { r.Session.Tick(); } catch (Exception e) { HostLog?.Invoke("tick: " + e.Message); }
        }
    }

    // ---------------- hotkeys ----------------
    public List<HotkeyBinding> HotkeyBindings()
    {
        var list = new List<HotkeyBinding>();
        var ids = _rt.Values.Where(r => r.Session != null).Select(r => r.Id).ToList();
        if (SelectedId != null && !ids.Contains(SelectedId)) ids.Add(SelectedId);
        foreach (var id in ids)
        {
            GameDef g;
            try { g = Game(id); } catch { continue; }
            foreach (var c in g.Cheats.Where(c => !c.Hidden))
            {
                foreach (var kind in new[] { "", "inc", "dec" })
                {
                    var hk = _settings.HotkeyFor(id, c, kind);
                    if (hk != null && !list.Any(b => b.Combo.Equals(hk, StringComparison.OrdinalIgnoreCase))) list.Add(new HotkeyBinding(hk, id, c.Id, kind));
                }
            }
        }
        return list;
    }

    public void OnHotkey(HotkeyBinding b)
    {
        var r = Rt(b.GameId);
        if (r.Session == null) return;
        var c = r.Session.Game.Cheats.First(x => x.Id == b.CheatId);
        _send(new { type = "hotkey", gameId = b.GameId, id = b.CheatId });
        if (b.Kind == "" && c.Type == "toggle") Toggle(b.GameId, c.Id, !r.Session.IsActive(c.Id));   // refuses (with a log line) when broken
        else if (b.Kind == "" && c.Type == "button") RunButton(b.GameId, c.Id);
        else if (b.Kind is "inc" or "dec")
        {
            try { r.Session.Add(c.Id, (b.Kind == "inc" ? 1 : -1) * (c.Step ?? 1)); }
            catch (CheatException e) { Log(e.Message, "error"); }
        }
    }

    /// <summary>App exit: restore every attached game.</summary>
    public void Shutdown()
    {
        foreach (var r in _rt.Values.Where(x => x.Session != null).ToList())
        {
            try { r.Session!.RestoreAll(); } catch (Exception e) { HostLog?.Invoke("shutdown restore: " + e.Message); }
            r.Session!.Dispose(); r.Session = null;
        }
    }

    public TrainerSession? SessionOf(string id) => _rt.TryGetValue(id, out var r) ? r.Session : null;
}

public static class VersionCheck
{
    /// <summary>"ok" when a supportedVersions entry matches every fingerprint it specifies; "mismatch" when entries
    /// have fingerprints but none matches; "unverified" when no entry has any fingerprint.</summary>
    public static (string state, string fingerprint) Check(GameDef g, IProcessMemory mem, ModuleInfo mod, (string? fileVersion, string? productVersion) fv)
    {
        var pe = mem.ReadPeInfo(mod);
        FileFingerprint? ff = null;
        bool needFile = g.SupportedVersions.Any(v => v.FileSize != null || v.HeadSha256 != null || v.TailSha256 != null);
        if (needFile && mod.Path != null) { try { ff = FileFingerprint.Compute(mod.Path); } catch { } }
        var fp = $"fileVersion={fv.fileVersion ?? "?"}, peTimestamp={pe?.timestamp.ToString() ?? "?"}, moduleSize={mod.Size}" + (ff != null ? $", fileSize={ff.Size}" : "");
        var withFp = g.SupportedVersions.Where(v => v.FileVersion != null || v.ProductVersion != null || v.PeTimestamp != null || v.ModuleSize != null
            || v.FileSize != null || v.HeadSha256 != null || v.TailSha256 != null).ToList();
        if (withFp.Count == 0) return ("unverified", fp);
        foreach (var v in withFp)
        {
            if (v.FileVersion != null && v.FileVersion != fv.fileVersion) continue;
            if (v.ProductVersion != null && v.ProductVersion != fv.productVersion) continue;
            if (v.PeTimestamp != null && v.PeTimestamp != pe?.timestamp) continue;
            if (v.ModuleSize != null && v.ModuleSize != mod.Size) continue;
            if (ff != null)
            {
                if (v.FileSize != null && v.FileSize != ff.Size) continue;
                if (v.HeadSha256 != null && !v.HeadSha256.Equals(ff.HeadSha256, StringComparison.OrdinalIgnoreCase)) continue;
                if (v.TailSha256 != null && !v.TailSha256.Equals(ff.TailSha256, StringComparison.OrdinalIgnoreCase)) continue;
            }
            return ("ok", fp);
        }
        return ("mismatch", fp);
    }
}
