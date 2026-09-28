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

    /// <summary>Up to <paramref name="limit"/> games. Throws <see cref="AccountException"/> "offline"/"timeout"/"steam_error".</summary>
    public async Task<List<SteamApp>> SearchAsync(string term, int limit = 10, CancellationToken ct = default)
    {
        term = (term ?? "").Trim();
        if (term.Length > 100) term = term[..100];
        if (term.Length < 2 && ParseAppId(term) == null) return new();
        if (ParseAppId(term) is int id)
        {
            var one = await DetailsAsync(id, ct).ConfigureAwait(false);
            return one == null ? new() : new() { one };
        }
        var j = await Get($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc={Country}&l=english", ct).ConfigureAwait(false);
        var list = new List<SteamApp>();
        if (j?["items"] is JsonArray items)
            foreach (var it in items)
            {
                if (it is not JsonObject o || (o["type"]?.GetValue<string>() ?? "app") != "app") continue;
                if (o["id"] is not JsonValue iv || !iv.TryGetValue<int>(out var appid) || appid <= 0) continue;
                var name = Clean(o["name"]?.GetValue<string>());
                if (name == null || list.Any(x => x.AppId == appid)) continue;
                list.Add(new SteamApp(appid, name, SteamImage(o["tiny_image"]?.GetValue<string>())));
                if (list.Count >= limit) break;
            }
        return list;
    }

    /// <summary>Store details for one app id; null when unknown or not a game.</summary>
    public async Task<SteamApp?> DetailsAsync(int appId, CancellationToken ct = default)
    {
        var j = await Get($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&cc={Country}&l=english", ct).ConfigureAwait(false);
        if (j?[appId.ToString()] is not JsonObject e || e["success"]?.GetValue<bool>() != true || e["data"] is not JsonObject d) return null;
        var type = d["type"]?.GetValue<string>() ?? "game";
        if (type != "game") return null;
        var name = Clean(d["name"]?.GetValue<string>());
        return name == null ? null : new SteamApp(appId, name, SteamImage(d["header_image"]?.GetValue<string>()));
    }

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
