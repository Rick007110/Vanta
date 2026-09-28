using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Vanta.Core;
using Vanta.Core.Update;

namespace Vanta.App;

/// <summary>
/// In-app side of the auto-updater: checks GitHub at start, every 30 min and on window activation (at most once per 5 min;
/// unauthenticated, silent when offline or rate-limited), offers a version once per session (see <see cref="UpdatePolicy"/>), downloads + verifies the release zip, and hands over to the helper (<see cref="UpdateHelper"/>).
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    public UpdatePolicy Policy { get; } = new();
    private readonly Action<object> _post;
    private readonly HttpClient _http;
    private readonly UpdateChecker _checker;
    private readonly System.Threading.Timer _timer;
    private UpdateCheck? _last;
    private int _busy;
    public UpdateStore Store { get; } = new(Settings.DataDir);
    public SemVer Current { get; } = SemVer.Parse(Branding.Version);

    public AppUpdater(Action<object> post)
    {
        _post = post;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Branding.Name, Branding.Version));
        _checker = new UpdateChecker(Current, GetAsync);
        _timer = new System.Threading.Timer(_ => { Policy.MarkChecked(); _ = CheckAsync(manual: false); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Start the schedule: first check shortly after the UI is ready, then every 30 minutes.</summary>
    public void Start() { _started = true; _timer.Change(UpdatePolicy.StartDelay, UpdatePolicy.Interval); }
    private bool _started;

    /// <summary>Window activated: check again when the last check is at least 5 minutes old (never during a download).</summary>
    public void OnActivated()
    {
        if (!_started || Volatile.Read(ref _busy) == 1) return;
        if (Policy.TryFocusCheck()) _ = CheckAsync(manual: false);
    }

    /// <summary>"Later" or the toast's close button: don't offer this version again during this session.</summary>
    public void Dismiss(string? version)
    {
        version ??= _last?.Release?.Version.ToString();
        if (version != null) { Policy.Dismiss(version); Log.Info("update " + version + ": postponed for this session"); }
    }

    private async Task<HttpResult> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var res = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var headers = res.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        return new HttpResult((int)res.StatusCode, await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), headers);
    }

    public async Task CheckAsync(bool manual)
    {
        if (manual) { _post(new { type = "updateStatus", state = "checking" }); Policy.MarkChecked(); }
        if (!manual && Volatile.Read(ref _busy) == 1) return;                   // downloading: the toast shows progress
        UpdateCheck c;
        try { c = await _checker.CheckAsync().ConfigureAwait(false); }
        catch (Exception e) { c = new UpdateCheck(UpdateState.Error, Message: e.Message); }
        _last = c;
        Log.Info($"update check: {c.State} {c.Release?.Version} {c.Message}");
        if (c.State == UpdateState.Available && c.Release != null)
        {
            var v = c.Release.Version.ToString();
            if (Store.IsFailed(v) && !manual) return;                           // rolled back before: don't nag
            var pending = Store.LoadPending();
            if (pending?.Version == v && !manual)
            {
                if (Policy.ShouldShowPending(v, manual)) _post(new { type = "updateStatus", state = "pending", version = v });
                return;
            }
            if (!Policy.ShouldOffer(v, manual)) { Log.Info($"update {v}: already offered this session"); return; }
            _post(new { type = "update", manual, current = Current.ToString(), release = c.ToUi() });
        }
        else if (manual) _post(new { type = "updateStatus", state = c.ToUi() });
    }

    public object Info() => new { current = Current.ToString(), last = _last?.ToUi(), pending = Store.LoadPending()?.Version };

    private async Task<PendingUpdate?> DownloadAsync()
    {
        var rel = _last?.Release;
        if (rel == null || _last!.State != UpdateState.Available) { await CheckAsync(manual: false).ConfigureAwait(false); rel = _last?.Release; }
        if (rel == null || rel.Version <= Current) { _post(new { type = "updateStatus", state = "uptodate" }); return null; }
        var throttle = new ProgressThrottle(TimeSpan.FromMilliseconds(100));        // max ~10 UI messages per second
        var ver = rel.Version.ToString();
        var progress = new SyncProgress(p => { if (throttle.ShouldReport(p)) _post(new { type = "updateStatus", state = "downloading", progress = Math.Round(p, 4), version = ver }); });
        return await Store.DownloadAsync(rel, (u, ct) => _http.GetStringAsync(u, ct), DownloadFileAsync, progress).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        var buf = new byte[1 << 16]; long done = 0; int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n; if (total > 0) progress?.Report((double)done / total);
        }
    }

    /// <summary>"Nu updaten": download + verify, then the caller restores the games and hands over to the helper.</summary>
    public async Task<PendingUpdate?> PrepareNowAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        try { return await DownloadAsync().ConfigureAwait(false); }
        catch (Exception e) { Log.Error("update download: " + e.Message); _post(new { type = "updateStatus", state = "error", message = e.Message }); return null; }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>"Later": download + verify in the background; the next start installs it before the UI opens.</summary>
    public async Task LaterAsync()
    {
        Dismiss(null);
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var p = await DownloadAsync().ConfigureAwait(false);
            if (p != null && Policy.ShouldShowPending(p.Version, manual: true)) _post(new { type = "updateStatus", state = "pending", version = p.Version });
        }
        catch (Exception e) { Log.Error("update download: " + e.Message); _post(new { type = "updateStatus", state = "error", message = e.Message }); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose() { _timer.Dispose(); _http.Dispose(); }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; posts every report to the thread pool, which
    /// can deliver them out of order and defeats the throttle).</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _a; private readonly object _l = new();
        public SyncProgress(Action<double> a) => _a = a;
        public void Report(double v) { lock (_l) _a(v); }
    }
}

