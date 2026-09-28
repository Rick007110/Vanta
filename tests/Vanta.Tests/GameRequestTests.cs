using System.Text.Json;
using System.Text.Json.Nodes;
using Vanta.Core;
using Vanta.Core.Account;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class GameRequestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-req-" + Guid.NewGuid().ToString("N"));
    private static readonly SupabaseConfig Api = new(new Uri("https://proj.supabase.test/"), "sb_publishable_test");
    private static readonly AccountSession Session = new("eyJ.access." + new string('a', 20), DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), new AccountUser("1234567890", "tester", null), "rt-1");
    public GameRequestTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AccountService Service(FakeBackend fb, bool signedIn) =>
        new(new Settings(), Store(signedIn), Path.Combine(_dir, "queue.json"), _ => Task.CompletedTask, _ => { }, Api, fb, startTimer: false);

    private static MemoryTokenStore Store(bool signedIn) { var s = new MemoryTokenStore(); if (signedIn) s.Save(Session); return s; }

    private static object Req(int appid, string name, int votes, bool voted = false) =>
        new { appid, name, cover_url = (string?)null, status = "open", note = (string?)null, votes, votes_7d = votes, voted, created = 1, updated = 1 };

    // ---------------- Steam store ----------------
    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("  app 42 ", 42)]
    [InlineData("https://store.steampowered.com/app/1172470/Apex_Legends/", 1172470)]
    [InlineData("steamdb.info/app/730/", 730)]
    [InlineData("Subnautica", null)]
    [InlineData("0", null)]
    [InlineData("1234567890", null)]
    [InlineData("", null)]
    public void ParseAppId(string input, int? expected) => Assert.Equal(expected, SteamStore.ParseAppId(input));

    [Fact]
    public async Task Search_by_name_keeps_apps_only_and_filters_images()
    {
        var fb = new FakeBackend
        {
            Respond = (r, _) => FakeBackend.Json(200, new
            {
                total = 3,
                items = new object[]
                {
                    new { type = "app", name = "Subnautica", id = 264710, tiny_image = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/264710/capsule_231x87.jpg" },
                    new { type = "sub", name = "Bundle", id = 1, tiny_image = "" },
                    new { type = "app", name = "Evil\u0001 <b>", id = 99, tiny_image = "javascript:alert(1)" },
                    new { type = "app", name = "Subnautica", id = 264710, tiny_image = "" },
                },
            }),
        };
        var list = await new SteamStore(fb).SearchAsync("subnautica");
        Assert.Equal(2, list.Count);
        Assert.Equal(264710, list[0].AppId);
        Assert.StartsWith("https://shared.akamai.steamstatic.com/", list[0].Image);
        Assert.Equal("Evil <b>", list[1].Name);          // control chars removed; HTML is escaped by the UI
        Assert.Null(list[1].Image);
        Assert.Contains("/api/storesearch/?term=subnautica&cc=nl", fb.Calls[0].path);
        Assert.Equal(3, fb.Calls.Count);                  // + one appdetails per unique app (answer unusable here: judged by name)
        Assert.Empty(await new SteamStore(fb).SearchAsync("a"));   // too short: no request
        Assert.Equal(3, fb.Calls.Count);
    }

    [Fact]
    public async Task Search_by_appid_uses_appdetails_and_skips_non_games()
    {
        var fb = new FakeBackend
        {
            Respond = (r, _) => r.RequestUri!.Query.Contains("appids=264710")
                ? FakeBackend.Json(200, new Dictionary<string, object> { ["264710"] = new { success = true, data = new { type = "game", name = "Subnautica", steam_appid = 264710, header_image = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/264710/header.jpg" } } })
                : FakeBackend.Json(200, new Dictionary<string, object> { ["5"] = new { success = true, data = new { type = "dlc", name = "Soundtrack" } } }),
        };
        var s = new SteamStore(fb);
        var one = Assert.Single(await s.SearchAsync("https://store.steampowered.com/app/264710/"));
        Assert.Equal("Subnautica", one.Name);
        Assert.Equal("https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/264710/header.jpg", one.Cover);
        Assert.Equal("not_a_game", (await Assert.ThrowsAsync<AccountException>(() => s.SearchAsync("5"))).Code);   // DLC without base game
        fb.Offline = true;
        Assert.Single(await s.SearchAsync("264710"));      // cached
        Assert.Equal("offline", (await Assert.ThrowsAsync<AccountException>(() => new SteamStore(fb).SearchAsync("264710"))).Code);
    }

    // ---------------- base games only (appdetails type) ----------------
    private static object Hit(int id, string name) => new { type = "app", name, id, tiny_image = "" };
    private static HttpResponseMessage Details(int id, string type, string name, int? fullgame = null, string? fullName = null) =>
        FakeBackend.Json(200, new Dictionary<string, object> { [id.ToString()] = new { success = true, data = fullgame == null
            ? (object)new { type, name, steam_appid = id, header_image = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg" }
            : new { type, name, steam_appid = id, fullgame = new { appid = fullgame.Value.ToString(), name = fullName } } } });

    private static readonly object AvatarSearch = new
    {
        total = 7,
        items = new[]
        {
            Hit(2840770, "Avatar: Frontiers of Pandora™"), Hit(2840771, "VALLEY OF MO’ARA STARTER PACK - AVATAR: FRONTIERS OF PANDORA™"),
            Hit(2840772, "Avatar: Frontiers of Pandora™ - Season Pass"), Hit(2840773, "Avatar: Frontiers of Pandora™ Soundtrack"),
            Hit(2840774, "Avatar: Frontiers of Pandora™ Demo"), Hit(700, "Pandora Map Editor"), Hit(800, "Avatar Tales"),
        },
    };

    private static HttpResponseMessage AvatarDetails(HttpRequestMessage r)
    {
        var q = r.RequestUri!.Query;
        if (r.RequestUri.AbsolutePath.Contains("storesearch")) return FakeBackend.Json(200, AvatarSearch);
        if (q.Contains("appids=2840770&")) return Details(2840770, "game", "Avatar: Frontiers of Pandora™");
        if (q.Contains("appids=2840771&")) return Details(2840771, "dlc", "VALLEY OF MO’ARA STARTER PACK", 2840770, "Avatar: Frontiers of Pandora™");
        if (q.Contains("appids=2840772&")) return Details(2840772, "dlc", "Season Pass", 2840770, "Avatar: Frontiers of Pandora™");
        if (q.Contains("appids=2840773&")) return Details(2840773, "music", "Soundtrack", 2840770, "Avatar: Frontiers of Pandora™");
        if (q.Contains("appids=2840774&")) return Details(2840774, "demo", "Demo", 2840770, "Avatar: Frontiers of Pandora™");
        if (q.Contains("appids=700&")) return Details(700, "application", "Pandora Map Editor");
        if (q.Contains("appids=800&")) return Details(800, "dlc", "Avatar Tales", 900, "Tales of Pandora");
        return FakeBackend.Json(200, new Dictionary<string, object> { ["0"] = new { success = false } });
    }

    [Fact]
    public async Task Search_by_name_returns_base_games_only_and_maps_dlc_to_the_base_game()
    {
        var fb = new FakeBackend { Respond = (r, _) => AvatarDetails(r) };
        var s = new SteamStore(fb);
        var list = await s.SearchAsync("avatar");
        Assert.Equal(new[] { (2840770, "Avatar: Frontiers of Pandora™"), (900, "Tales of Pandora") }, list.Select(x => (x.AppId, x.Name)));
        Assert.Equal(7, s.Lookups);
        Assert.All(fb.Calls.Skip(1), c => Assert.Contains("/api/appdetails?appids=", c.path));
        Assert.All(fb.Calls.Skip(1), c => Assert.Contains("filters=basic", c.path));
        await s.SearchAsync("avatar");                   // types are cached: only the store search is sent again
        Assert.Equal(7, s.Lookups);
        Assert.Equal(9, fb.Calls.Count);
    }

    [Fact]
    public async Task Pasted_dlc_resolves_to_the_base_game_and_tools_are_rejected()
    {
        var s = new SteamStore(new FakeBackend { Respond = (r, _) => AvatarDetails(r) });
        var one = Assert.Single(await s.SearchAsync("https://store.steampowered.com/app/2840771/VALLEY_OF_MOARA/"));
        Assert.Equal((2840770, "Avatar: Frontiers of Pandora™"), (one.AppId, one.Name));
        Assert.StartsWith("https://shared.akamai.steamstatic.com/", one.Image);   // from the base game's own details
        Assert.Equal(2840770, Assert.Single(await s.SearchAsync("2840773")).AppId);   // soundtrack
        Assert.Equal(2840770, Assert.Single(await s.SearchAsync("app 2840774")).AppId);   // demo
        Assert.Equal("not_a_game", (await Assert.ThrowsAsync<AccountException>(() => s.SearchAsync("700"))).Code);
        Assert.Empty(await s.SearchAsync("123456"));        // unknown to Steam
        Assert.Null(await s.DetailsAsync(2840771));
        Assert.Equal("Avatar: Frontiers of Pandora™", (await s.DetailsAsync(2840770))!.Name);
    }

    private sealed class SlowSteam : HttpMessageHandler
    {
        public int Active, MaxActive, Details;
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.RequestUri!.AbsolutePath.Contains("storesearch"))
                return FakeBackend.Json(200, new { items = Enumerable.Range(1, 12).Select(i => Hit(1000 + i, i % 3 == 0 ? $"Game {i} Soundtrack" : $"Game {i}")).ToArray() });
            Interlocked.Increment(ref Details);
            var now = Interlocked.Increment(ref Active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            try
            {
                await Task.Delay(40, ct);
                if (Override?.Invoke(r) is { } o) return o;
                var id = int.Parse(System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query)["appids"]!);
                return Details(id, (id - 1000) % 3 == 0 ? "music" : "game", $"Game {id - 1000}");
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    [Fact]
    public async Task Type_lookups_are_limited_in_parallel()
    {
        var h = new SlowSteam();
        var list = await new SteamStore(h).SearchAsync("game");
        Assert.Equal(8, list.Count);
        Assert.DoesNotContain(list, x => x.Name.Contains("Soundtrack"));
        Assert.Equal(12, h.Details);
        Assert.InRange(h.MaxActive, 2, SteamStore.MaxParallel);
    }

    [Fact]
    public async Task Rate_limit_pauses_lookups_and_falls_back_to_names()
    {
        var h = new SlowSteam { Override = _ => FakeBackend.Json(429, new { }) };
        var s = new SteamStore(h);
        var list = await s.SearchAsync("game");
        Assert.Equal(8, list.Count);                       // "… Soundtrack" dropped by name
        Assert.DoesNotContain(list, x => x.Name.Contains("Soundtrack"));
        Assert.InRange(h.Details, 1, SteamStore.MaxParallel);   // after the first 429 no new lookups are started
        var before = h.Details;
        Assert.Equal(8, (await s.SearchAsync("game")).Count);
        Assert.Equal(before, h.Details);                   // still paused
        Assert.Equal("rate_limited", (await Assert.ThrowsAsync<AccountException>(() => s.SearchAsync("1001"))).Code);

        var nul = new SlowSteam { Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("null") } };
        Assert.Equal(8, (await new SteamStore(nul).SearchAsync("game")).Count);   // Steam's throttled "null" answer
    }

    [Theory]
    [InlineData("Avatar: Frontiers of Pandora™ Soundtrack", true)]
    [InlineData("VALLEY OF MO’ARA STARTER PACK - AVATAR: FRONTIERS OF PANDORA™", true)]
    [InlineData("Cyberpunk 2077 - Season Pass", true)]
    [InlineData("Hades II Demo", true)]
    [InlineData("Skyrim - Anniversary Upgrade", true)]
    [InlineData("Far Cry 6 - Lost Between Worlds Pack", true)]
    [InlineData("The Jackbox Party Pack 9", false)]
    [InlineData("Subnautica", false)]
    [InlineData("Baldur's Gate 3", false)]
    [InlineData("Demolish & Build 2018", false)]
    public void Name_fallback_recognises_extras(string name, bool extra) => Assert.Equal(extra, SteamStore.LooksLikeExtra(name));

    // ---------------- requests API ----------------
    [Fact]
    public async Task List_is_public_and_uses_the_token_when_signed_in()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { items = new[] { Req(264710, "Subnautica", 12, true), Req(0, "bad", 1) }, logged_in = true }) };
        var (items, loggedIn, error) = await Service(fb, signedIn: true).GameRequestsAsync();
        Assert.Null(error);
        Assert.True(loggedIn);
        var r = Assert.Single(items!);
        Assert.Equal((264710, 12, true), (r.AppId, r.Votes, r.Voted));
        Assert.Equal("Bearer " + Session.Token, fb.Calls.Single().auth);
        Assert.Equal("/rest/v1/rpc/vanta_game_requests", fb.Calls.Single().path);

        var anon = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { items = Array.Empty<object>(), logged_in = false }) };
        var res = await Service(anon, signedIn: false).GameRequestsAsync();
        Assert.False(res.loggedIn);
        Assert.Null(anon.Calls.Single().auth);             // publishable key only: no bearer
    }

    [Fact]
    public async Task List_reports_server_not_ready_before_the_sql_is_applied()
    {
        var (items, _, error) = await Service(new FakeBackend(), signedIn: false).GameRequestsAsync();
        Assert.Null(items);
        Assert.Equal("server_not_ready", error);
    }

    [Fact]
    public async Task Vote_and_unvote_send_the_right_rpc()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true, created = true, request = Req(264710, "Subnautica", 1, true) }) };
        var svc = Service(fb, signedIn: true);
        var v = await svc.VoteGameAsync(264710, "Subnautica", "https://evil.example/x.jpg", unvote: false);
        Assert.True(v.ok);
        Assert.True(v.request!.Voted);
        var body = JsonNode.Parse(fb.Calls[0].body)!;
        Assert.Equal("/rest/v1/rpc/vanta_request_game", fb.Calls[0].path);
        Assert.Equal(264710, body["p_appid"]!.GetValue<int>());
        Assert.Null(body["p_cover_url"]);                  // non-Steam cover never leaves the app
        await svc.VoteGameAsync(264710, null, null, unvote: true);
        Assert.Equal("/rest/v1/rpc/vanta_unvote_game", fb.Calls[1].path);

        Assert.Equal("not_logged_in", (await Service(fb, signedIn: false).VoteGameAsync(1, "x", null, false)).error);
        var closed = new FakeBackend { Respond = (_, _) => FakeBackend.Json(409, new { code = "P0001", message = "request_closed", details = "status=added" }) };
        Assert.Equal("request_closed", (await Service(closed, signedIn: true).VoteGameAsync(1, "x", null, false)).error);
    }

    [Fact]
    public async Task Controller_routes_request_messages()
    {
        var sent = new List<string>();
        var g = TestUtil.Tlc();
        var c = new TrainerController(new MemCatalog(g), new FakeProvider(), new Settings(), o => { lock (sent) sent.Add(JsonSerializer.Serialize(o, VJson.Compact)); },
            status: new StatusStore(Path.Combine(_dir, "status.json")));
        var fb = new FakeBackend
        {
            Respond = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("vanta_request_game")
                ? FakeBackend.Json(200, new { ok = true, request = Req(264710, "Subnautica", 1, true) })
                : FakeBackend.Json(200, new { items = new[] { Req(264710, "Subnautica", 1, true) }, logged_in = true }),
        };
        c.Account = Service(fb, signedIn: true);
        c.Steam = new SteamStore(new FakeBackend { Offline = true });
        JsonElement M(string j) => JsonDocument.Parse(j).RootElement.Clone();
        string Ack(string j) => JsonSerializer.Serialize(c.HandleUi(M(j)), VJson.Compact);

        Assert.Contains("\"ok\":true", Ack("{\"type\":\"requestVote\",\"appid\":264710,\"name\":\"Subnautica\"}"));
        Assert.Contains("\"ok\":false", Ack("{\"type\":\"requestVote\",\"appid\":\"x\"}"));
        Assert.Contains("\"ok\":true", Ack("{\"type\":\"steamSearch\",\"term\":\"subnautica\"}"));
        async Task<JsonNode> Wait(string type)
        {
            for (int i = 0; i < 150; i++) { lock (sent) { var s = sent.FirstOrDefault(x => x.Contains($"\"type\":\"{type}\"")); if (s != null) return JsonNode.Parse(s)!; } await Task.Delay(20); }
            throw new TimeoutException(type);
        }
        Assert.True((await Wait("requestVoteResult"))["ok"]!.GetValue<bool>());
        var list = await Wait("requests");                    // refreshed after the vote
        Assert.Equal(264710, list["items"]![0]!["appid"]!.GetValue<int>());
        var search = await Wait("steamSearch");
        Assert.False(search["ok"]!.GetValue<bool>());
        Assert.Equal("offline", search["error"]!.GetValue<string>());
        c.Account = null;
        Assert.Contains("\"ok\":false", Ack("{\"type\":\"getRequests\"}"));
    }
}
