using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vanta.Core.Account;

/// <summary>
/// "Vanta.exe --account-selftest [backend-url]": checks DPAPI token storage and the loopback + PKCE login on this PC.
/// Without a URL the backend is simulated in-process; with a URL (e.g. a local wrangler dev + mock Discord) the full
/// login, a report, the community counts and account deletion run against it.
/// </summary>
public sealed class AccountSelfTest
{
    private readonly TextWriter _o;
    private int _ok, _fail;
    public AccountSelfTest(TextWriter o) => _o = o;

    private void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _ok++; else _fail++;
        _o.WriteLine($"{(ok ? "OK  " : "FOUT")} {name}{(detail != null ? " — " + detail : "")}");
    }

    public int Run(string? backend)
    {
        _o.WriteLine($"{Branding.Name} {Branding.Version} account-selftest ({(OperatingSystem.IsWindows() ? "Windows" : "geen Windows")})");
        Dpapi();
        try { LoginAsync(backend).GetAwaiter().GetResult(); }
        catch (Exception e) { Check("login", false, e.GetType().Name + ": " + e.Message); }
        _o.WriteLine(_fail == 0 ? $"account-selftest: alle {_ok} controles geslaagd" : $"account-selftest: {_fail} FOUT(EN), {_ok} OK");
        return _fail == 0 ? 0 : 1;
    }

    private void Dpapi()
    {
        if (!OperatingSystem.IsWindows()) { Check("DPAPI (overgeslagen: alleen Windows)", true); return; }
        try
        {
            var secret = Encoding.UTF8.GetBytes("vt_selftest_" + Guid.NewGuid());
            var enc = DpapiTokenStore.Protect(secret);
            Check("DPAPI versleutelt", enc.Length > secret.Length && !enc.AsSpan().SequenceEqual(secret));
            Check("DPAPI ontsleutelt", DpapiTokenStore.Unprotect(enc).AsSpan().SequenceEqual(secret));
            var file = Path.Combine(Path.GetTempPath(), $"vanta-selftest-{Guid.NewGuid():N}.bin");
            var store = new DpapiTokenStore(file);
            var s = new AccountSession("vt_" + new string('q', 43), DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), new AccountUser("1", "selftest", null));
            store.Save(s);
            Check("sessiebestand bevat geen leesbare token", !File.ReadAllText(file, Encoding.Latin1).Contains(s.Token));
            Check("sessie terug te lezen", store.Load()?.Token == s.Token);
            store.Clear();
            Check("sessie gewist", !File.Exists(file) && store.Load() == null);
        }
        catch (Exception e) { Check("DPAPI", false, e.Message); }
    }

    private async Task LoginAsync(string? backend)
    {
        Func<string, Task> browser = url =>
        {
            _ = Task.Run(async () =>
            {
                using var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var next = url;
                for (int i = 0; i < 6; i++)
                {
                    using var r = await h.GetAsync(next);
                    if (r.StatusCode != HttpStatusCode.Found || r.Headers.Location == null) break;
                    next = r.Headers.Location.IsAbsoluteUri ? r.Headers.Location.ToString() : new Uri(new Uri(next), r.Headers.Location).ToString();
                }
            });
            return Task.CompletedTask;
        };

        if (string.IsNullOrWhiteSpace(backend))
        {
            // simulated backend: /auth/start redirects straight to the loopback, /auth/token checks PKCE
            string? challenge = null;
            var fake = new Sim(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/auth/start")
                {
                    var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
                    challenge = q["challenge"];
                    var res = new HttpResponseMessage(HttpStatusCode.Found);
                    res.Headers.Location = new Uri($"http://127.0.0.1:{q["port"]}/callback?code=sim-code&state={q["state"]}");
                    return res;
                }
                if (path == "/auth/token")
                {
                    var j = JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
                    var ok = j["code"]!.GetValue<string>() == "sim-code" && AccountClient.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(j["verifier"]!.GetValue<string>()))) == challenge;
                    return new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                    { Content = new StringContent(ok ? "{\"token\":\"vt_" + new string('s', 43) + "\",\"expires\":0,\"user\":{\"id\":\"1\",\"username\":\"selftest\"}}" : "{\"error\":\"invalid_verifier\"}", Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var client = new AccountClient(new Uri("https://selftest.invalid/"), fake) { LoginTimeout = TimeSpan.FromSeconds(20) };
            // the "browser" asks the simulated backend for the redirect, then calls the loopback listener
            var s = await client.LoginAsync(async url =>
            {
                using var h = new HttpClient(fake);
                var r = await h.GetAsync(url);
                browser(r.Headers.Location!.ToString());
            });
            Check("loopback-listener + PKCE (gesimuleerde server)", s.Token.StartsWith("vt_") && s.User.Username == "selftest");
            return;
        }

        var svc = new AccountService(new Settings(), new MemoryTokenStore(), null, browser, _ => { }, new Uri(backend), startTimer: false);
        await svc.LoginAsync();
        Check("inloggen via " + backend, svc.Session != null, svc.Session?.User.Username);
        if (svc.Session == null) return;
        var game = "selftest-" + Guid.NewGuid().ToString("N")[..6];
        var r1 = await svc.ReportAsync(new ReportItem(game, "godmode", "fileVersion=1", "broken", "selftest", Branding.Version, "1", "Selftest", "God mode"));
        Check("melding versturen", r1.ok && r1.community?.Broken == 1, r1.error);
        var c = await svc.CommunityAsync(game, "fileVersion=1", force: true);
        Check("community-telling ophalen", c != null && c.TryGetValue("godmode", out var e) && e.Broken == 1);
        Check("account verwijderen", await svc.DeleteAsync());
    }

    private sealed class Sim : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public Sim(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(_f(r));
    }
}