/// <summary>
/// The helper runs from a temp copy of Vanta.exe (so the real exe can be replaced):
///   Vanta.exe --apply-update --pid N --app "C:\...\Vanta" [--elevated]
/// It waits for the old process, elevates when the app folder is not writable (Program Files), backs up, installs, starts the
/// new version and waits for its "started" marker. No marker within 90 s (or an early exit) = roll back to the backup,
/// remember the version as failed and start the old version again.
/// </summary>
internal static class UpdateHelper
{
    private const int StartTimeoutSec = 90;
    private static string? _log;

    public static bool IsHelperCommand(string[] args) => args.Length > 0 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the running exe to %LOCALAPPDATA%\Vanta\updates\helper and starts it. Returns false when that failed.</summary>
    public static bool Launch(UpdateStore store)
    {
        try
        {
            var self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, UpdateApplier.ExeName);
            Directory.CreateDirectory(store.HelperDir);
            var helper = Path.Combine(store.HelperDir, "Vanta-updater.exe");
            File.Copy(self, helper, true);
            var appDir = Path.GetDirectoryName(self)!;
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = store.HelperDir };
            psi.ArgumentList.Add("--apply-update"); psi.ArgumentList.Add("--pid"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--app"); psi.ArgumentList.Add(appDir);
            Process.Start(psi);
            Log.Info("update: helper started for " + appDir);
            return true;
        }
        catch (Exception e) { Log.Error("update helper: " + e.Message); return false; }
    }

    private static string? Arg(string[] a, string name) { var i = Array.FindIndex(a, x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static void L(string t) { try { if (_log != null) File.AppendAllText(_log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {t}{Environment.NewLine}"); } catch { } }

    public static int Run(string[] args)
    {
        var store = new UpdateStore(Settings.DataDir);
        Directory.CreateDirectory(store.Dir);
        _log = Path.Combine(store.Dir, "update.log");
        var appDir = Arg(args, "--app");
        var pidText = Arg(args, "--pid");
        bool elevated = args.Contains("--elevated", StringComparer.OrdinalIgnoreCase);
        L($"helper start: app={appDir} pid={pidText} elevated={elevated} admin={Elevation.IsAdmin}");
        if (appDir == null || !Directory.Exists(appDir)) { L("no valid app folder"); return 3; }
        if (int.TryParse(pidText, out var pid))
            try { using var p = Process.GetProcessById(pid); if (!p.WaitForExit(30000)) { L("old Vanta does not exit; stopping"); return 4; } } catch (ArgumentException) { }

        var pending = store.LoadPending();
        if (pending == null || !File.Exists(pending.Zip)) { L("no pending update"); StartApp(appDir, null, elevated); return 5; }

        if (!UpdateApplier.CanWrite(appDir))
        {
            if (Elevation.IsAdmin || elevated) { L("app folder not writable, not even as admin"); return Fail(store, pending, appDir, null, elevated, "no write access"); }
            L("app folder not writable: restarting with administrator rights (UAC)");
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = store.HelperDir };
                foreach (var a in args) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--elevated");
                using var p = Process.Start(psi)!; p.WaitForExit(); return p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception) { L("UAC geweigerd: update overgeslagen, oude versie start"); StartApp(appDir, null, false); return 6; }
        }

        string content;
        try { content = UpdateApplier.Apply(pending.Zip, pending.Sha256, appDir, store, L); }
        catch (Exception e) { L("install failed: " + e.Message); return Fail(store, pending, appDir, null, elevated, e.Message); }

        var marker = store.StartedMarker(pending.Version);
        try { File.Delete(marker); } catch { }
        L("nieuwe versie starten: " + pending.Version);
        using var proc = StartApp(appDir, new[] { "--updated", pending.Version }, elevated);
        var until = DateTime.UtcNow.AddSeconds(StartTimeoutSec);
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(marker))
            {
                L("new version is running: done");
                store.ClearPending();
                try { Directory.Delete(store.StagingDir, true); } catch { }
                try { File.Delete(pending.Zip); } catch { }
                return 0;
            }
            if (proc != null && proc.HasExited) { L($"nieuwe versie stopte direct (exit {proc.ExitCode})"); break; }
            Thread.Sleep(500);
        }
        try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
        KillAppProcesses(appDir);
        return Fail(store, pending, appDir, content, elevated, "new version did not start");
    }

    private static int Fail(UpdateStore store, PendingUpdate pending, string appDir, string? content, bool elevated, string why)
    {
        if (content != null)
            try { UpdateApplier.Rollback(store.BackupDir, appDir, content); L("rolled back to the previous version"); }
            catch (Exception e) { L("rollback failed: " + e.Message + " - backup is in " + store.BackupDir); }
        store.MarkFailed(pending.Version);
        store.ClearPending();
        StartApp(appDir, new[] { "--update-failed", pending.Version }, elevated)?.Dispose();
        L("update " + pending.Version + " failed: " + why);
        return 2;
    }

    /// <summary>Starts Vanta.exe. From an elevated helper it goes through explorer.exe so the app runs as the normal user again.</summary>
    private static Process? StartApp(string appDir, string[]? args, bool elevated)
    {
        var exe = Path.Combine(appDir, UpdateApplier.ExeName);
        try
        {
            if (elevated || Elevation.IsAdmin)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { exe } })?.Dispose();
                return null;                              // explorer can't pass args or give us the process: the marker decides
            }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = appDir };
            if (args != null) foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi);
        }
        catch (Exception e) { L("start failed: " + e.Message); return null; }
    }

    private static void KillAppProcesses(string appDir)
    {
        var exe = Path.GetFullPath(Path.Combine(appDir, UpdateApplier.ExeName));
        foreach (var p in Process.GetProcessesByName("Vanta"))
            try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) p.Kill(true); } catch { } finally { p.Dispose(); }
    }
}

/// <summary>Small borderless "Updating Vanta…" window shown while a pending update is handed to the helper.</summary>
internal sealed class UpdateSplash : Form
{
    public UpdateSplash(string version)
    {
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true;
        Size = new Size(420, 150); BackColor = Color.FromArgb(0x0B, 0x0D, 0x14); Text = Branding.Name;
        var title = new Label { Text = Strings.Get("update.splash.title"), ForeColor = Color.FromArgb(0xEE, 0xF0, 0xF8), Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(28, 34) };
        var sub = new Label { Text = Strings.Get("update.splash.sub", version), ForeColor = Color.FromArgb(0x9A, 0xA0, 0xB8), Font = new Font("Segoe UI", 9.5f), AutoSize = true, Location = new Point(30, 74) };
        var bar = new Panel { BackColor = Color.FromArgb(0x74, 0x66, 0xFF), Location = new Point(30, 110), Size = new Size(360, 3) };
        Controls.AddRange(new Control[] { title, sub, bar });
    }
}
