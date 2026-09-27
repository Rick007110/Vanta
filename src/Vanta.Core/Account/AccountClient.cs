using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vanta.Core.Account;

public sealed class AccountException : Exception
{
    public string Code { get; }
    public int Status { get; }
    public TimeSpan? RetryAfter { get; }
    public AccountException(string code, int status = 0, string? message = null, TimeSpan? retryAfter = null) : base(message ?? code)
    { Code = code; Status = status; RetryAfter = retryAfter; }
    /// <summary>Worth retrying later (offline, server error, rate limit).</summary>
    public bool Transient => Status == 0 || Status == 429 || Status >= 500;
}

public sealed record ReportItem(string GameId, string CheatId, string Fingerprint, string Status, string? Note,
    string VantaVersion, string? GameVersion, string? GameName, string? CheatName);

public sealed record CommunityEntry(int Works, int Broken, string Status, string? FixedInVersion);

/// <summary>HTTP client for the Vanta backend (Cloudflare Worker). All calls are async and cancellable.</summary>
public sealed class AccountClient
{
    private readonly HttpClient _http;
    public Uri BaseUrl { get; }
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public AccountClient(Uri baseUrl, HttpMessageHandler? handler = null)
    {
        BaseUrl = new Uri(baseUrl.ToString().TrimEnd('/') + "/");
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Branding.Name}/{Branding.Version}");
    }

    /// <summary>
    /// Desktop login: loopback listener on 127.0.0.1 (random port) + PKCE. <paramref name="openBrowser"/> gets the URL
    /// to open in the system browser; the backend redirects back to http://127.0.0.1:port/callback?code&amp;state.
    /// </summary>
    public async Task<AccountSession> LoginAsync(Func<string, Task> openBrowser, CancellationToken ct = default)
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var url = new Uri(BaseUrl, $"auth/start?port={port}&state={state}&challenge={challenge}").ToString();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoginTimeout);
            await openBrowser(url).ConfigureAwait(false);
            var (code, error) = await WaitForCallback(listener, state, timeout.Token).ConfigureAwait(false);
            if (error != null) throw new AccountException(error == "access_denied" ? "login_cancelled" : error, 400);
            return await RedeemAsync(code!, verifier, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AccountException("login_timeout"); }
        finally { listener.Stop(); }
    }

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<(string? code, string? error)> WaitForCallback(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            client.ReceiveTimeout = 5000;
            await using var stream = client.GetStream();
            var line = await ReadRequestLine(stream, ct).ConfigureAwait(false);
            // "GET /callback?code=...&state=... HTTP/1.1"
            var parts = line?.Split(' ');
            if (parts is not { Length: >= 2 } || parts[0] != "GET" || !parts[1].StartsWith("/callback?", StringComparison.Ordinal))
            {
                await Respond(stream, 404, "Not found").ConfigureAwait(false);
                continue;
            }
            var q = System.Web.HttpUtility.ParseQueryString(parts[1][10..]);
            if (q["state"] != state)
            {
                await Respond(stream, 400, Page(Strings.Get("account.page.badstate"))).ConfigureAwait(false);
                continue;   // not ours (stale tab): keep waiting for the real one
            }
            var err = q["error"];
            await Respond(stream, 200, Page(Strings.Get(err == null ? "account.page.ok" : "account.page.failed"))).ConfigureAwait(false);
            return err != null ? (null, err) : (q["code"] ?? "", null);
        }
    }

    private static async Task<string?> ReadRequestLine(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[4096]; int n = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);
        try
        {
            while (n < buf.Length)
            {
                int r = await s.ReadAsync(buf.AsMemory(n), cts.Token).ConfigureAwait(false);
                if (r == 0) break;
                n += r;
                var text = Encoding.ASCII.GetString(buf, 0, n);
                if (text.Contains("\r\n\r\n")) return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (IOException) { }
        var t = Encoding.ASCII.GetString(buf, 0, n);
        var i = t.IndexOf("\r\n", StringComparison.Ordinal);
        return i > 0 ? t[..i] : null;
    }

    private static string Page(string msg) =>
        "<!doctype html><meta charset=utf-8><title>Vanta</title><style>body{font:16px system-ui,sans-serif;background:#0d0f14;color:#e8eaf0;display:grid;place-items:center;height:90vh}</style>" +
        $"<p>{WebUtility.HtmlEncode(msg)}</p>";

    private static async Task Respond(NetworkStream s, int status, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {b.Length}\r\n" +
                   "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nConnection: close\r\n\r\n";
        try { await s.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false); await s.WriteAsync(b).ConfigureAwait(false); await s.FlushAsync().ConfigureAwait(false); }
        catch (IOException) { }
    }

    public async Task<AccountSession> RedeemAsync(string code, string verifier, CancellationToken ct = default)
    {
        var j = await Send(HttpMethod.Post, "auth/token", null, new { code, verifier }, ct).ConfigureAwait(false);
        return new AccountSession(j!["token"]!.GetValue<string>(), j["expires"]?.GetValue<long>() ?? 0, ParseUser(j["user"]!));
    }

    public static AccountUser ParseUser(JsonNode u) => new(u["id"]!.GetValue<string>(), u["username"]?.GetValue<string>() ?? "?", u["avatarUrl"]?.GetValue<string>());

    public async Task<AccountUser> MeAsync(string token, CancellationToken ct = default) => ParseUser((await Send(HttpMethod.Get, "me", token, null, ct).ConfigureAwait(false))!["user"]!);
    public Task LogoutAsync(string token, CancellationToken ct = default) => Send(HttpMethod.Post, "auth/logout", token, new { }, ct);
    public Task DeleteAccountAsync(string token, CancellationToken ct = default) => Send(HttpMethod.Delete, "me", token, null, ct);

    public async Task<CommunityEntry?> ReportAsync(string token, ReportItem r, CancellationToken ct = default)
    {
        var j = await Send(HttpMethod.Post, "reports", token, new
        {
            game_id = r.GameId, cheat_id = r.CheatId, fingerprint = r.Fingerprint, status = r.Status, note = r.Note,
            vanta_version = r.VantaVersion, game_version = r.GameVersion, game_name = r.GameName, cheat_name = r.CheatName,
        }, ct).ConfigureAwait(false);
        return j?["community"] is JsonNode c ? ParseEntry(c) : null;
    }

    public Task WithdrawAsync(string token, string gameId, string cheatId, string fingerprint, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, "reports", token, new { game_id = gameId, cheat_id = cheatId, fingerprint }, ct);

    public async Task<Dictionary<string, CommunityEntry>> CommunityAsync(string gameId, string fingerprint, CancellationToken ct = default)
    {
        var j = await Send(HttpMethod.Get, $"community/{Uri.EscapeDataString(gameId)}?fingerprint={Uri.EscapeDataString(fingerprint)}", null, null, ct).ConfigureAwait(false);
        var d = new Dictionary<string, CommunityEntry>();
        if (j?["cheats"] is JsonObject o) foreach (var (k, v) in o) if (v != null) d[k] = ParseEntry(v);
        return d;
    }

    public Task UsageAsync(string gameId, IReadOnlyDictionary<string, int> counts, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "usage", null, new { game_id = gameId, counts }, ct);

    private static CommunityEntry ParseEntry(JsonNode c) => new(c["works"]?.GetValue<int>() ?? 0, c["broken"]?.GetValue<int>() ?? 0,
        c["status"]?.GetValue<string>() ?? "open", c["fixed_in_version"]?.GetValue<string?>());

    private async Task<JsonNode?> Send(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
        if (token != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = JsonContent.Create(body, options: new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new AccountException("offline", 0, e.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AccountException("timeout"); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? j = null;
            try { if (text.Length > 0) j = JsonNode.Parse(text); } catch (JsonException) { }
            if (res.IsSuccessStatusCode) return j;
            var code = j?["error"]?.GetValue<string>() ?? "http_" + (int)res.StatusCode;
            TimeSpan? retry = res.Headers.RetryAfter?.Delta;
            throw new AccountException(code, (int)res.StatusCode, j?["message"]?.GetValue<string>() ?? code, retry);
        }
    }
}
