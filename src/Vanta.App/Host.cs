using System.Collections.Concurrent;
using System.Text.Json;
using Vanta.Core;
using Vanta.Core.Catalog;
using Vanta.Core.Engine;
using Vanta.Core.Win;

namespace Vanta.App;

/// <summary>
/// Owns the engine thread. Every TrainerController call happens on that one thread (queue + 200 ms tick / 1 s poll),
/// so the UI thread never blocks on game memory.
/// </summary>
internal sealed class Host : IDisposable
{
    private static Host? _current;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Action<string> _post;          // JSON -> UI (any thread)
    public TrainerController Controller { get; private set; } = null!;
    public Settings Settings { get; }
    public ICatalogSource Catalog { get; }
    public ArtCache Art { get; }
    public event Action<List<HotkeyBinding>>? HotkeysChanged;
    private volatile bool _stopped;
    private int _restored;

    public Host(Action<string> post)
    {
        _post = post;
        Settings = Settings.Load();
        Strings.Lang = Settings.Language;
        Art = new ArtCache(Path.Combine(Settings.DataDir, "art"));
        var dir = string.IsNullOrWhiteSpace(Settings.CatalogDir) ? Path.Combine(AppContext.BaseDirectory, "games") : Settings.CatalogDir!;
        try
        {
            Directory.CreateDirectory(dir);
            var local = new LocalCatalog(dir);
            foreach (var p in local.Problems) Log.Error("catalog: " + p);
            Log.Info($"catalog {dir}: {local.Entries.Count} game(s){(local.IndexRebuilt ? " (index rebuilt)" : "")}");
            Catalog = local;
        }
        catch (Exception e) { Log.Error("catalog: " + e.Message); Catalog = new LocalCatalog(Path.Combine(AppContext.BaseDirectory, "games"), writeIndex: false); }
        _current = this;
        _thread = new Thread(Loop) { IsBackground = true, Name = "vanta-engine" };
        var ready = new ManualResetEventSlim();
        _thread.Start(ready);
        ready.Wait();
    }

    private void Send(object msg) => _post(JsonSerializer.Serialize(msg, Vanta.Core.Json.Compact));

    private void Loop(object? readyObj)
    {
        Controller = new TrainerController(Catalog, new WinProcessProvider(), Settings, Send,
            url => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); return Task.CompletedTask; },
            s => { try { s.Save(); } catch (Exception e) { Log.Error("settings: " + e.Message); } },
            new Vanta.Core.Stores.StoreDetector(new WinStoreEnv()))
        { HostLog = t => Log.Write("engine", t) };
        Controller.HotkeysChanged += () => HotkeysChanged?.Invoke(Controller.HotkeyBindings());
        try
        {
            Controller.Account = new Vanta.Core.Account.AccountService(Settings, new Vanta.Core.Account.DpapiTokenStore(),
                Path.Combine(Settings.DataDir, "report-queue.json"),
                url => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); return Task.CompletedTask; },
                Send, log: t => Log.Write("account", t));
            Log.Info(Controller.Account.Configured ? "account: backend " + Controller.Account.Client!.BaseUrl : "account: geen backend ingesteld");
        }
        catch (Exception e) { Log.Error("account: " + e.Message); }
        ((ManualResetEventSlim)readyObj!).Set();
        var nextTick = DateTime.UtcNow; var nextPoll = DateTime.UtcNow;
        while (!_stopped)
        {
            var wait = (int)Math.Clamp((Min(nextTick, nextPoll) - DateTime.UtcNow).TotalMilliseconds, 0, 200);
            if (_queue.TryTake(out var job, wait)) { Safe(job); continue; }
            var now = DateTime.UtcNow;
            if (now >= nextPoll) { Safe(Controller.Poll); nextPoll = now.AddSeconds(1); }
            if (now >= nextTick) { Safe(Controller.Tick); nextTick = now.AddMilliseconds(200); }
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static void Safe(Action a) { try { a(); } catch (Exception e) { Log.Error("engine: " + e); } }

    public void Enqueue(Action a) { if (!_stopped) _queue.Add(a); }

    /// <summary>Runs on the engine thread and waits (used for shutdown).</summary>
    public bool Invoke(Action a, int timeoutMs)
    {
        if (_stopped) return false;
        using var done = new ManualResetEventSlim();
        _queue.Add(() => { try { a(); } finally { done.Set(); } });
        return done.Wait(timeoutMs);
    }

    public void HandleUiJson(string json)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement.Clone(); } catch { return; }
        Enqueue(() => Send(Controller.HandleUi(m)));
    }

    public string LibraryJson() => JsonSerializer.Serialize(Controller.Library(), Vanta.Core.Json.Compact);

    /// <summary>Restore every patched game. Safe to call more than once (only the first call does work).</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _restored, 1) == 1) return;
        Log.Info("shutdown: restoring games");
        if (Thread.CurrentThread == _thread) { try { Controller.Shutdown(); } catch (Exception e) { Log.Error("shutdown: " + e.Message); } }
        else if (!Invoke(Controller.Shutdown, 8000)) Log.Error("shutdown: restore timed out");
        try { Controller.Account?.Shutdown(); Controller.Account?.Dispose(); } catch (Exception e) { Log.Error("account shutdown: " + e.Message); }
        _stopped = true;
    }

    public static void EmergencyRestore()
    {
        var h = _current; if (h == null) return;
        try { h.Shutdown(); } catch { }
    }

    public void Dispose() { Shutdown(); _queue.Dispose(); }
}
