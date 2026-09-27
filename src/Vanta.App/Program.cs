using System.Runtime.InteropServices;
using Vanta.Core;

namespace Vanta.App;

internal static class Program
{
    [DllImport("kernel32")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32")] private static extern bool AllocConsole();
    [DllImport("kernel32")] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32")] private static extern uint GetFileType(IntPtr h);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr h, int cmd);

    [STAThread]
    private static int Main(string[] args)
    {
        // Console modes: Vanta.exe --selftest | validate/index/import/asm ... (same tools as vanta-tool)
        if (args.Length > 0)
        {
            var cmd = args[0].TrimStart('-', '/').ToLowerInvariant();
            if (cmd == "selftest" || Cli.Commands.Contains(cmd) || cmd is "help" or "h" or "?")
                return RunConsole(cmd, args);
        }

        // Update helper (temp copy of Vanta.exe): replaces the app files, restarts, rolls back on failure.
        if (UpdateHelper.IsHelperCommand(args)) { Log.Init(); return UpdateHelper.Run(args); }

        using var mutex = new Mutex(true, Branding.MutexName, out bool first);
        if (!first)
        {
            // already running: bring that window to the front instead of starting a second engine
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(System.Diagnostics.Process.GetCurrentProcess().ProcessName))
                if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero) { ShowWindow(p.MainWindowHandle, 9); SetForegroundWindow(p.MainWindowHandle); }
            return 0;
        }

        Log.Init();
        Log.Info($"{Branding.Name} {Branding.Version} start, OS {Environment.OSVersion}, admin={Elevation.IsAdmin}");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // "Later" chosen last time: install the downloaded + verified update before the UI opens.
        if (!args.Contains("--no-update", StringComparer.OrdinalIgnoreCase))
        {
            var store = new Core.Update.UpdateStore(Settings.DataDir);
            var pending = store.ApplicablePending(Core.Update.SemVer.Parse(Branding.Version));
            if (pending != null)
            {
                Log.Info($"update: pending {pending.Version} wordt geïnstalleerd");
                UpdateSplash? splash = null;
                try { splash = new UpdateSplash(pending.Version); splash.Show(); Application.DoEvents(); }
                catch (Exception e) { Log.Error("update splash: " + e.Message); splash = null; }   // the splash is optional
                bool launched = UpdateHelper.Launch(store);
                if (launched) Thread.Sleep(1200);
                try { splash?.Close(); } catch { }
                if (launched) { mutex.ReleaseMutex(); return 0; }
            }
        }
        Application.ThreadException += (_, e) => Log.Error("UI: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Error("fatal: " + e.ExceptionObject); Host.EmergencyRestore(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Host.EmergencyRestore();
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunConsole(string cmd, string[] args)
    {
        // stdout already redirected to a file/pipe (e.g. "Vanta.exe --selftest > log.txt"): just use it.
        var h = GetStdHandle(-11);
        bool redirected = h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) is 1 or 3;
        bool own = false;
        if (!redirected)
        {
            own = !AttachConsole(-1);            // started from Explorer: open our own console window and wait at the end
            if (own) AllocConsole();
        }
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Console.SetOut(stdout);
        if (!own) Console.WriteLine();
        int rc;
        if (cmd == "selftest")
        {
            var dummy = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "selftest", "vanta_dummy.exe");
            using var s = typeof(Program).Assembly.GetManifestResourceStream("selftest.game.json")!;
            var json = new StreamReader(s).ReadToEnd();
            var logFile = Path.Combine(Settings.DataDir, "selftest.log");
            Directory.CreateDirectory(Settings.DataDir);
            using var tee = new TeeWriter(stdout, logFile);
            rc = new Core.Win.SelfTest(tee).Run(dummy, json);
            tee.WriteLine($"Log: {logFile}");
        }
        else rc = Cli.Run(args, stdout);
        if (own) { Console.WriteLine(); Console.WriteLine("Druk op Enter om te sluiten…"); try { Console.ReadLine(); } catch { } }
        return rc;
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a; private readonly StreamWriter _b;
        public TeeWriter(TextWriter a, string file) { _a = a; _b = new StreamWriter(file, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true }; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string? value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        protected override void Dispose(bool disposing) { if (disposing) _b.Dispose(); base.Dispose(disposing); }
    }
}

internal static class Elevation
{
    public static bool IsAdmin
    {
        get
        {
            try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }
}

/// <summary>Daily log file in %LOCALAPPDATA%\Vanta\logs (last 7 kept).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _file;
    public static string Dir => Path.Combine(Settings.DataDir, "logs");
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _file = Path.Combine(Dir, $"vanta-{DateTime.Now:yyyyMMdd}.log");
            foreach (var old in Directory.GetFiles(Dir, "vanta-*.log").OrderByDescending(f => f).Skip(7)) File.Delete(old);
        }
        catch { _file = null; }
    }
    public static void Write(string level, string text)
    {
        if (_file == null) return;
        lock (Gate) { try { File.AppendAllText(_file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {text}{Environment.NewLine}"); } catch { } }
    }
    public static void Info(string t) => Write("info", t);
    public static void Error(string t) => Write("error", t);
}
