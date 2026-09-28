using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Vanta.Core;
using Vanta.Core.Engine;

namespace Vanta.App;

/// <summary>Frameless window hosting the web UI (embedded resources served as https://vanta.example/...).</summary>
internal sealed class MainForm : Form
{
    private const string Origin = "https://" + Branding.UiHost;
    private const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string Csp = "default-src 'self'; img-src 'self' https://vanta.example https://cdn.discordapp.com data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'none'";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 12, 16) };
    private Host? _host;
    private AppUpdater? _updater;
    private bool _uiReadyOnce;
    private HotkeyWindow? _hotkeys;
    private bool _ready;

    public MainForm()
    {
        Text = Branding.Name;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(10, 12, 16);
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Min(1320, wa.Width - 40), Math.Min(860, wa.Height - 40));
        MinimumSize = new Size(1040, 680);
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        Activated += (_, _) => _updater?.OnActivated();              // focus: re-check (throttled to once per 5 min)
        FormClosing += (_, _) => { _hotkeys?.UnregisterAll(); _host?.Shutdown(); _updater?.Dispose(); };
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _host?.Shutdown();
    }

    private async Task InitAsync()
    {
        string? version = null;
        try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (WebView2RuntimeNotFoundException) { }
        if (version == null) { MissingRuntime(); return; }
        try
        {
            _host = new Host(json => Post(json));
            _updater = new AppUpdater(o => Post(JsonSerializer.Serialize(o, Vanta.Core.Json.Compact)));
            _hotkeys = new HotkeyWindow(b => _host.Enqueue(() => _host.Controller.OnHotkey(b)), msg => Log.Error(msg));
            _host.HotkeysChanged += list => BeginInvokeSafe(() => _hotkeys.Register(list));
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.DataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = Environment.GetCommandLineArgs().Contains("--devtools");
            s.AreBrowserAcceleratorKeysEnabled = false;   // F5/Ctrl+R must not reload: F5 is a cheat hotkey
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsPasswordAutosaveEnabled = false; s.IsGeneralAutofillEnabled = false;
            try { s.IsNonClientRegionSupportEnabled = true; } catch { /* older runtime: JS drag fallback */ }
            core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResource;
            core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin)) { e.Cancel = true; OpenExternal(e.Uri); } };
            core.DocumentTitleChanged += (_, _) => Text = Branding.Name;
            await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.vantaHost = {{ library: {_host.LibraryJson()}, admin: {(Elevation.IsAdmin ? "true" : "false")} }};");
            core.Navigate(Origin + "/index.html");
            _ready = true;
            Log.Info($"WebView2 {version}");
        }
        catch (Exception e)
        {
            Log.Error("init: " + e);
            MessageBox.Show(this, Strings.Get("app.startFail", e.Message, Log.Dir), Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task UpdateNowAsync()
    {
        if (_updater == null || _host == null) return;
        var pending = await _updater.PrepareNowAsync();
        if (pending == null) return;
        BeginInvokeSafe(() =>
        {
            Post(JsonSerializer.Serialize(new { type = "updateStatus", state = "installing", version = pending.Version }, Vanta.Core.Json.Compact));
            _hotkeys?.UnregisterAll();
            _host.Shutdown();                                   // restore every patched game first
            if (UpdateHelper.Launch(_updater.Store)) Close();
            else Post(JsonSerializer.Serialize(new { type = "updateStatus", state = "error", message = Strings.Get("app.helperFail") }, Vanta.Core.Json.Compact));
        });
    }

    private void MissingRuntime()
    {
        Log.Error("WebView2 runtime not found");
        var r = MessageBox.Show(this,
            Strings.Get("webview2.missing", WebView2Download),
            Strings.Get("webview2.missing.title", Branding.Name), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (r == DialogResult.OK) OpenExternal(WebView2Download);
        Close();
    }

    private static void OpenExternal(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception e) { Log.Error("open url: " + e.Message); }
    }

    private void BeginInvokeSafe(Action a) { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(a); } catch (InvalidOperationException) { } }

    private void Post(string json) => BeginInvokeSafe(() => { if (_ready) try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception e) { Log.Error("post: " + e.Message); } });

    // ---------------- UI -> host ----------------
    private void OnMessage(string json)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(json).RootElement; } catch { return; }
        var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
        int reqId = m.TryGetProperty("reqId", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
        void Ack() => Post(JsonSerializer.Serialize(new { type = "ack", reqId, ok = true }));
        switch (type)
        {
            case "window":
                var action = m.TryGetProperty("action", out var a) ? a.GetString() : null;
                if (action == "minimize") WindowState = FormWindowState.Minimized;
                else if (action == "maximize") WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                else if (action == "close") Close();
                else if (action == "drag") { ReleaseCapture(); SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0); }
                Ack(); return;
            case "hotkeyCapture":
                _hotkeys?.Suspend(m.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True);
                Ack(); return;
            case "ready":
                if (!_uiReadyOnce)
                {
                    _uiReadyOnce = true;
                    _updater?.Store.MarkStarted(Branding.Version);      // tells the update helper this version starts fine
                    _updater?.Start();
                    var argv = Environment.GetCommandLineArgs();
                    int ui = Array.IndexOf(argv, "--updated"), uf = Array.IndexOf(argv, "--update-failed");
                    if (ui > 0) Post(JsonSerializer.Serialize(new { type = "updateStatus", state = "updated", version = Branding.Version }, Vanta.Core.Json.Compact));
                    if (uf > 0 && uf + 1 < argv.Length) Post(JsonSerializer.Serialize(new { type = "updateStatus", state = "failed", version = argv[uf + 1] }, Vanta.Core.Json.Compact));
                }
                _host?.HandleUiJson(json);
                return;
            case "checkUpdate":
                if (_updater != null) _ = _updater.CheckAsync(manual: true);
                Ack(); return;
            case "updateLater":
                if (_updater != null) _ = _updater.LaterAsync();
                Ack(); return;
            case "updateDismiss":
                _updater?.Dismiss(m.TryGetProperty("version", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null);
                Ack(); return;
            case "updateNow":
                Ack(); _ = UpdateNowAsync(); return;
            case "openUrl":
                if (m.TryGetProperty("url", out var u)) OpenExternal(u.GetString() ?? "");
                Ack(); return;
            default:
                _host?.HandleUiJson(json);
                return;
        }
    }

    // ---------------- embedded UI + art ----------------
    private void OnResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        var env = _web.CoreWebView2.Environment;
        if (path.StartsWith("art/"))
        {
            var parts = path.Split('/');
            if (parts.Length == 3 && long.TryParse(parts[1], out var appId) && _host != null)
            {
                var deferral = e.GetDeferral();
                _ = ServeArt(e, deferral, env, appId, parts[2]);
                return;
            }
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        if (path.Length == 0) path = "index.html";
        var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ui/" + path);
        if (stream == null) { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
        var headers = $"Content-Type: {Mime(path)}\r\nCache-Control: no-cache\r\nContent-Security-Policy: {Csp}\r\nX-Content-Type-Options: nosniff";
        e.Response = env.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    private async Task ServeArt(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Deferral deferral, CoreWebView2Environment env, long appId, string kind)
    {
        try
        {
            var art = await _host!.Art.GetAsync(appId, kind);
            BeginInvokeSafe(() =>
            {
                try
                {
                    e.Response = art == null ? env.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store")
                        : env.CreateWebResourceResponse(new MemoryStream(art.Value.data), 200, "OK", $"Content-Type: {art.Value.type}\r\nCache-Control: max-age=86400");
                }
                finally { deferral.Complete(); }
            });
        }
        catch (Exception ex) { Log.Error("art: " + ex.Message); BeginInvokeSafe(() => { try { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); } finally { deferral.Complete(); } }); }
    }

    private static string Mime(string p) => Path.GetExtension(p).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".ttf" => "font/ttf", ".png" => "image/png", ".jpg" => "image/jpeg", ".svg" => "image/svg+xml", ".json" => "application/json", _ => "application/octet-stream",
    };

    // ---------------- frameless window: resize borders + aero snap ----------------
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00040000 /*WS_THICKFRAME*/ | 0x00020000 /*WS_MINIMIZEBOX*/ | 0x00010000 /*WS_MAXIMIZEBOX*/;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x83;
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Keep the standard (invisible) resize borders, snap and shadow of WS_THICKFRAME; drop only the caption.
            var before = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            base.WndProc(ref m);
            var after = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            after.r0.top = before.r0.top + (WindowState == FormWindowState.Maximized ? after.r0.left - before.r0.left : 0);
            Marshal.StructureToPtr(after, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr lppos; }
}

