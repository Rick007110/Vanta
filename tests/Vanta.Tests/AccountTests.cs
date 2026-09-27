using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vanta.Core;
using Vanta.Core.Account;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

/// <summary>Fake backend: records requests, answers with a delegate.</summary>
public sealed class FakeBackend : HttpMessageHandler
{
    public readonly List<(string method, string path, string? auth, string body)> Calls = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage>? Respond;
    public bool Offline;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add((r.Method.Method, r.RequestUri!.PathAndQuery, r.Headers.Authorization?.ToString(), body));
        if (Offline) throw new HttpRequestException("offline");
        return Respond?.Invoke(r, body) ?? Json(404, new { error = "not_found" });
    }
    public static HttpResponseMessage Json(int status, object o) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    public int Count(string method, string pathPrefix) { lock (Calls) return Calls.Count(c => c.method == method && c.path.StartsWith(pathPrefix)); }
}

public class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-acct-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Api = new("https://api.test/");
    private static readonly AccountSession Session = new("vt_" + new string('a', 43), DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), new AccountUser("1234567890", "tester", null));
    private readonly List<object> _sent = new();
    public AccountTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AccountService Service(FakeBackend fb, ITokenStore? store = null, Settings? s = null) =>
        new(s ?? new Settings(), store ?? new MemoryTokenStore(), Path.Combine(_dir, "queue.json"), _ => Task.CompletedTask, o => { lock (_sent) _sent.Add(o); }, Api, fb, startTimer: false);

    private static ReportItem Item(string cheat = "godmode", string status = "broken", string? note = "crash") =>
        new("the-last-caretaker", cheat, "fileVersion=0.8.5.651238", status, note, "0.3.0", "0.8.5", "The Last Caretaker", "God mode");

    // ---------------- login (loopback + PKCE) ----------------
    private static async Task<HttpResponseMessage> HitLoopback(string authUrl, Func<Dictionary<string, string>, string> query)
    {
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl).Query);
        var d = q.AllKeys.ToDictionary(k => k!, k => q[k]!);
        using var h = new HttpClient();
        return await h.GetAsync($"http://127.0.0.1:{d["port"]}{query(d)}");
    }

    private static FakeBackend TokenBackend(Func<string>? challenge)
    {
        var fb = new FakeBackend();
        fb.Respond = (r, body) =>
        {
            if (r.RequestUri!.AbsolutePath != "/auth/token") return FakeBackend.Json(404, new { error = "not_found" });
            var j = JsonNode.Parse(body)!;
            var ver = j["verifier"]!.GetValue<string>();
            var ch = AccountClient.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(ver)));
            if (j["code"]!.GetValue<string>() != "one-time" || ch != challenge!()) return FakeBackend.Json(400, new { error = "invalid_verifier" });
            return FakeBackend.Json(200, new { token = "vt_" + new string('b', 43), expires = 1900000000, user = new { id = "42", username = "tester", avatarUrl = "https://cdn.discordapp.com/embed/avatars/0.png" } });
        };
        return fb;
    }

    [Fact]
    public async Task Login_loopback_pkce_roundtrip()
    {
        string? challenge = null;
        var fb = TokenBackend(() => challenge!);
        var client = new AccountClient(Api, fb);
        var responses = new List<HttpStatusCode>();
        var s = await client.LoginAsync(async url =>
        {
            Assert.StartsWith("https://api.test/auth/start?port=", url);
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            challenge = q["challenge"];
            Assert.Matches("^[A-Za-z0-9_-]{43}$", challenge!);
            Assert.True(int.Parse(q["port"]!) >= 1024);
            _ = Task.Run(async () =>
            {
                responses.Add((await HitLoopback(url, _ => "/favicon.ico")).StatusCode);                                     // ignored
                responses.Add((await HitLoopback(url, d => "/callback?code=x&state=someone-else")).StatusCode);             // stale tab: ignored
                var ok = await HitLoopback(url, d => $"/callback?code=one-time&state={d["state"]}");
                responses.Add(ok.StatusCode);
                Assert.Contains("ingelogd", (await ok.Content.ReadAsStringAsync()).ToLowerInvariant());
            });
            await Task.CompletedTask;
        });
        Assert.Equal("vt_" + new string('b', 43), s.Token);
        Assert.Equal("tester", s.User.Username);
        await Task.Delay(100);
        Assert.Equal(new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.OK }, responses);
    }

    [Fact]
    public async Task Login_cancelled_in_discord_and_timeout()
    {
        var client = new AccountClient(Api, new FakeBackend());
        var e = await Assert.ThrowsAsync<AccountException>(() => client.LoginAsync(url =>
        {
            _ = Task.Run(() => HitLoopback(url, d => $"/callback?error=access_denied&state={d["state"]}"));
            return Task.CompletedTask;
        }));
        Assert.Equal("login_cancelled", e.Code);
        client.LoginTimeout = TimeSpan.FromMilliseconds(300);
        var t = await Assert.ThrowsAsync<AccountException>(() => client.LoginAsync(_ => Task.CompletedTask));
        Assert.Equal("login_timeout", t.Code);
    }

    [Fact]
    public async Task Service_login_saves_session_and_pushes_payload()
    {
        string? challenge = null;
        var fb = TokenBackend(() => challenge!);
        var store = new MemoryTokenStore();
        var svc = new AccountService(new Settings(), store, null, url =>
        {
            challenge = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["challenge"];
            _ = Task.Run(() => HitLoopback(url, d => $"/callback?code=one-time&state={d["state"]}"));
            return Task.CompletedTask;
        }, o => { lock (_sent) _sent.Add(o); }, Api, fb, startTimer: false);
        await svc.LoginAsync();
        Assert.Equal("tester", store.Load()!.User.Username);
        var last = JsonSerializer.SerializeToNode(_sent.Last())!;
        Assert.True(last["loggedIn"]!.GetValue<bool>());
        Assert.Equal("tester", last["user"]!["username"]!.GetValue<string>());
        Assert.Contains(_sent, o => JsonSerializer.SerializeToNode(o)!["busy"]!.GetValue<bool>());
    }

    // ---------------- reports + queue ----------------
    [Fact]
    public async Task Report_sent_with_bearer_and_fields()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true, community = new { works = 1, broken = 5, status = "open", fixed_in_version = (string?)null } }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item());
        Assert.True(r.ok);
        Assert.Equal(5, r.community!.Broken);
        var call = fb.Calls.Single();
        Assert.Equal(("POST", "/reports", "Bearer " + Session.Token), (call.method, call.path, call.auth));
        var b = JsonNode.Parse(call.body)!;
        Assert.Equal("broken", b["status"]!.GetValue<string>());
        Assert.Equal("crash", b["note"]!.GetValue<string>());
        Assert.Equal("fileVersion=0.8.5.651238", b["fingerprint"]!.GetValue<string>());
        Assert.Equal(0, svc.Queue.Count);
    }

    [Fact]
    public async Task Offline_reports_are_queued_persisted_deduplicated_and_flushed_later()
    {
        var fb = new FakeBackend { Offline = true, Respond = (_, _) => FakeBackend.Json(200, new { ok = true }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r1 = await svc.ReportAsync(Item(status: "broken"));
        var r2 = await svc.ReportAsync(Item(status: "works", note: null));   // same cheat: replaces the queued one
        var r3 = await svc.ReportAsync(Item(cheat: "ammo"));
        Assert.True(r1.queued && r2.queued && r3.queued && !r1.ok);
        Assert.Equal("offline", r1.error);
        Assert.Equal(2, svc.Queue.Count);
        // restart: the queue survives
        var svc2 = Service(fb, store);
        Assert.Equal(2, svc2.Queue.Count);
        Assert.Equal("works", svc2.Queue.Snapshot().Single(q => q.Item.CheatId == "godmode").Item.Status);
        fb.Offline = false;
        await svc2.FlushAsync();
        Assert.Equal(0, svc2.Queue.Count);
        Assert.Equal(0, new ReportQueue(Path.Combine(_dir, "queue.json")).Count);
        Assert.Equal(2, fb.Calls.Count(c => c.method == "POST" && c.path == "/reports" && c.body.Length > 0) - 3);
    }

    [Fact]
    public async Task Expired_session_logs_out_and_keeps_report_queued()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(401, new { error = "unauthorized" }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item());
        Assert.Equal("session_expired", r.error);
        Assert.True(r.queued);
        Assert.Null(svc.Session);
        Assert.Null(store.Load());
        Assert.Equal("session_expired", JsonSerializer.SerializeToNode(_sent.Last())!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rejected_reports_are_dropped_not_retried()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(403, new { error = "banned" }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item());
        Assert.False(r.ok || r.queued);
        Assert.Equal("banned", r.error);
        Assert.Equal(0, svc.Queue.Count);
        Assert.NotNull(svc.Session);
    }

    [Fact]
    public async Task Not_logged_in_or_not_configured()
    {
        var svc = Service(new FakeBackend());
        Assert.Equal("not_logged_in", (await svc.ReportAsync(Item())).error);
        var off = new AccountService(new Settings(), new MemoryTokenStore(), null, _ => Task.CompletedTask, _ => { }, backend: null, startTimer: false);
        if (!off.Configured) Assert.Equal("not_configured", (await off.ReportAsync(Item())).error);
    }

    [Fact]
    public async Task Withdraw_sends_delete_with_key()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true, removed = true }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item(), withdraw: true);
        Assert.True(r.ok);
        var c = fb.Calls.Single();
        Assert.Equal(("DELETE", "/reports"), (c.method, c.path));
        Assert.Equal("godmode", JsonNode.Parse(c.body)!["cheat_id"]!.GetValue<string>());
    }

    // ---------------- account ----------------
    [Fact]
    public async Task Delete_account_clears_everything_only_after_server_confirmed()
    {
        var fb = new FakeBackend { Offline = true };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        await svc.ReportAsync(Item());           // queued
        Assert.False(await svc.DeleteAsync());   // offline: nothing deleted locally
        Assert.NotNull(store.Load());
        fb.Offline = false;
        fb.Respond = (r, _) => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == "/me" ? FakeBackend.Json(200, new { ok = true }) : FakeBackend.Json(500, new { });
        Assert.True(await svc.DeleteAsync());
        Assert.Null(store.Load());
        Assert.Null(svc.Session);
        Assert.Equal(0, svc.Queue.Count);
        Assert.Contains(_sent, o => JsonSerializer.SerializeToNode(o)!["type"]!.GetValue<string>() == "accountDeleted");
    }

    [Fact]
    public async Task Logout_clears_local_even_when_server_unreachable()
    {
        var fb = new FakeBackend { Offline = true };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        await svc.LogoutAsync();
        Assert.Null(store.Load());
        Assert.Null(svc.Session);
        Assert.Equal(1, fb.Count("POST", "/auth/logout"));
    }

    [Fact]
    public void Expired_stored_session_is_ignored()
    {
        var store = new MemoryTokenStore();
        store.Save(Session with { Expires = 1000 });
        var svc = Service(new FakeBackend(), store);
        Assert.Null(svc.Session);
        Assert.Null(store.Load());
    }

    // ---------------- community + usage ----------------
    [Fact]
    public async Task Community_is_cached_for_five_minutes()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { game_id = "g", cheats = new { godmode = new { works = 2, broken = 12, status = "open" } } }) };
        var svc = Service(fb);
        var now = DateTimeOffset.UtcNow;
        svc.Now = () => now;
        var d = await svc.CommunityAsync("the-last-caretaker", "fileVersion=1");
        Assert.Equal(12, d!["godmode"].Broken);
        await svc.CommunityAsync("the-last-caretaker", "fileVersion=1");
        Assert.Single(fb.Calls);
        Assert.Equal("/community/the-last-caretaker?fingerprint=fileVersion%3D1", fb.Calls[0].path);
        now = now.AddMinutes(6);
        await svc.CommunityAsync("the-last-caretaker", "fileVersion=1");
        Assert.Equal(2, fb.Calls.Count);
        fb.Offline = true;   // offline: last known counts
        Assert.NotNull(await svc.CommunityAsync("the-last-caretaker", "fileVersion=1", force: true));
    }

    [Fact]
    public async Task Usage_only_when_opted_in()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true }) };
        var s = new Settings();
        var svc = Service(fb, s: s);
        svc.CountUsage("g", "fly");
        await svc.FlushUsageAsync();
        Assert.Empty(fb.Calls);
        s.ShareUsage = true;
        svc.CountUsage("g", "fly"); svc.CountUsage("g", "fly"); svc.CountUsage("g", "ammo");
        await svc.FlushUsageAsync();
        var c = fb.Calls.Single();
        Assert.Equal(("POST", "/usage", null), (c.method, c.path, c.auth));   // anonymous: no token
        var b = JsonNode.Parse(c.body)!;
        Assert.Equal(2, b["counts"]!["fly"]!.GetValue<int>());
        Assert.Equal(0, svc.PendingUsage);
    }

    // ---------------- helpers ----------------
    [Fact]
    public void Backend_resolution_and_validation()
    {
        var s = new Settings { BackendUrl = "https://from-settings.example" };
        Assert.Equal("https://from-env.example/", AccountService.ResolveBackend(s, "https://from-env.example")!.ToString());
        Assert.Equal("https://from-settings.example/", AccountService.ResolveBackend(s, null)!.ToString());
        Assert.Equal("http://127.0.0.1:8787/", AccountService.ResolveBackend(new Settings { BackendUrl = "http://127.0.0.1:8787" }, null)!.ToString());
        Assert.Null(AccountService.ResolveBackend(new Settings { BackendUrl = "http://evil.example" }, null) is { } u && u.Host == "evil.example" ? u : null);
        Assert.Null(AccountService.ResolveBackend(new Settings { BackendUrl = "https://x.example/?a=1" }, "") is { } v && v.Host == "x.example" ? v : null);
    }

    [Fact]
    public void Report_fingerprint_is_printable_ascii()
    {
        Assert.Equal("fileVersion=0.8.5.651238", AccountService.ReportFingerprint("fileVersion=0.8.5.651238"));
        var h = AccountService.ReportFingerprint("label:Versión 1.0");
        Assert.Matches("^h:[0-9a-f]{40}$", h);
        Assert.Equal(h, AccountService.ReportFingerprint("label:Versión 1.0"));
        Assert.StartsWith("h:", AccountService.ReportFingerprint(new string('a', 201)));
    }

    [SkippableFact]
    public void Dpapi_store_roundtrip_on_windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only (covered by account-selftest under Wine)");
        var st = new DpapiTokenStore(Path.Combine(_dir, "account.bin"));
        st.Save(Session);
        Assert.DoesNotContain(Session.Token, File.ReadAllText(st.File));
        Assert.Equal(Session.Token, st.Load()!.Token);
        st.Clear();
        Assert.Null(st.Load());
    }

    // ---------------- controller integration ----------------
    [Fact]
    public async Task Controller_report_message_builds_item_and_answers_with_reportResult()
    {
        var sent = new List<string>();
        var g = TestUtil.Tlc();
        var status = new StatusStore(Path.Combine(_dir, "status.json"));
        var c = new TrainerController(new MemCatalog(g), new FakeProvider(), new Settings(), o => { lock (sent) sent.Add(JsonSerializer.Serialize(o, VJson.Compact)); }, status: status);
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true, community = new { works = 0, broken = 1, status = "open" } }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        c.Account = Service(fb, store);
        var cheat = g.Cheats.First(x => !x.Hidden);
        var ack = JsonSerializer.Serialize(c.HandleUi(JsonDocument.Parse($"{{\"type\":\"report\",\"gameId\":\"{g.Id}\",\"id\":\"{cheat.Id}\",\"status\":\"broken\",\"note\":\"  {new string('x', 400)}  \"}}").RootElement), VJson.Compact);
        Assert.Contains("\"ok\":true", ack);
        for (int i = 0; i < 100 && !sent.Any(s => s.Contains("reportResult")); i++) await Task.Delay(20);
        var res = JsonNode.Parse(sent.First(s => s.Contains("\"reportResult\"")))!;
        Assert.True(res["ok"]!.GetValue<bool>());
        Assert.Equal(1, res["community"]!["broken"]!.GetValue<int>());
        var body = JsonNode.Parse(fb.Calls.Single().body)!;
        Assert.Equal(g.Id, body["game_id"]!.GetValue<string>());
        Assert.Equal(300, body["note"]!.GetValue<string>().Length);
        Assert.Equal(Branding.Version, body["vanta_version"]!.GetValue<string>());
        Assert.Equal(g.Name, body["game_name"]!.GetValue<string>());
        // invalid status is refused synchronously; without an account the feature is off
        Assert.Contains("\"ok\":false", JsonSerializer.Serialize(c.HandleUi(JsonDocument.Parse($"{{\"type\":\"report\",\"gameId\":\"{g.Id}\",\"id\":\"{cheat.Id}\",\"status\":\"maybe\"}}").RootElement), VJson.Compact));
        c.Account = null;
        Assert.Contains("\"ok\":false", JsonSerializer.Serialize(c.HandleUi(JsonDocument.Parse($"{{\"type\":\"getCommunity\",\"gameId\":\"{g.Id}\"}}").RootElement), VJson.Compact));
    }
}
