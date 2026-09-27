using Vanta.Core.Engine;
using Vanta.Core.Catalog;
using Vanta.Core.Import;

namespace Vanta.Core;

/// <summary>Command-line tools shared by vanta-tool (dev) and Vanta.exe (end users): validate, index, import.</summary>
public static class Cli
{
    public static readonly string[] Commands = { "validate", "index", "import", "asm", "selftest", "verify", "account-selftest" };

    public static int Run(string[] args, TextWriter o)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(o); return 0; }
        var cmd = args[0].TrimStart('-');
        var rest = args.Skip(1).ToArray();
        try
        {
            return cmd switch
            {
                "validate" => Validate(rest, o),
                "index" => Index(rest, o),
                "import" => Import(rest, o),
                "asm" => Asm(rest, o),
                "uifixture" => UiFixture(rest, o),
                "verify" => Verify(rest, o),
                "account-selftest" => new Account.AccountSelfTest(o).Run(rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1)),
                "selftest" => rest.Length >= 2 ? new Win.SelfTest(o).Run(rest[0], File.ReadAllText(rest[1])) : Fail(o, "selftest <vanta_dummy.exe> <selftest.game.json>"),
                _ => Fail(o, $"onbekend commando '{args[0]}'"),
            };
        }
        catch (Exception e) { return Fail(o, e.Message); }
    }

    private static int Fail(TextWriter o, string m) { o.WriteLine("FOUT: " + m); return 2; }

    private static void Usage(TextWriter o) => o.WriteLine($@"{Branding.Name} tools {Branding.Version}
  validate <game.json|map> [...]     schema + semantische controle (AOB's, asm, verwijzingen)
  index <games-map>                  bouwt games/index.json (catalogus-index)
  import <tabel.CT> [-o map] [--id id] [--name naam] [--process x.exe] [--appid n]
                                     zet een Cheat Engine-tabel om naar game.json (+ import-report.txt)
  asm <bestand.asm> [origin-hex]     assembleert AA-tekst (test)
  selftest <dummy.exe> <game.json>   engine-test tegen het dummy-doelproces (alleen Windows)
  verify <game-id|game.json> [pad]   zoekt elke AOB van de game in de module op schijf (pad = module-bestand of
                                     installatiemap; zonder pad: automatisch via Steam/Ubisoft/Epic/GOG/EA/Xbox)
  verify <game-id> --live            hetzelfde in de draaiende game (alleen lezen)
  account-selftest [url key]         controleert DPAPI-opslag en inloggen (loopback + PKCE, Supabase) op deze pc
  opties: --games <map>              andere catalogusmap (standaard: games naast Vanta.exe)");

    private static IEnumerable<string> GameFiles(string path)
    {
        if (File.Exists(path)) { yield return path; yield break; }
        if (!Directory.Exists(path)) throw new FileNotFoundException(path);
        var direct = Path.Combine(path, "game.json");
        if (File.Exists(direct)) { yield return direct; yield break; }
        foreach (var d in Directory.EnumerateDirectories(path).OrderBy(x => x))
        {
            var f = Path.Combine(d, "game.json");
            if (File.Exists(f)) yield return f;
        }
    }

    private static int Validate(string[] a, TextWriter o)
    {
        if (a.Length == 0) return Fail(o, "validate <game.json|map>");
        int errors = 0, warns = 0, files = 0;
        foreach (var path in a)
            foreach (var f in GameFiles(path))
            {
                files++;
                var list = GameValidator.ValidateFile(f);
                var e = list.Count(x => x.Level == "error"); var w = list.Count(x => x.Level == "warn");
                errors += e; warns += w;
                o.WriteLine($"{(e == 0 ? "OK  " : "FOUT")} {f}  ({e} fout(en), {w} waarschuwing(en))");
                foreach (var x in list) o.WriteLine("     " + x);
            }
        o.WriteLine($"{files} bestand(en), {errors} fout(en), {warns} waarschuwing(en)");
        return errors == 0 && files > 0 ? 0 : 1;
    }

    private static int Index(string[] a, TextWriter o)
    {
        if (a.Length == 0) return Fail(o, "index <games-map>");
        var problems = new List<string>();
        var idx = CatalogBuilder.Build(a[0], problems);
        CatalogBuilder.Save(idx, a[0]);
        foreach (var p in problems) o.WriteLine("WAARSCHUWING " + p);
        o.WriteLine($"index.json geschreven: {idx.Games.Count} game(s)");
        return problems.Count == 0 ? 0 : 1;
    }

    private static int Import(string[] a, TextWriter o)
    {
        if (a.Length == 0) return Fail(o, "import <tabel.CT> [-o map]");
        string? outDir = null, id = null, name = null, proc = null; long? appid = null;
        for (int i = 1; i < a.Length; i++)
        {
            string V() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"waarde ontbreekt na {a[i]}");
            switch (a[i]) { case "-o": outDir = V(); break; case "--id": id = V(); break; case "--name": name = V(); break; case "--process": proc = V(); break; case "--appid": appid = long.Parse(V()); break; default: return Fail(o, "onbekende optie " + a[i]); }
        }
        var res = CtImporter.Import(File.ReadAllText(a[0]), Path.GetFileNameWithoutExtension(a[0]), new ImportOptions { Id = id, Name = name, Process = proc, SteamAppId = appid });
        outDir ??= Path.Combine(Directory.GetCurrentDirectory(), res.Game.Id);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "game.json"), System.Text.Json.JsonSerializer.Serialize(res.Game, Json.Options));
        File.WriteAllText(Path.Combine(outDir, "import-report.txt"), res.Report());
        o.Write(res.Report());
        var val = GameValidator.ValidateFile(Path.Combine(outDir, "game.json"));
        o.WriteLine($"validatie: {val.Count(x => x.Level == "error")} fout(en), {val.Count(x => x.Level == "warn")} waarschuwing(en)");
        foreach (var x in val.Where(x => x.Level != "info")) o.WriteLine("     " + x);
        o.WriteLine($"geschreven: {Path.Combine(outDir, "game.json")}");
        return 0;
    }

    private static int Asm(string[] a, TextWriter o)
    {
        var origin = a.Length > 1 ? Convert.ToUInt64(a[1], 16) : 0x140000000UL;
        var res = Engine.MiniAssembler.Assemble(File.ReadAllLines(a[0]), origin);
        o.WriteLine(Engine.AobScanner.ToHex(res.Code));
        foreach (var (k, v) in res.Labels) o.WriteLine($"{k} = {v:X}");
        return 0;
    }

    public static GameDef LoadGameArg(string idOrFile, string? gamesDir)
    {
        if (File.Exists(idOrFile)) return Json.LoadGame(idOrFile);
        var dirs = new List<string>();
        if (gamesDir != null) dirs.Add(gamesDir);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "games"));
        dirs.Add(Path.Combine(Settings.DataDir, "games"));
        dirs.Add(Path.Combine(Directory.GetCurrentDirectory(), "games"));
        foreach (var d in dirs.Where(Directory.Exists))
        {
            var f = Path.Combine(d, idOrFile, "game.json");
            if (File.Exists(f)) return Json.LoadGame(f);
            foreach (var sub in Directory.EnumerateDirectories(d))
            {
                var gf = Path.Combine(sub, "game.json");
                if (File.Exists(gf) && Json.LoadGame(gf) is var g && g.Id.Equals(idOrFile, StringComparison.OrdinalIgnoreCase)) return g;
            }
        }
        throw new FileNotFoundException($"game '{idOrFile}' niet gevonden in: {string.Join("; ", dirs)}");
    }

    private static int Verify(string[] a, TextWriter o)
    {
        if (a.Length == 0) return Fail(o, "verify <game-id|game.json> [pad-naar-module-of-map | --live] [--games map]");
        string? gamesDir = null, path = null; bool live = false;
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] == "--games" && i + 1 < a.Length) gamesDir = a[++i];
            else if (a[i] == "--live") live = true;
            else path = a[i];
        }
        var g = LoadGameArg(a[0], gamesDir);
        List<string> lines; bool ok;
        if (live)
        {
            if (!OperatingSystem.IsWindows()) return Fail(o, "--live werkt alleen op Windows");
            var pp = new Win.WinProcessProvider();
            var p = pp.List().FirstOrDefault(x => g.ProcessNames.Any(n => n.Equals(x.Name, StringComparison.OrdinalIgnoreCase)));
            if (p == null) return Fail(o, $"{string.Join("/", g.ProcessNames)} draait niet");
            using var mem = pp.Open(p.Pid);
            var mod = mem.FindModule(g.MainModule);
            (lines, ok) = Verifier.Report(g, mem, mod?.Path != null ? new Dictionary<string, string> { [g.MainModule] = mod.Path } : null, pp.FileVersion(mod?.Path).fileVersion);
            lines.Insert(1, $"Live: PID {p.Pid} (alleen lezen)");
        }
        else
        {
            if (path == null)
            {
                if (!OperatingSystem.IsWindows()) return Fail(o, "geef het pad naar de module of de installatiemap op");
                var det = new Stores.StoreDetector(new Win.WinStoreEnv()).Detect(g, fresh: true);
                var inst = det.Primary ?? throw new InvalidOperationException("Geen installatie gevonden (Steam, Ubisoft Connect, Epic, GOG, EA app, Xbox). Geef het pad op: verify " + g.Id + " \"D:\\pad\\naar\\" + g.MainModule + "\" (of de installatiemap), of gebruik --live terwijl de game draait.");
                o.WriteLine($"Installatie gevonden: {inst.StoreName}: {inst.InstallDir}");
                path = inst.InstallDir!;
            }
            var files = Verifier.LocateModules(g, path, w => o.WriteLine("WAARSCHUWING " + w));
            if (!files.ContainsKey(g.MainModule)) return Fail(o, $"{g.MainModule} niet gevonden onder {path}");
            using var mem = new PeFileMemory();
            foreach (var (n, f) in files) mem.Add(f, n);
            string? fv = null;
            try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(files[g.MainModule]); fv = vi.FileVersion; } catch { }
            (lines, ok) = Verifier.Report(g, mem, files, string.IsNullOrWhiteSpace(fv) ? null : fv);
        }
        foreach (var l in lines) o.WriteLine(l);
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            var rep = Path.Combine(Settings.DataDir, $"verify-{g.Id}.txt");
            File.WriteAllLines(rep, lines);
            o.WriteLine("Rapport: " + rep);
        }
        catch { }
        return ok ? 0 : 1;
    }

    private sealed class NoProcesses : IProcessProvider
    {
        public IReadOnlyList<ProcInfo> List() => Array.Empty<ProcInfo>();
        public IProcessMemory Open(int pid) => throw new InvalidOperationException();
        public (string?, string?) FileVersion(string? path) => (null, null);
    }

    /// <summary>Dev only: writes ui/shared/devdata.js with the exact payloads the host sends (library + game per id).</summary>
    private static int UiFixture(string[] a, TextWriter o)
    {
        if (a.Length < 2) return Fail(o, "uifixture <games-map> <uit.js>");
        var cat = new Catalog.LocalCatalog(a[0], writeIndex: false);
        var first = cat.Entries.FirstOrDefault(e => e.Id == "the-last-caretaker") ?? cat.Entries.FirstOrDefault();
        var settings = new Settings { Recent = first == null ? new() : new() { first.Id } };
        // demo installs (default store paths) so the preview shows the store chip / "Start via ..."
        const string steam = @"C:\Program Files (x86)\Steam", ubi = @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Far Cry 6";
        var env = new Stores.FakeStoreEnv()
            .Key(@"HKCU\Software\Valve\Steam", ("SteamPath", steam.Replace('\\', '/')))
            .File(steam + @"\steamapps\appmanifest_552520.acf", "\"AppState\" { \"appid\" \"552520\" \"StateFlags\" \"4\" \"installdir\" \"FarCry5\" \"SizeOnDisk\" \"41234567890\" \"buildid\" \"18766066\" }")
            .File(steam + @"\steamapps\common\FarCry5\bin\FarCry5.exe")
            .Key(Stores.UbisoftProvider.InstallsKey + @"\5266", ("InstallDir", ubi.Replace('\\', '/') + "/"))
            .File(ubi + @"\bin\FarCry6.exe");
        var ctl = new TrainerController(cat, new NoProcesses(), settings, _ => { }, stores: new Stores.StoreDetector(env));
        var games = new System.Text.Json.Nodes.JsonObject();
        foreach (var e in cat.Entries)
            games[e.Id] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ctl.UiGame(e.Id), Json.Options))!["game"]!.DeepClone();
        var lib = System.Text.Json.JsonSerializer.Serialize(ctl.Library(), Json.Options);
        File.WriteAllText(a[1], "/* written by `vanta-tool uifixture` - dev/browser preview only (the app gets this from the host) */\n" +
            $"window.VantaDev = {{ library: {lib}, games: {games.ToJsonString(Json.Options)} }};\n");
        o.WriteLine($"{a[1]}: {cat.Entries.Count} game(s)");
        return 0;
    }
}
