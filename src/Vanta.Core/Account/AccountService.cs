using System.Security.Cryptography;
using System.Text;

namespace Vanta.Core.Account;

/// <summary>
/// Account state for the app: session (DPAPI-stored), login/logout/delete, report queue with retry,
/// community counts cache and the opt-in anonymous usage counter. Methods may be called from any thread;
/// results go to the UI through <c>send</c>.
/// </summary>
public sealed class AccountService : IDisposable
{
    private readonly Settings _settings;
    private readonly ITokenStore _store;
    private readonly Func<string, Task> _openBrowser;
    private readonly Action<object> _send;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset at, Dictionary<string, CommunityEntry> data)> _community = new();
    private readonly Dictionary<string, Dictionary<string, int>> _usage = new();
    private readonly Timer _timer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _loginCts;
    private AccountSession? _session;
    private string? _lastError;
    private DateTimeOffset _nextUsageFlush;

    public AccountClient? Client { get; }
    public ReportQueue Queue { get; }
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public TimeSpan CommunityTtl { get; set; } = TimeSpan.FromMinutes(5);
    public const string PrivacyUrl = "https://github.com/" + Vanta.Core.Update.GitHubRelease.Repo + "/blob/main/docs/privacy.md";
    public bool Busy { get; private set; }
    public bool Configured => Client != null;
    public AccountSession? Session { get { lock (_gate) return _session; } }

    public AccountService(Settings settings, ITokenStore store, string? queueFile, Func<string, Task> openBrowser, Action<object> send,
        SupabaseConfig? supabase = null, HttpMessageHandler? handler = null, Action<string>? log = null, bool startTimer = true, bool resolve = true)
    {
        _settings = settings; _store = store; _openBrowser = openBrowser; _send = send; _log = log ?? (_ => { });
        if (supabase == null && resolve)
            supabase = ResolveSupabase(settings, Environment.GetEnvironmentVariable("VANTA_SUPABASE_URL"), Environment.GetEnvironmentVariable("VANTA_SUPABASE_KEY"), _log);
        if (supabase != null) Client = new AccountClient(supabase, handler);
        Queue = new ReportQueue(queueFile);
        var s = store.Load();
        if (s != null && s.Usable(Now())) _session = s; else if (s != null) store.Clear();
        _nextUsageFlush = Now().AddMinutes(30);
        _timer = new Timer(_ => _ = TickAsync(), null, startTimer ? TimeSpan.FromSeconds(20) : Timeout.InfiniteTimeSpan, startTimer ? TimeSpan.FromMinutes(2) : Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Supabase project: env VANTA_SUPABASE_URL/KEY, then settings.json "supabaseUrl"/"supabaseKey", then the built-in
    /// values. URL must be https (or loopback http for testing). Secret/service_role keys are refused: they bypass RLS
    /// and must never be in the app.
    /// </summary>
    public static SupabaseConfig? ResolveSupabase(Settings s, string? envUrl, string? envKey, Action<string>? log = null)
    {
        Uri? url = null;
        foreach (var raw in new[] { envUrl, s.SupabaseUrl, Branding.SupabaseUrl })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) && string.IsNullOrEmpty(u.Query))
            { url = u; break; }
        }
        var key = new[] { envKey, s.SupabaseKey, Branding.SupabaseKey }.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k))?.Trim();
        if (url == null || key == null) return null;
        if (!IsPublicKey(key)) { log?.Invoke("account: geweigerd: dit is geen publishable/anon key (secret keys horen nooit in Vanta)"); return null; }
        return new SupabaseConfig(url, key);
    }

    /// <summary>True for sb_publishable_... and legacy anon JWTs; false for secret/service_role keys and anything else.</summary>
    public static bool IsPublicKey(string key)
    {
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal)) return true;
        var parts = key.Split('.');
        if (!key.StartsWith("eyJ", StringComparison.Ordinal) || parts.Length != 3) return false;
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            return System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(p))?["role"]?.GetValue<string>() == "anon";
        }
        catch (Exception) { return false; }
    }

    /// <summary>Server fingerprints must be printable ASCII (≤ 200); anything else is hashed so the same game version always maps to the same key.</summary>
    public static string ReportFingerprint(string versionKey)
    {
        if (versionKey.Length is > 0 and <= 200 && versionKey.All(c => c >= 0x20 && c <= 0x7E)) return versionKey;
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionKey))).ToLowerInvariant()[..40];
    }

    public object Payload()
    {
        var s = Session;
        return new
        {
            type = "account", configured = Configured, loggedIn = s != null, busy = Busy, error = _lastError,
            user = s == null ? null : new { id = s.User.Id, username = s.User.Username, avatarUrl = s.User.AvatarUrl },
            shareUsage = _settings.ShareUsage, pending = Queue.Count, privacyUrl = PrivacyUrl,
        };
    }

    private void Push(string? error = null) { _lastError = error; _send(Payload()); }

    // ---------------- login / logout / delete ----------------
    public async Task LoginAsync()
    {
        if (Client == null) { Push("not_configured"); return; }
        _loginCts?.Cancel();
        var cts = _loginCts = new CancellationTokenSource();
        Busy = true; Push();
        try
        {
            var s = await Client.LoginAsync(_openBrowser, cts.Token).ConfigureAwait(false);
            lock (_gate) _session = s;
            try { _store.Save(s); } catch (Exception e) { _log("account: token opslaan mislukt: " + e.Message); }
            _log("account: " + Strings.Get("account.loggedin", s.User.Username));
            Busy = false; Push();
            _ = FlushAsync();
        }
        catch (OperationCanceledException) { Busy = false; Push(); }
        catch (AccountException e) { _log("account: login " + e.Code); Busy = false; Push(e.Code); }
        catch (Exception e) { _log("account: login " + e.Message); Busy = false; Push("login_failed"); }
    }

    public void CancelLogin() { _loginCts?.Cancel(); }

    public async Task LogoutAsync()
    {
        var s = Session;
        lock (_gate) _session = null;
        _store.Clear();
        Push();
        if (s != null && Client != null)
            try { await Client.LogoutAsync(s.Token).ConfigureAwait(false); } catch (AccountException) { /* expired access token: the session ends server-side when its refresh token is unused */ }
        _log("account: " + Strings.Get("account.loggedout"));
    }

    /// <summary>Deletes the account and every report on the server. Local state is only cleared once the server confirmed.</summary>
    public async Task<bool> DeleteAsync()
    {
        var s = Session;
        if (s == null || Client == null) { Push("not_logged_in"); return false; }
        Busy = true; Push();
        try
        {
            await WithTokenAsync(t => Client.DeleteAccountAsync(t)).ConfigureAwait(false);
            lock (_gate) _session = null;
            _store.Clear();
            Queue.Clear();
            lock (_gate) _community.Clear();
            Busy = false; Push();
            _send(new { type = "accountDeleted" });
            _log("account: " + Strings.Get("account.deleted"));
            return true;
        }
        catch (AccountException e) when (e.Status == 401) { Busy = false; if (Session != null) Expire(); return false; }
        catch (AccountException e) { Busy = false; Push(e.Code); return false; }
    }

    // ---------------- tokens ----------------
    /// <summary>A valid access token: refreshes it shortly before it expires (Supabase access tokens live ~1 hour).
    /// A rejected refresh token ends the session; a network problem throws a transient error.</summary>
    public async Task<string> TokenAsync(bool force = false)
    {
        var s = Session ?? throw new AccountException("not_logged_in", 401);
        if (!force && !s.Expired(Now(), 60)) return s.Token;
        if (s.RefreshToken == null) { Expire(); throw new AccountException("session_expired", 401); }
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cur = Session ?? throw new AccountException("not_logged_in", 401);
            if (!ReferenceEquals(cur, s) && !cur.Expired(Now(), 60)) return cur.Token;   // refreshed by another caller meanwhile
            AccountSession fresh;
            try { fresh = await Client!.RefreshAsync(cur.RefreshToken!).ConfigureAwait(false); }
            catch (AccountException e) when (!e.Transient) { Expire(); throw new AccountException("session_expired", 401); }
            if (fresh.User.Username == "?") fresh = fresh with { User = cur.User };
            lock (_gate) _session = fresh;
            try { _store.Save(fresh); } catch (Exception e) { _log("account: token opslaan mislukt: " + e.Message); }
            return fresh.Token;
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>Runs an authenticated call; on 401 refreshes once and retries.</summary>
    private async Task<T> WithTokenAsync<T>(Func<string, Task<T>> call)
    {
        var t = await TokenAsync().ConfigureAwait(false);
        try { return await call(t).ConfigureAwait(false); }
        catch (AccountException e) when (e.Status == 401 && Session?.RefreshToken != null)
        {
            t = await TokenAsync(force: true).ConfigureAwait(false);
            return await call(t).ConfigureAwait(false);
        }
    }

    private Task WithTokenAsync(Func<string, Task> call) => WithTokenAsync<bool>(async t => { await call(t).ConfigureAwait(false); return true; });

    private void Expire()
    {
        lock (_gate) _session = null;
        _store.Clear();
        _log("account: " + Strings.Get("account.expired"));
        Push("session_expired");
    }

    // ---------------- reports ----------------
    /// <summary>Queues and immediately tries to send. Result: sent (with fresh counts), queued (offline) or failed.</summary>
    public async Task<(bool ok, bool queued, CommunityEntry? community, string? error)> ReportAsync(ReportItem r, bool withdraw = false)
    {
        if (Client == null) return (false, false, null, "not_configured");
        if (Session == null) return (false, false, null, "not_logged_in");
        var q = new QueuedReport { Kind = withdraw ? "withdraw" : "report", Item = r, Queued = Now() };
        Queue.Put(q);
        var res = await SendOne(q).ConfigureAwait(false);
        if (res.sent) { Queue.Done(q); UpdateCache(r, res.community); }
        else if (res.drop) Queue.Done(q);
        Push(res.error is "session_expired" ? "session_expired" : null);
        return (res.sent, !res.sent && !res.drop, res.community, res.error);
    }

    private async Task<(bool sent, bool drop, CommunityEntry? community, string? error)> SendOne(QueuedReport q)
    {
        var s = Session;
        if (s == null || Client == null) return (false, false, null, "not_logged_in");
        try
        {
            if (q.Kind == "withdraw") { await WithTokenAsync(t => Client.WithdrawAsync(t, q.Item.GameId, q.Item.CheatId, q.Item.Fingerprint)).ConfigureAwait(false); return (true, false, null, null); }
            return (true, false, await WithTokenAsync(t => Client.ReportAsync(t, q.Item)).ConfigureAwait(false), null);
        }
        catch (AccountException e) when (e.Status == 401) { if (Session != null) Expire(); return (false, false, null, "session_expired"); }
        catch (AccountException e) when (e.Transient) { q.Attempts++; Queue.Touch(); return (false, false, null, e.Code); }
        catch (AccountException e) { _log($"account: melding geweigerd ({e.Code})"); return (false, true, null, e.Code); }   // banned, invalid: retrying won't help
    }

    /// <summary>Retries queued reports (after login, every 2 minutes, when the connection is back).</summary>
    public async Task FlushAsync()
    {
        if (Session == null || Client == null || Queue.Count == 0) return;
        if (!await _flushLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            int sent = 0;
            foreach (var q in Queue.Snapshot())
            {
                var r = await SendOne(q).ConfigureAwait(false);
                if (r.sent) { Queue.Done(q); UpdateCache(q.Item, r.community); sent++; }
                else if (r.drop) Queue.Done(q);
                else break;   // offline / rate limited / expired: try again later
            }
            if (sent > 0) { _log($"account: {sent} wachtende melding(en) verstuurd"); Push(); }
        }
        finally { _flushLock.Release(); }
    }

    private void UpdateCache(ReportItem r, CommunityEntry? c)
    {
        lock (_gate)
        {
            var key = r.GameId + "|" + r.Fingerprint;
            if (c != null && _community.TryGetValue(key, out var e)) e.data[r.CheatId] = c;
            else _community.Remove(key);
        }
    }

    // ---------------- community counts ----------------
    public async Task<Dictionary<string, CommunityEntry>?> CommunityAsync(string gameId, string fingerprint, bool force = false)
    {
        if (Client == null) return null;
        var key = gameId + "|" + fingerprint;
        lock (_gate)
            if (!force && _community.TryGetValue(key, out var e) && Now() - e.at < CommunityTtl) return e.data;
        try
        {
            var d = await Client.CommunityAsync(gameId, fingerprint).ConfigureAwait(false);
            lock (_gate) _community[key] = (Now(), d);
            return d;
        }
        catch (AccountException e) { _log("account: community " + e.Code); lock (_gate) return _community.TryGetValue(key, out var old) ? old.data : null; }
    }

    // ---------------- anonymous usage (opt-in) ----------------
    public void CountUsage(string gameId, string cheatId)
    {
        if (!_settings.ShareUsage || Client == null) return;
        lock (_gate)
        {
            if (!_usage.TryGetValue(gameId, out var m)) _usage[gameId] = m = new();
            m[cheatId] = Math.Min(1000, m.GetValueOrDefault(cheatId) + 1);
        }
    }

    public int PendingUsage { get { lock (_gate) return _usage.Sum(g => g.Value.Count); } }

    public async Task FlushUsageAsync()
    {
        if (Client == null) return;
        List<(string game, Dictionary<string, int> counts)> batch;
        lock (_gate)
        {
            if (!_settings.ShareUsage) { _usage.Clear(); return; }
            batch = _usage.Select(g => (g.Key, g.Value.Take(100).ToDictionary(x => x.Key, x => x.Value))).ToList();
            _usage.Clear();
        }
        foreach (var (game, counts) in batch)
            try { await Client.UsageAsync(game, counts).ConfigureAwait(false); }
            catch (AccountException e) { _log("account: usage " + e.Code); }   // anonymous statistics: losing a batch is fine
    }

    private async Task TickAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
            if (Now() >= _nextUsageFlush) { _nextUsageFlush = Now().AddMinutes(30); await FlushUsageAsync().ConfigureAwait(false); }
        }
        catch (Exception e) { _log("account: " + e.Message); }
    }

    /// <summary>On exit: send the usage counts (short timeout); queued reports stay on disk for next time.</summary>
    public void Shutdown(int timeoutMs = 3000)
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _loginCts?.Cancel();
        try { FlushUsageAsync().Wait(timeoutMs); } catch { }
    }

    public void Dispose() { _timer.Dispose(); _loginCts?.Dispose(); _refreshLock.Dispose(); _flushLock.Dispose(); }
}
