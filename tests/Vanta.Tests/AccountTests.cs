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
    public readonly List<(string method, string path, string? auth, string body, string? apikey)> Calls = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage>? Respond;
    public bool Offline;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add((r.Method.Method, r.RequestUri!.PathAndQuery, r.Headers.Authorization?.ToString(), body,
            r.Headers.TryGetValues("apikey", out var k) ? k.First() : null));
        if (Offline) throw new HttpRequestException("offline");
        return Respond?.Invoke(r, body) ?? Json(404, new { code = "PGRST202", message = "Could not find the function" });
    }
    public static HttpResponseMessage Json(int status, object o) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    public int Count(string method, string pathPrefix) { lock (Calls) return Calls.Count(c => c.method == method && c.path.StartsWith(pathPrefix)); }
}

public class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-acct-" + Guid.NewGuid().ToString("N"));
    private static readonly SupabaseConfig Api = new(new Uri("https://proj.supabase.test/"), "sb_publishable_test");
    private static readonly AccountSession Session = new("eyJ.access." + new string('a', 20), DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), new AccountUser("1234567890", "tester", null), "rt-1");
    private readonly List<object> _sent = new();
    public AccountTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AccountService Service(FakeBackend fb, ITokenStore? store = null, Settings? s = null) =>
        new(s ?? new Settings(), store ?? new MemoryTokenStore(), Path.Combine(_dir, "queue.json"), _ => Task.CompletedTask, o => { lock (_sent) _sent.Add(o); }, Api, fb, startTimer: false);

    private static ReportItem Item(string cheat = "godmode", string status = "broken", string? note = "crash") =>
        new("the-last-caretaker", cheat, "fileVersion=0.8.5.651238", status, note, "0.3.0", "0.8.5", "The Last Caretaker", "God mode");

    // ---------------- login (loopback + PKCE) ----------------
    /// <summary>Plays the browser after Supabase Auth: calls the loopback redirect_to with a query built from its port/state.</summary>
    private static async Task<HttpResponseMessage> HitLoopback(string authUrl, Func<Dictionary<string, string>, string> query)
    {
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl).Query);
        var redirect = new Uri(q["redirect_to"]!);
        var d = new Dictionary<string, string> { ["port"] = redirect.Port.ToString(), ["state"] = System.Web.HttpUtility.ParseQueryString(redirect.Query)["state"]!,
                                                 ["challenge"] = q["code_challenge"]! };
        using var h = new HttpClient();
        return await h.GetAsync($"http://127.0.0.1:{d["port"]}{query(d)}");
    }

    private static object SessionJson(string access, string refresh, long exp) => new
    {
        access_token = access, token_type = "bearer", expires_in = 3600, expires_at = exp, refresh_token = refresh,
        user = new { id = "0b6f0000-0000-0000-0000-000000000001", user_metadata = new { provider_id = "42", full_name = "tester_name", custom_claims = new { global_name = "tester" }, avatar_url = "https://cdn.discordapp.com/embed/avatars/0.png" } },
    };

    private static FakeBackend TokenBackend(Func<string>? challenge)
    {
        var fb = new FakeBackend();
        fb.Respond = (r, body) =>
        {
            if (r.RequestUri!.PathAndQuery != "/auth/v1/token?grant_type=pkce") return FakeBackend.Json(404, new { error_code = "not_found", msg = "x" });
            var j = JsonNode.Parse(body)!;
            var ver = j["code_verifier"]!.GetValue<string>();
            var ch = AccountClient.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(ver)));
            if (j["auth_code"]!.GetValue<string>() != "one-time" || ch != challenge!()) return FakeBackend.Json(400, new { code = 400, error_code = "bad_code_verifier", msg = "mismatch" });
            return FakeBackend.Json(200, SessionJson("eyJ.new.token", "rt-new", 1900000000));
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
            Assert.StartsWith("https://proj.supabase.test/auth/v1/authorize?provider=discord&redirect_to=http%3A%2F%2F127.0.0.1%3A", url);
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            challenge = q["code_challenge"];
            Assert.Equal("s256", q["code_challenge_method"]);
            Assert.Null(q["scopes"]);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", challenge!);
            var redirect = new Uri(q["redirect_to"]!);
            Assert.Equal("/callback", redirect.AbsolutePath);
            Assert.True(redirect.Port >= 1024);
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
        Assert.Equal("eyJ.new.token", s.Token);
        Assert.Equal("rt-new", s.RefreshToken);
        Assert.Equal(1900000000, s.Expires);
        Assert.Equal(new AccountUser("42", "tester", "https://cdn.discordapp.com/embed/avatars/0.png"), s.User);
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
    public async Task Login_error_in_url_fragment_is_bounced_back_to_the_listener()
    {
        var client = new AccountClient(Api, new FakeBackend());
        string? page = null;
        var e = await Assert.ThrowsAsync<AccountException>(() => client.LoginAsync(url =>
        {
            _ = Task.Run(async () =>
            {
                page = await (await HitLoopback(url, d => $"/callback?state={d["state"]}")).Content.ReadAsStringAsync();     // #error=... not sent by browsers
                await HitLoopback(url, d => $"/callback?state={d["state"]}&frag=1&error=access_denied&error_description=denied"); // what the page's script does
            });
            return Task.CompletedTask;
        }));
        Assert.Equal("login_cancelled", e.Code);
        Assert.Contains("location.hash", page);
    }

    [Fact]
    public async Task Service_login_saves_session_and_pushes_payload()
    {
        string? challenge = null;
        var fb = TokenBackend(() => challenge!);
        var store = new MemoryTokenStore();
        var svc = new AccountService(new Settings(), store, null, url =>
        {
            challenge = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["code_challenge"];
            _ = Task.Run(() => HitLoopback(url, d => $"/callback?code=one-time&state={d["state"]}"));
            return Task.CompletedTask;
        }, o => { lock (_sent) _sent.Add(o); }, Api, fb, startTimer: false);
        await svc.LoginAsync();
        Assert.Equal("tester", store.Load()!.User.Username);
        Assert.Equal("rt-new", store.Load()!.RefreshToken);
        var last = JsonSerializer.SerializeToNode(_sent.Last())!;
        Assert.True(last["loggedIn"]!.GetValue<bool>());
        Assert.Equal("tester", last["user"]!["username"]!.GetValue<string>());
        Assert.Contains(_sent, o => JsonSerializer.SerializeToNode(o)!["busy"]!.GetValue<bool>());
    }

    // ---------------- reports + queue ----------------
    [Fact]
    public async Task Report_sent_as_rpc_with_user_token_and_publishable_key()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true, community = new { works = 1, broken = 5, status = "open", fixed_in_version = (string?)null } }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item());
        Assert.True(r.ok);
        Assert.Equal(5, r.community!.Broken);
        var call = fb.Calls.Single();
        Assert.Equal(("POST", "/rest/v1/rpc/vanta_submit_report", "Bearer " + Session.Token, "sb_publishable_test"), (call.method, call.path, call.auth, call.apikey));
        var b = JsonNode.Parse(call.body)!;
        Assert.Equal("broken", b["p_status"]!.GetValue<string>());
        Assert.Equal("crash", b["p_note"]!.GetValue<string>());
        Assert.Equal("fileVersion=0.8.5.651238", b["p_fingerprint"]!.GetValue<string>());
        Assert.Equal("0.3.0", b["p_vanta_version"]!.GetValue<string>());
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
        Assert.Equal(2, fb.Calls.Count(c => c.method == "POST" && c.path == "/rest/v1/rpc/vanta_submit_report" && c.body.Length > 0) - 3);
    }

    [Fact]
    public async Task Rejected_refresh_token_logs_out_and_keeps_report_queued()
    {
        var fb = new FakeBackend
        {
            Respond = (r, _) => r.RequestUri!.AbsolutePath == "/auth/v1/token"
                ? FakeBackend.Json(400, new { code = 400, error_code = "refresh_token_not_found", msg = "Invalid Refresh Token" })
                : FakeBackend.Json(401, new { code = "PGRST303", message = "JWT expired" })
        };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item());
        Assert.Equal("session_expired", r.error);
        Assert.True(r.queued);
        Assert.Null(svc.Session);
        Assert.Null(store.Load());
        Assert.Equal("session_expired", JsonSerializer.SerializeToNode(_sent.Last())!["error"]!.GetValue<string>());
        Assert.Equal(1, fb.Count("POST", "/auth/v1/token?grant_type=refresh_token"));
    }

    [Fact]
    public async Task Access_token_is_refreshed_before_expiry_and_rotation_is_stored()
    {
        var fb = new FakeBackend
        {
            Respond = (r, body) => r.RequestUri!.PathAndQuery == "/auth/v1/token?grant_type=refresh_token"
                ? (JsonNode.Parse(body)!["refresh_token"]!.GetValue<string>() == "rt-1" ? FakeBackend.Json(200, SessionJson("eyJ.fresh", "rt-2", 1900000000)) : FakeBackend.Json(400, new { error_code = "refresh_token_already_used", msg = "x" }))
                : FakeBackend.Json(200, new { ok = true })
        };
        var store = new MemoryTokenStore(); store.Save(Session with { Expires = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeSeconds() });
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item());
        Assert.True(r.ok);
        Assert.Equal("Bearer eyJ.fresh", fb.Calls.Last().auth);
        Assert.Equal(("eyJ.fresh", "rt-2"), (store.Load()!.Token, store.Load()!.RefreshToken));
        Assert.Equal("tester", store.Load()!.User.Username);
        await svc.ReportAsync(Item(cheat: "ammo"));   // still fresh: no second refresh
        Assert.Equal(1, fb.Count("POST", "/auth/v1/token"));
    }

    [Fact]
    public async Task Unauthorized_call_refreshes_once_and_retries()
    {
        int n = 0;
        var fb = new FakeBackend
        {
            Respond = (r, _) => r.RequestUri!.AbsolutePath == "/auth/v1/token" ? FakeBackend.Json(200, SessionJson("eyJ.fresh", "rt-2", 1900000000))
                : r.Headers.Authorization!.Parameter == "eyJ.fresh" ? FakeBackend.Json(200, new { ok = true, removed = true })
                : (n++ == 0 ? FakeBackend.Json(401, new { code = "PGRST303", message = "JWT expired" }) : FakeBackend.Json(500, new { }))
        };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        Assert.True((await svc.ReportAsync(Item(), withdraw: true)).ok);
        Assert.Equal(3, fb.Calls.Count);   // call (401), refresh, retry
        Assert.NotNull(svc.Session);
    }

    [Fact]
    public async Task Rejected_reports_are_dropped_not_retried()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(403, new { code = "PT403", message = "banned", details = "" }) };
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
        var off = new AccountService(new Settings(), new MemoryTokenStore(), null, _ => Task.CompletedTask, _ => { }, supabase: null, startTimer: false, resolve: false);
        Assert.False(off.Configured);
        Assert.Equal("not_configured", (await off.ReportAsync(Item())).error);
    }

    [Fact]
    public async Task Withdraw_calls_withdraw_rpc()
    {
        var fb = new FakeBackend { Respond = (_, _) => FakeBackend.Json(200, new { ok = true, removed = true }) };
        var store = new MemoryTokenStore(); store.Save(Session);
        var svc = Service(fb, store);
        var r = await svc.ReportAsync(Item(), withdraw: true);
        Assert.True(r.ok);
        var c = fb.Calls.Single();
        Assert.Equal(("POST", "/rest/v1/rpc/vanta_withdraw_report"), (c.method, c.path));
        Assert.Equal("godmode", JsonNode.Parse(c.body)!["p_cheat_id"]!.GetValue<string>());
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
        fb.Respond = (r, _) => r.RequestUri!.AbsolutePath == "/rest/v1/rpc/vanta_delete_my_account" && r.Headers.Authorization!.Parameter == Session.Token
            ? FakeBackend.Json(200, new { ok = true, reports = 3 }) : FakeBackend.Json(500, new { });
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
        Assert.Equal(1, fb.Count("POST", "/auth/v1/logout"));
    }

    [Fact]
    public void Expired_stored_session_without_refresh_token_is_ignored()
    {
        var store = new MemoryTokenStore();
        store.Save(Session with { Expires = 1000, RefreshToken = null });
        var svc = Service(new FakeBackend(), store);
        Assert.Null(svc.Session);
        Assert.Null(store.Load());
        store.Save(Session with { Expires = 1000 });   // access token expired but refreshable: stays signed in
        Assert.NotNull(Service(new FakeBackend(), store).Session);
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
        Assert.Equal(("/rest/v1/rpc/vanta_community", null, "sb_publishable_test"), (fb.Calls[0].path, fb.Calls[0].auth, fb.Calls[0].apikey));
        Assert.Equal("fileVersion=1", JsonNode.Parse(fb.Calls[0].body)!["p_fingerprint"]!.GetValue<string>());
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
        Assert.Equal(("POST", "/rest/v1/rpc/vanta_count_usage", null), (c.method, c.path, c.auth));   // anonymous: no token
        var b = JsonNode.Parse(c.body)!;
        Assert.Equal(2, b["p_counts"]!["fly"]!.GetValue<int>());
        Assert.Equal(0, svc.PendingUsage);
    }

    // ---------------- helpers ----------------
    [Fact]
    public void Supabase_config_resolution_refuses_secret_keys()
    {
        static string Jwt(string role) => "eyJhbGciOiJIUzI1NiJ9." + AccountClient.B64Url(Encoding.UTF8.GetBytes($"{{\"role\":\"{role}\"}}")) + ".sig";
        var s = new Settings { SupabaseUrl = "https://from-settings.supabase.co", SupabaseKey = "sb_publishable_settings" };
        Assert.Equal(new SupabaseConfig(new Uri("https://from-env.supabase.co"), "sb_publishable_env"), AccountService.ResolveSupabase(s, "https://from-env.supabase.co", "sb_publishable_env"));
        Assert.Equal("https://from-settings.supabase.co/", AccountService.ResolveSupabase(s, null, null)!.Url.ToString());
        Assert.Equal("http://127.0.0.1:54321/", AccountService.ResolveSupabase(new Settings { SupabaseUrl = "http://127.0.0.1:54321", SupabaseKey = Jwt("anon") }, null, null)!.Url.ToString());
        Assert.Null(AccountService.ResolveSupabase(new Settings { SupabaseUrl = "http://evil.example", SupabaseKey = "sb_publishable_x" }, null, null));
        var logs = new List<string>();
        Assert.Null(AccountService.ResolveSupabase(new Settings { SupabaseUrl = "https://p.supabase.co", SupabaseKey = "sb_secret_x" }, null, null, logs.Add));
        Assert.Null(AccountService.ResolveSupabase(new Settings { SupabaseUrl = "https://p.supabase.co", SupabaseKey = Jwt("service_role") }, null, null));
        Assert.Single(logs);
        Assert.Null(AccountService.ResolveSupabase(new Settings { SupabaseUrl = "https://p.supabase.co" }, null, ""));   // never the built-in key for another project
        Assert.Equal(new SupabaseConfig(new Uri("https://p.supabase.co"), "sb_publishable_env"), AccountService.ResolveSupabase(new Settings { SupabaseUrl = "https://p.supabase.co" }, null, "sb_publishable_env"));
        Assert.Equal(new SupabaseConfig(new Uri(Branding.SupabaseUrl), Branding.SupabaseKey), AccountService.ResolveSupabase(new Settings(), null, null));   // built-in defaults
        Assert.True(AccountService.IsPublicKey(Branding.SupabaseKey));
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
        Assert.Equal(g.Id, body["p_game_id"]!.GetValue<string>());
        Assert.Equal(300, body["p_note"]!.GetValue<string>().Length);
        Assert.Equal(Branding.Version, body["p_vanta_version"]!.GetValue<string>());
        Assert.Equal(g.Name, body["p_game_name"]!.GetValue<string>());
        // invalid status is refused synchronously; without an account the feature is off
        Assert.Contains("\"ok\":false", JsonSerializer.Serialize(c.HandleUi(JsonDocument.Parse($"{{\"type\":\"report\",\"gameId\":\"{g.Id}\",\"id\":\"{cheat.Id}\",\"status\":\"maybe\"}}").RootElement), VJson.Compact));
        c.Account = null;
        Assert.Contains("\"ok\":false", JsonSerializer.Serialize(c.HandleUi(JsonDocument.Parse($"{{\"type\":\"getCommunity\",\"gameId\":\"{g.Id}\"}}").RootElement), VJson.Compact));
    }
}