/// <summary>Global hotkeys via RegisterHotKey on a message-only window.</summary>
internal sealed class HotkeyWindow : NativeWindow
{
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    private readonly Action<HotkeyBinding> _fire;
    private readonly Action<string> _log;
    private readonly Dictionary<int, HotkeyBinding> _map = new();
    private List<HotkeyBinding> _wanted = new();
    private bool _suspended;

    public HotkeyWindow(Action<HotkeyBinding> fire, Action<string> log)
    {
        _fire = fire; _log = log;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /*HWND_MESSAGE*/ });
    }

    public void Register(List<HotkeyBinding> list) { _wanted = list; if (!_suspended) Apply(); }
    public void Suspend(bool on) { _suspended = on; if (on) UnregisterAll(); else Apply(); }

    public void UnregisterAll()
    {
        foreach (var id in _map.Keys) UnregisterHotKey(Handle, id);
        _map.Clear();
    }

    private void Apply()
    {
        UnregisterAll();
        int id = 1;
        foreach (var b in _wanted)
        {
            if (!HotkeyParser.TryParse(b.Combo, out var mods, out var vk)) { _log($"hotkey '{b.Combo}' invalid"); continue; }
            if (b.Kind == "") mods |= HotkeyParser.MOD_NOREPEAT;      // toggles: no auto-repeat; +/- may repeat while held
            if (RegisterHotKey(Handle, id, mods, vk)) _map[id++] = b;
            else _log($"hotkey {b.Combo} ({b.CheatId}) could not be registered (in use by another program?)");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /*WM_HOTKEY*/ && _map.TryGetValue(m.WParam.ToInt32(), out var b)) _fire(b);
        base.WndProc(ref m);
    }
}
