using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vanta.Core.Account;

/// <summary>A community game request (public.vanta_game_requests).</summary>
public sealed record GameRequest(int AppId, string Name, string? CoverUrl, string Status, string? Note, int Votes, int Votes7d, bool Voted)
{
    public static GameRequest? Parse(JsonNode? j)
    {
        if (j is not JsonObject o || o["appid"] is not JsonValue a || !a.TryGetValue<int>(out var id) || id <= 0) return null;
        string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int N(string k) => o[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        return new GameRequest(id, S("name") ?? ("App " + id), S("cover_url"), S("status") ?? "open", S("note"), N("votes"), N("votes_7d"),
            o["voted"] is JsonValue vv && vv.TryGetValue<bool>(out var b) && b);
    }

    public object ToUi() => new { appid = AppId, name = Name, cover = CoverUrl, status = Status, note = Note, votes = Votes, votes7d = Votes7d, voted = Voted };
}

/// <summary>A Steam store search hit.</summary>
public sealed record SteamApp(int AppId, string Name, string? Image)
{
    /// <summary>Header image on the Steam CDN (accepted by the database as cover).</summary>
    public string Cover => $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";
    public object ToUi() => new { appid = AppId, name = Name, image = Image, cover = Cover };
}

/// <summary>
/// Steam store lookups for "Request a game". Runs in the host because the store API sends no CORS headers
/// (and the UI's CSP blocks all network access anyway). A name searches the store; an app id or store link looks the app up.
/// </summary>
public sealed class SteamStore
{
    private readonly HttpClient _http;
    public string Country { get; set; } = "nl";

    public SteamStore(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Branding.Name}/{Branding.Version}");
    }

    private static readonly Regex AppUrl = new(@"(?:store\.steampowered\.com|steamcommunity\.com|steamdb\.info)/app/(\d{1,9})", RegexOptions.IgnoreCase);

