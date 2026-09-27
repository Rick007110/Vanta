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

/// <summary>Supabase project URL + public (publishable/anon) key.</summary>
public sealed record SupabaseConfig(Uri Url, string Key);

/// <summary>
/// HTTP client for Supabase: Auth (Discord sign-in with PKCE, refresh, logout) and the public.vanta_* RPC functions.
/// Only ever uses the project's public key; everything is protected by Row Level Security in the database.
/// </summary>
public sealed class AccountClient
{
    private readonly HttpClient _http;
    public Uri BaseUrl { get; }
    public string ApiKey { get; }
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public AccountClient(SupabaseConfig cfg, HttpMessageHandler? handler = null)
    {
        BaseUrl = new Uri(cfg.Url.ToString().TrimEnd('/') + "/");
        ApiKey = cfg.Key;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Branding.Name}/{Branding.Version}");
    }

    /// <summary>Supabase Auth authorize URL. Discord always asks for "identify" + "email" (Supabase adds the email scope).</summary>
    public string AuthorizeUrl(int port, string state, string challenge) =>
        new Uri(BaseUrl, "auth/v1/authorize?provider=discord&redirect_to=" + Uri.EscapeDataString($"http://127.0.0.1:{port}/callback?state={state}") +
                         $"&code_challenge={challenge}&code_challenge_method=s256").ToString();

    /// <summary>
    /// Desktop login: loopback listener on 127.0.0.1 (random port) + PKCE. <paramref name="openBrowser"/> gets the URL
    /// to open in the system browser; Supabase redirects back to http://127.0.0.1:port/callback?state=..&amp;code=..
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoginTimeout);
            await openBrowser(AuthorizeUrl(port, state, challenge)).ConfigureAwait(false);
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
            var parts = line?.Split(' ');
            if (parts is not { Length: >= 2 } || parts[0] != "GET" || !parts[1].StartsWith("/callback?", StringComparison.Ordinal))
            {
                await Respond(stream, 404, Page("Not found")).ConfigureAwait(false);
                continue;
            }
            var q = System.Web.HttpUtility.ParseQueryString(parts[1][10..]);
            if (q["state"] != state)
            {
                await Respond(stream, 400, Page(Strings.Get("account.page.badstate"))).ConfigureAwait(false);
                continue;   // not ours (stale tab): keep waiting for the real one
            }
            var err = q["error_code"] ?? q["error"];
            var code = q["code"];
            if (err == null && string.IsNullOrEmpty(code))
            {
                if (q["frag"] == null)
                {
                    // Some errors arrive in the URL fragment, which browsers don't send: bounce it back as a query string.
                    await Respond(stream, 200, FragmentPage(), script: true).ConfigureAwait(false);
                    continue;
                }
                err = "login_failed";   // e.g. tokens in the fragment (implicit flow): not accepted, PKCE only
            }
            await Respond(stream, 200, Page(Strings.Get(err == null ? "account.page.ok" : "account.page.failed"))).ConfigureAwait(false);
            return err != null ? (null, err) : (code, null);
        }
    }

    private static async Task<string?> ReadRequestLine(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[8192]; int n = 0;
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

    private const string PageStyle = "<style>body{font:16px system-ui,sans-serif;background:#0d0f14;color:#e8eaf0;display:grid;place-items:center;height:90vh}</style>";

    private static string Page(string msg) =>
        "<!doctype html><meta charset=utf-8><title>Vanta</title>" + PageStyle + $"<p>{WebUtility.HtmlEncode(msg)}</p>";

    private static string FragmentPage() =>
        "<!doctype html><meta charset=utf-8><title>Vanta</title>" + PageStyle + "<p>…</p><script>" +
        "var h=location.hash.slice(1);location.replace('/callback'+location.search+'&frag=1'+(h?'&'+h:''));</script>";

    private static async Task Respond(NetworkStream s, int status, string body, bool script = false)
    {
        var b = Encoding.UTF8.GetBytes(body);
        var csp = "default-src 'none'; style-src 'unsafe-inline'" + (script ? "; script-src 'unsafe-inline'" : "");
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {b.Length}\r\n" +
                   $"Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: {csp}\r\nConnection: close\r\n\r\n";
        try { await s.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false); await s.WriteAsync(b).ConfigureAwait(false); await s.FlushAsync().ConfigureAwait(false); }
        catch (IOException) { }
    }

    // ---------------- auth ----------------
    public async Task<AccountSession> RedeemAsync(string code, string verifier, CancellationToken ct = default) =>
        ParseSession((await Send(HttpMethod.Post, "auth/v1/token?grant_type=pkce", null, new { auth_code = code, code_verifier = verifier }, ct).ConfigureAwait(false))!);

    /// <summary>New access token; Supabase rotates the refresh token, so the returned session must replace the old one.</summary>
    public async Task<AccountSession> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        ParseSession((await Send(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token", null, new { refresh_token = refreshToken }, ct).ConfigureAwait(false))!);

    public static AccountSession ParseSession(JsonNode j)
    {
        var token = j["access_token"]?.GetValue<string>() ?? throw new AccountException("login_failed", 0, "no access_token");
        long exp = j["expires_at"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (j["expires_in"]?.GetValue<long>() ?? 3600);
        return new AccountSession(token, exp, ParseUser(j["user"] ?? new JsonObject()), j["refresh_token"]?.GetValue<string>());
    }

    /// <summary>Discord id, display name and avatar from the Supabase user's metadata.</summary>
    public static AccountUser ParseUser(JsonNode u)
    {
        var m = u["user_metadata"] ?? new JsonObject();
        string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var x) && !string.IsNullOrWhiteSpace(x) ? x : null;
        var id = S(m["provider_id"]) ?? S(m["sub"]) ?? S(u["id"]) ?? "?";
        var name = S(m["custom_claims"]?["global_name"]) ?? S(m["full_name"]) ?? S(m["user_name"]) ?? S(m["name"]) ?? "?";
        var avatar = S(m["avatar_url"]);
        if (avatar != null && !avatar.StartsWith("https://cdn.discordapp.com/", StringComparison.Ordinal)) avatar = null;
        return new AccountUser(id, name, avatar);
    }

    public async Task<AccountUser> MeAsync(string token, CancellationToken ct = default) => ParseUser((await Send(HttpMethod.Get, "auth/v1/user", token, null, ct).ConfigureAwait(false))!);
    public Task LogoutAsync(string token, CancellationToken ct = default) => Send(HttpMethod.Post, "auth/v1/logout?scope=local", token, new { }, ct);

    // ---------------- RPC ----------------
    private Task<JsonNode?> Rpc(string fn, string? token, object args, CancellationToken ct) => Send(HttpMethod.Post, "rest/v1/rpc/" + fn, token, args, ct);

    public Task DeleteAccountAsync(string token, CancellationToken ct = default) => Rpc("vanta_delete_my_account", token, new { }, ct);

    public async Task<CommunityEntry?> ReportAsync(string token, ReportItem r, CancellationToken ct = default)
    {
        var j = await Rpc("vanta_submit_report", token, new
        {
            p_game_id = r.GameId, p_cheat_id = r.CheatId, p_fingerprint = r.Fingerprint, p_status = r.Status, p_note = r.Note,
            p_vanta_version = r.VantaVersion, p_game_version = r.GameVersion, p_game_name = r.GameName, p_cheat_name = r.CheatName,
        }, ct).ConfigureAwait(false);
        return j?["community"] is JsonObject c ? ParseEntry(c) : null;
    }

    public Task WithdrawAsync(string token, string gameId, string cheatId, string fingerprint, CancellationToken ct = default) =>
        Rpc("vanta_withdraw_report", token, new { p_game_id = gameId, p_cheat_id = cheatId, p_fingerprint = fingerprint }, ct);

    public async Task<Dictionary<string, CommunityEntry>> CommunityAsync(string gameId, string fingerprint, CancellationToken ct = default)
    {
        var j = await Rpc("vanta_community", null, new { p_game_id = gameId, p_fingerprint = fingerprint }, ct).ConfigureAwait(false);
        var d = new Dictionary<string, CommunityEntry>();
        if (j?["cheats"] is JsonObject o) foreach (var (k, v) in o) if (v is JsonObject) d[k] = ParseEntry(v);
        return d;
    }

    public Task UsageAsync(string gameId, IReadOnlyDictionary<string, int> counts, CancellationToken ct = default) =>
        Rpc("vanta_count_usage", null, new { p_game_id = gameId, p_counts = counts }, ct);

    private static CommunityEntry ParseEntry(JsonNode c) => new(c["works"]?.GetValue<int>() ?? 0, c["broken"]?.GetValue<int>() ?? 0,
        c["status"]?.GetValue<string>() ?? "open", c["fixed_in_version"]?.GetValue<string?>());

    private static readonly System.Text.RegularExpressions.Regex CodeRx = new("^[a-z][a-z0-9_]{1,40}$");
    private static readonly System.Text.RegularExpressions.Regex RetryRx = new(@"retry_after=(\d+)");

    private async Task<JsonNode?> Send(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(BaseUrl, path));
        req.Headers.TryAddWithoutValidation("apikey", ApiKey);
        // User JWT if signed in; legacy anon keys are JWTs too and go in Authorization, new publishable keys must not.
        var bearer = token ?? (ApiKey.StartsWith("eyJ", StringComparison.Ordinal) ? ApiKey : null);
        if (bearer != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body != null) req.Content = JsonContent.Create(body, options: new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });
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
            int status = (int)res.StatusCode;
            string? Str(string k) => j is JsonObject o && o[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
            // Supabase Auth: {error_code, msg} or {error, error_description}; PostgREST: {code, message, details}
            var msg = Str("message");
            var code = Str("error_code") ?? (Str("error") is { } e2 && CodeRx.IsMatch(e2) ? e2 : null)
                       ?? (msg != null && CodeRx.IsMatch(msg) ? msg : null)
                       ?? (Str("code") switch
                       {
                           "42501" => "forbidden",
                           "PGRST202" => "server_not_ready",
                           { } c when c.StartsWith("PGRST3", StringComparison.Ordinal) => "jwt_invalid",
                           _ => status == 401 ? "unauthorized" : "http_" + status,
                       });
            TimeSpan? retry = res.Headers.RetryAfter?.Delta;
            if (Str("details") is { } det && RetryRx.Match(det) is { Success: true } m) retry = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value));
            throw new AccountException(code, status, Str("msg") ?? Str("error_description") ?? msg ?? code, retry);
        }
    }
}
