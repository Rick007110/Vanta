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
        Uri? backend = null, HttpMessageHandler? handler = null, Action<string>? log = null, bool startTimer = true)
    {
        _settings = settings; _store = store; _openBrowser = openBrowser; _send = send; _log = log ?? (_ => { });
        backend ??= ResolveBackend(settings, Environment.GetEnvironmentVariable("VANTA_BACKEND_URL"));
        if (backend != null) Client = new AccountClient(backend, handler);
        Queue = new ReportQueue(queueFile);
        var s = store.Load();
        if (s != null && !s.Expired(Now())) _session = s; else if (s != null) store.Clear();
        _nextUsageFlush = Now().AddMinutes(30);
        _timer = new Timer(_ => _ = TickAsync(), null, startTimer ? TimeSpan.FromSeconds(20) : Timeout.InfiniteTimeSpan, startTimer ? TimeSpan.FromMinutes(2) : Timeout.InfiniteTimeSpan);
    }

    /// <summary>Backend URL: env VANTA_BACKEND_URL, then settings.json "backendUrl", then the built-in default. Only https (or loopback http for testing).</summary>
    public static Uri? ResolveBackend(Settings s, string? env)
    {
        foreach (var raw in new[] { env, s.BackendUrl, Branding.BackendUrl })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) && string.IsNullOrEmpty(u.Query))
                return u;
        }
        return null;
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
            try { await Client.LogoutAsync(s.Token).ConfigureAwait(false); } catch (AccountException) { /* token dies with its 180-day TTL */ }
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
            await Client.DeleteAccountAsync(s.Token).ConfigureAwait(false);
            lock (_gate) _session = null;
            _store.Clear();
            Queue.Clear();
            lock (_gate) _community.Clear();
            Busy = false; Push();
            _send(new { type = "accountDeleted" });
            _log("account: " + Strings.Get("account.deleted"));
            return true;
        }
        catch (AccountException e) when (e.Status == 401) { Expire(); Busy = false; return false; }
        catch (AccountException e) { Busy = false; Push(e.Code); return false; }
    }

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
            if (q.Kind == "withdraw") { await Client.WithdrawAsync(s.Token, q.Item.GameId, q.Item.CheatId, q.Item.Fingerprint).ConfigureAwait(false); return (true, false, null, null); }
            return (true, false, await Client.ReportAsync(s.Token, q.Item).ConfigureAwait(false), null);
        }
        catch (AccountException e) when (e.Status == 401) { Expire(); return (false, false, null, "session_expired"); }
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

    public void Dispose() { _timer.Dispose(); _loginCts?.Dispose(); }
}
