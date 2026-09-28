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
        Assert.Contains("/api/storesearch/?term=subnautica&cc=nl", fb.Calls.Single().path);
        Assert.Empty(await new SteamStore(fb).SearchAsync("a"));   // too short: no request
        Assert.Single(fb.Calls);
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
        Assert.Empty(await s.SearchAsync("5"));
        fb.Offline = true;
        Assert.Equal("offline", (await Assert.ThrowsAsync<AccountException>(() => s.SearchAsync("264710"))).Code);
    }

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