    /// <summary>App id from "12345", "app 12345" or a store/SteamDB link; null for a plain name.</summary>
    public static int? ParseAppId(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        var m = AppUrl.Match(s);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var u) && u > 0) return u;
        if (s.StartsWith("app ", StringComparison.OrdinalIgnoreCase)) s = s[4..].Trim();
        return s.Length <= 9 && s.All(char.IsAsciiDigit) && int.TryParse(s, out var n) && n > 0 ? n : null;
    }

    /// <summary>
    /// Up to <paramref name="limit"/> base games. The store search has no app type, so every hit is checked with appdetails
    /// (cached, at most <see cref="MaxParallel"/> at a time): DLC, soundtracks, season passes, demos, tools and videos are
    /// dropped; a DLC/demo hit is replaced by its base game ("fullgame") when that is not in the list yet. When Steam
    /// rate-limits or a lookup fails, the hit is judged by its name instead (<see cref="LooksLikeExtra"/>).
    /// An app id or store link resolves DLC to the base game, or throws "not_a_game".
    /// Throws <see cref="AccountException"/> "offline"/"timeout"/"steam_error"/"rate_limited"/"not_a_game".
    /// </summary>
    public async Task<List<SteamApp>> SearchAsync(string term, int limit = 10, CancellationToken ct = default)
    {
        term = (term ?? "").Trim();
        if (term.Length > 100) term = term[..100];
        if (term.Length < 2 && ParseAppId(term) == null) return new();
        if (ParseAppId(term) is int id)
        {
            var one = await ResolveGameAsync(id, ct).ConfigureAwait(false);
            return one == null ? new() : new() { one };
        }
        var j = await Get($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc={Country}&l=english", ct).ConfigureAwait(false);
        var hits = new List<SteamApp>();
        if (j?["items"] is JsonArray items)
            foreach (var it in items)
            {
                if (it is not JsonObject o || (o["type"]?.GetValue<string>() ?? "app") != "app") continue;
                if (o["id"] is not JsonValue iv || !iv.TryGetValue<int>(out var appid) || appid <= 0) continue;
                var name = Clean(o["name"]?.GetValue<string>());
                if (name == null || hits.Any(x => x.AppId == appid)) continue;
                hits.Add(new SteamApp(appid, name, SteamImage(o["tiny_image"]?.GetValue<string>())));
                if (hits.Count >= MaxLookups) break;
            }
        var infos = await InfosAsync(hits.Select(h => h.AppId), ct).ConfigureAwait(false);
        var list = new List<SteamApp>();
        void Add(SteamApp a) { if (list.Count < limit && !list.Any(x => x.AppId == a.AppId)) list.Add(a); }
        foreach (var h in hits)
        {
            if (!infos.TryGetValue(h.AppId, out var info)) { if (!LooksLikeExtra(h.Name)) Add(h); continue; }   // unknown: judge by name
            if (info.IsGame) Add(h);
            else if (info.FullGameId is int fg && fg > 0 && info.FullGameName != null) Add(new SteamApp(fg, info.FullGameName, null));
        }
        return list;
    }

    /// <summary>Store details for one app id when it is a game; null when unknown or not a game.</summary>
    public async Task<SteamApp?> DetailsAsync(int appId, CancellationToken ct = default)
    {
        var info = await InfoAsync(appId, ct).ConfigureAwait(false);
        return info is { IsGame: true, Name: not null } ? new SteamApp(appId, info.Name, info.Header) : null;
    }

    /// <summary>A pasted app id: the game itself, the base game of a DLC/demo/soundtrack, null when Steam doesn't know it,
    /// or <see cref="AccountException"/> "not_a_game" (a tool, video or DLC without base game).</summary>
    public async Task<SteamApp?> ResolveGameAsync(int appId, CancellationToken ct = default)
    {
        var info = await InfoAsync(appId, ct).ConfigureAwait(false);
        if (info == null) return null;
        if (info.IsGame) return info.Name == null ? null : new SteamApp(appId, info.Name, info.Header);
        if (info.FullGameId is int fg && fg > 0 && fg != appId)
        {
            var parent = await InfoAsync(fg, ct).ConfigureAwait(false);
            if (parent is { IsGame: true, Name: not null }) return new SteamApp(fg, parent.Name, parent.Header);
            if (parent == null && info.FullGameName != null) return new SteamApp(fg, info.FullGameName, null);
        }
        throw new AccountException("not_a_game");
    }

    // ---------------- app type lookups (appdetails) ----------------
    /// <summary>What appdetails says about an app. <see cref="Type"/> is "game", "dlc", "music", "demo", "video", "mod", ...</summary>
    public sealed record AppInfo(string Type, string? Name, string? Header, int? FullGameId, string? FullGameName)
    {
        public bool IsGame => Type == "game";
    }

    public const int MaxParallel = 4, MaxLookups = 12;
    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(12);
    public static TimeSpan RateLimitPause { get; set; } = TimeSpan.FromSeconds(60);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (AppInfo? info, DateTime at)> _cache = new();
    private readonly SemaphoreSlim _gate = new(MaxParallel, MaxParallel);
    private DateTime _pausedUntil = DateTime.MinValue;
    /// <summary>appdetails requests actually sent (tests).</summary>
    public int Lookups => _lookups;
    private int _lookups;

    /// <summary>Known infos for the ids (cached or looked up, max <see cref="MaxParallel"/> parallel). Ids whose lookup failed
    /// (offline, rate limit, timeout) are missing from the result; ids Steam doesn't know are missing too.</summary>
    public async Task<Dictionary<int, AppInfo>> InfosAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var tasks = ids.Distinct().Select(async id =>
        {
            try { return (id, info: await InfoAsync(id, ct).ConfigureAwait(false)); }
            catch (AccountException) { return (id, info: (AppInfo?)null); }
        }).ToList();
        var done = await Task.WhenAll(tasks).ConfigureAwait(false);
        return done.Where(x => x.info != null).ToDictionary(x => x.id, x => x.info!);
    }

    /// <summary>One app (cached). Null = Steam doesn't know it (cached too). Throws on network errors / rate limits (not cached).</summary>
    public async Task<AppInfo?> InfoAsync(int appId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(appId, out var c) && DateTime.UtcNow - c.at < CacheTtl) return c.info;
        if (DateTime.UtcNow < _pausedUntil) throw new AccountException("rate_limited", 429);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(appId, out c) && DateTime.UtcNow - c.at < CacheTtl) return c.info;
            if (DateTime.UtcNow < _pausedUntil) throw new AccountException("rate_limited", 429);
            Interlocked.Increment(ref _lookups);
            JsonNode? j;
            try { j = await Get($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&cc={Country}&l=english", ct).ConfigureAwait(false); }
            catch (AccountException e) when (e.Code == "rate_limited") { _pausedUntil = DateTime.UtcNow + RateLimitPause; throw; }
            if (j == null) throw new AccountException("rate_limited", 429);   // Steam answers "null" when it throttles
            var info = ParseInfo(j, appId);
            _cache[appId] = (info, DateTime.UtcNow);
            return info;
        }
        finally { _gate.Release(); }
    }

    public static AppInfo? ParseInfo(JsonNode? j, int appId)
    {
        if (j?[appId.ToString()] is not JsonObject e || e["success"]?.GetValue<bool>() != true || e["data"] is not JsonObject d) return null;
        string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var type = (S(d["type"]) ?? "").Trim().ToLowerInvariant();
        if (type.Length == 0) type = "unknown";
        int? fg = null; string? fgName = null;
        if (d["fullgame"] is JsonObject f)
        {
            var raw = f["appid"];
            if (raw is JsonValue rv && (rv.TryGetValue<int>(out var n) || (rv.TryGetValue<string>(out var sv) && int.TryParse(sv, out n))) && n > 0) fg = n;
            fgName = Clean(S(f["name"]));
        }
        return new AppInfo(type, Clean(S(d["name"])), SteamImage(S(d["header_image"])), fg, fgName);
    }

    private static readonly Regex Extra = new(
        @"\b(soundtracks?|OST|season pass|expansion pass|year \d+ pass|dlc|demo|playtest|prologue demo|artbook|art ?book|digital art|wallpapers?|dedicated server|sdk|mod tools?|benchmark|trailer|upgrade|starter pack|costume pack|skin pack|cosmetic pack|bonus content|supporter pack)\b",
        RegexOptions.IgnoreCase);

    /// <summary>Fallback when appdetails is unavailable: names that are clearly not a base game.</summary>
    public static bool LooksLikeExtra(string name) => Extra.IsMatch(name) || Regex.IsMatch(name, @"\s[-–:]\s.*\bpack\s*$", RegexOptions.IgnoreCase);

    private static string? Clean(string? s)
    {
        s = Regex.Replace(s ?? "", @"[\u0000-\u001f\u007f]", "").Trim();
        return s.Length == 0 ? null : s.Length > 120 ? s[..120] : s;
    }

    /// <summary>Only https Steam CDN images pass (they are shown in the UI).</summary>
    public static string? SteamImage(string? url) =>
        url != null && url.Length <= 400 && Regex.IsMatch(url, @"^https://([a-z0-9-]+\.)*(steamstatic\.com|akamaihd\.net)/[A-Za-z0-9._~/%?=&+-]+$") ? url : null;

    private async Task<JsonNode?> Get(string url, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.GetAsync(url, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new AccountException("offline", 0, e.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AccountException("timeout"); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new AccountException((int)res.StatusCode == 429 ? "rate_limited" : "steam_error", (int)res.StatusCode);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { return text.Length == 0 ? null : JsonNode.Parse(text); }
            catch (JsonException) { throw new AccountException("steam_error", 502); }
        }
    }
}
