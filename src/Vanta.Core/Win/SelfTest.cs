using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Vanta.Core.Catalog;
using Vanta.Core.Engine;

namespace Vanta.Core.Win;

/// <summary>
/// End-to-end check of the Windows memory engine against the harmless dummy target (selftest/vanta_dummy.exe):
/// AOB patch, code-cave injection, exported symbols, pointer chains via an AOB/RIP base, freeze, byte-exact restore,
/// game exit + restart with automatic re-apply, and the anti-cheat refusal. Run with <c>Vanta.exe --selftest</c>.
/// </summary>
public sealed class SelfTest
{
    private readonly TextWriter _out;
    private int _pass, _fail;
    private readonly List<Process> _started = new();
    public SelfTest(TextWriter output) => _out = output;

    private void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++; else _fail++;
        _out.WriteLine($"[{(ok ? "OK  " : "FOUT")}] {name}{(detail != null ? "  - " + detail : "")}");
    }

    private void Info(string s) => _out.WriteLine("       " + s);

    private sealed record Dummy(Process P, ulong Battery, ulong Xp, ulong Health);

    private Dummy StartDummy(string exe)
    {
        var psi = new ProcessStartInfo(exe, "--seconds 120") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        var p = Process.Start(psi) ?? throw new Exception("dummy start mislukt");
        _started.Add(p);
        var line = p.StandardOutput.ReadLine() ?? throw new Exception("dummy gaf geen uitvoer");
        var m = Regex.Match(line, @"battery=([0-9A-Fa-f]+) xp=([0-9A-Fa-f]+) health=([0-9A-Fa-f]+)");
        if (!m.Success) throw new Exception("onverwachte dummy-uitvoer: " + line);
        // keep draining stdout so the dummy never blocks
        _ = Task.Run(() => { try { while (p.StandardOutput.ReadLine() != null) { } } catch { } });
        ulong H(int i) => ulong.Parse(m.Groups[i].Value, NumberStyles.HexNumber);
        return new Dummy(p, H(1), H(2), H(3));
    }

    private static double ReadD(IProcessMemory m, ulong a) { var b = m.ReadBytes(a, 8); return b == null ? double.NaN : BitConverter.ToDouble(b); }
    private static int ReadI(IProcessMemory m, ulong a) => m.TryReadI32(a, out var v) ? v : int.MinValue;

    private static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    public int Run(string dummyExe, string gameJson)
    {
        _out.WriteLine($"{Branding.Name} {Branding.Version} selftest - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _out.WriteLine($"OS: {Environment.OSVersion}, 64-bit proces: {Environment.Is64BitProcess}, admin: {IsAdmin()}");
        _out.WriteLine($"Dummy: {dummyExe}");
        try { RunInner(dummyExe, gameJson); }
        catch (Exception e) { Check("onverwachte fout", false, e.ToString()); }
        finally
        {
            foreach (var p in _started) { try { if (!p.HasExited) p.Kill(); } catch { } }
        }
        _out.WriteLine();
        _out.WriteLine(_fail == 0 ? $"RESULTAAT: GESLAAGD ({_pass} controles)" : $"RESULTAAT: MISLUKT ({_fail} van {_pass + _fail} controles faalden)");
        return _fail == 0 ? 0 : 1;
    }

    private static bool IsAdmin()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void RunInner(string dummyExe, string gameJson)
    {
        if (!File.Exists(dummyExe)) { Check("dummy gevonden", false, dummyExe); return; }
        var errors = GameValidator.ValidateJson(gameJson).Where(f => f.Level == "error").ToList();
        Check("selftest-definitie valide", errors.Count == 0, string.Join("; ", errors));
        var g = System.Text.Json.JsonSerializer.Deserialize<GameDef>(gameJson, Json.Options)!;
        Json.Normalize(g);
        Check("confidence 'broken' geladen", g.Cheats.FirstOrDefault(c => c.Id == "broken_god")?.Confidence == "broken");

        var d = StartDummy(dummyExe);
        Info($"dummy pid {d.P.Id}, battery @ {d.Battery:X}, xp @ {d.Xp:X}, health @ {d.Health:X}");
        var prov = new WinProcessProvider();
        var mem = prov.Open(d.P.Id);
        Check("proces geopend (OpenProcess)", mem.IsAlive);
        ModuleInfo? mod = null;
        WaitFor(() => (mod = mem.FindModule(g.MainModule)) != null, 5000);
        Check("module gevonden (Toolhelp)", mod != null, mod != null ? $"{mod.Name} @ {mod.Base:X}, {mod.Size} bytes" : null);
        if (mod == null) return;
        var textBefore = mem.ReadBytes(mod.Base, (int)mod.Size);
        Check("module leesbaar", textBefore != null);

        var session = new TrainerSession(g, mem, (l, t) => Info($"{l}: {t}"));
        session.Runtime.FreeDelay = TimeSpan.FromMilliseconds(200);

        // 1. AOB patch
        var sw = Stopwatch.StartNew();
        session.Enable("battery");
        Check("AOB-patch toegepast (Infinite Battery)", session.IsActive("battery"), $"{sw.ElapsedMilliseconds} ms incl. scan");
        Thread.Sleep(200);
        var b1 = ReadD(mem, d.Battery); Thread.Sleep(400); var b2 = ReadD(mem, d.Battery);
        Check("batterij daalt niet meer", b1 == b2 && !double.IsNaN(b1), $"{b1} -> {b2}");
        session.Disable("battery");
        b1 = ReadD(mem, d.Battery); Thread.Sleep(400); b2 = ReadD(mem, d.Battery);
        Check("na uitzetten daalt batterij weer", b2 < b1, $"{b1} -> {b2}");

        // 2. code cave + exported symbol + pointer value
        session.Enable("xp_hook");
        Check("code-cave hook geplaatst (XP)", session.IsActive("xp_hook"), $"xp_data @ {session.Runtime.Symbols.GetValueOrDefault("xp_data"):X}");
        var cave = session.Runtime.Symbols.GetValueOrDefault("xp_data");
        Check("cave binnen ±2GB van module", cave != 0 && Math.Abs((long)cave - (long)mod.Base) < int.MaxValue, $"afstand {(long)cave - (long)mod.Base:X}");
        ulong? xpAddr = null;
        WaitFor(() => (xpAddr = session.Runtime.ResolveAddress(session.Cheat("xp_value"))) != null, 3000);
        Check("pointer via geëxporteerd symbool wijst naar dummy-XP", xpAddr == d.Xp, $"{xpAddr:X} vs {d.Xp:X}");
        session.SetValue("xp_value", 5000);
        Thread.Sleep(150);
        Check("XP waarde gezet", ReadI(mem, d.Xp) >= 5000, $"xp={ReadI(mem, d.Xp)}");
        session.SetValue("xp_mult", 3);
        int x1 = ReadI(mem, d.Xp); Thread.Sleep(500); int x2 = ReadI(mem, d.Xp);
        Check("XP-multiplier x3 actief", x2 > x1 && (x2 - x1) % 30 == 0, $"delta {x2 - x1} (veelvoud van 30)");

        // 3. AOB/RIP static pointer + freeze
        var hp = session.Runtime.ResolveAddress(session.Cheat("health"));
        Check("statische pointer via AOB+RIP", hp == d.Health, $"{hp:X} vs {d.Health:X}");
        session.SetValue("health", 5000);
        Check("health gezet", ReadI(mem, d.Health) > 4000, $"health={ReadI(mem, d.Health)}");
        session.Enable("god");
        // the dummy deals 1 damage every 50 ms; without freeze health would drop ~20 in a second
        int minHp = int.MaxValue;
        for (int i = 0; i < 10; i++) { Thread.Sleep(100); minHp = Math.Min(minHp, ReadI(mem, d.Health)); session.Tick(); }
        session.Tick();
        Check("freeze houdt health op 999", ReadI(mem, d.Health) == 999 && minHp >= 995, $"na tick {ReadI(mem, d.Health)}, laagste {minHp}");

        // 4. restore everything, byte-exact
        session.Enable("battery");
        session.RestoreAll(resetValues: true);
        Thread.Sleep(300);
        var textAfter = mem.ReadBytes(mod.Base, (int)mod.Size);
        int diff = textBefore == null || textAfter == null ? -1 : Enumerable.Range(0, textBefore.Length).Count(i => textBefore[i] != textAfter[i]);
        // the module's own .data changes while it runs (battery/xp): compare only executable regions
        int codeDiff = CodeDiff(mem, mod, textBefore!, textAfter!);
        Check("alles hersteld: code byte-identiek", codeDiff == 0, $"{codeDiff} gewijzigde code-bytes ({diff} totaal incl. data)");
        Check("cave vrijgegeven", !mem.Regions(cave, cave + 1).Any(r => r.Start <= cave && cave < r.Start + r.Size && r.Readable), $"{cave:X}");
        mem.Dispose();

        // 5. controller: detection, auto-attach, game restart -> re-apply, anti-cheat refusal
        var blocked = new GameDef { Id = "blocked", Name = "Online game", ProcessNames = { "vanta_dummy.exe" }, AntiCheat = true, Cheats = g.Cheats };
        var cat = new SingleCatalog(g, blocked);
        var sent = new List<string>();
        var statusFile = Path.Combine(Path.GetTempPath(), $"vanta-selftest-status-{Environment.ProcessId}.json");
        try { File.Delete(statusFile); } catch { }
        var ctl = new TrainerController(cat, prov, new Settings { AttachDelaySec = 0.3, Language = "nl" }, o => sent.Add(System.Text.Json.JsonSerializer.Serialize(o)),
            status: new StatusStore(statusFile));
        Check("anti-cheat game geweigerd", ctl.StatusOf("blocked") == "blocked");
        WaitFor(() => { ctl.Poll(); return ctl.StatusOf(g.Id) == "attached"; }, 8000);
        Check("auto-koppelen aan draaiend proces", ctl.StatusOf(g.Id) == "attached");
        Check("anti-cheat game niet gekoppeld ondanks draaiend proces", ctl.SessionOf("blocked") == null && ctl.StatusOf("blocked") == "blocked");
        ctl.HandleUi(System.Text.Json.JsonDocument.Parse($"{{\"type\":\"toggle\",\"gameId\":\"{g.Id}\",\"id\":\"battery\",\"enabled\":true}}").RootElement);
        Check("cheat aan via controller", ctl.SessionOf(g.Id)?.IsActive("battery") == true);
        SelfTestStatus(ctl, g, statusFile);
        d.P.Kill(); d.P.WaitForExit(5000);
        WaitFor(() => { ctl.Tick(); ctl.Poll(); return ctl.StatusOf(g.Id) == "notfound"; }, 5000);
        Check("game-exit gedetecteerd", ctl.StatusOf(g.Id) == "notfound");
        var d2 = StartDummy(dummyExe);
        WaitFor(() => { ctl.Poll(); return ctl.StatusOf(g.Id) == "attached"; }, 8000);
        Check("na herstart opnieuw gekoppeld", ctl.StatusOf(g.Id) == "attached", $"pid {d2.P.Id}");
        Check("actieve cheat opnieuw toegepast na herstart", ctl.SessionOf(g.Id)?.IsActive("battery") == true);
        using (var m2 = prov.Open(d2.P.Id))
        {
            b1 = ReadD(m2, d2.Battery); Thread.Sleep(400); b2 = ReadD(m2, d2.Battery);
            Check("batterij bevroren in nieuw proces", b1 == b2, $"{b1} -> {b2}");
            ctl.Shutdown();
            b1 = ReadD(m2, d2.Battery); Thread.Sleep(400); b2 = ReadD(m2, d2.Battery);
            Check("afsluiten van Vanta herstelt de game", b2 < b1, $"{b1} -> {b2}");
        }
    }

    private static System.Text.Json.JsonElement Msg(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
    private static bool AckOk(object ack) => System.Text.Json.JsonSerializer.SerializeToElement(ack).GetProperty("ok").GetBoolean();

    /// <summary>'broken' confidence refusal, "toch proberen" override, and local test status persisted in status.json.</summary>
    private void SelfTestStatus(TrainerController ctl, GameDef g, string statusFile)
    {
        string T(string id, bool en, bool force = false) => $"{{\"type\":\"toggle\",\"gameId\":\"{g.Id}\",\"id\":\"{id}\",\"enabled\":{(en ? "true" : "false")}{(force ? ",\"force\":true" : "")}}}";
        string S(string id, string? st) => $"{{\"type\":\"setStatus\",\"gameId\":\"{g.Id}\",\"id\":\"{id}\",\"status\":{(st == null ? "null" : "\"" + st + "\"")}}}";
        var s = ctl.SessionOf(g.Id)!;
        var bg = g.Cheats.First(c => c.Id == "broken_god");

        var ack = ctl.HandleUi(Msg(T("broken_god", true)));
        Check("'broken' cheat geweigerd", !AckOk(ack) && !s.IsActive("broken_god"));
        ctl.OnHotkey(new HotkeyBinding("F7", g.Id, "broken_god", ""));
        Check("'broken' cheat geweigerd via sneltoets", !s.IsActive("broken_god"));
        ack = ctl.HandleUi(Msg(T("broken_god", true, force: true)));
        Check("'broken' cheat via 'toch proberen' (force) aan", AckOk(ack) && s.IsActive("broken_god"));
        ctl.HandleUi(Msg(T("broken_god", false)));
        Check("'broken' cheat weer uit", !s.IsActive("broken_god"));

        // local status: "Werkt" overrides game.json 'broken'
        ack = ctl.HandleUi(Msg(S("broken_god", "works")));
        Check("teststatus 'werkt' opgeslagen", AckOk(ack) && File.Exists(statusFile));
        var reloaded = new StatusStore(statusFile);
        var key = ctl.StatusKey(g);
        Check("teststatus bewaard per game + versie-vingerafdruk", reloaded.Get(g.Id, key, "broken_god") == "works" && key.StartsWith("fileVersion="), key);
        Check("lokale 'werkt' telt als bevestigd", ctl.EffectiveConfidence(g, bg) == "confirmed");
        ack = ctl.HandleUi(Msg(T("broken_god", true)));
        Check("cheat met lokale status 'werkt' zonder force aan", AckOk(ack) && s.IsActive("broken_god"));
        ctl.HandleUi(Msg(T("broken_god", false)));

        // local "Werkt niet" on a confirmed cheat
        ctl.HandleUi(Msg(S("battery", "broken")));
        ctl.HandleUi(Msg(T("battery", false)));
        ack = ctl.HandleUi(Msg(T("battery", true)));
        Check("lokale 'werkt niet' blokkeert een bevestigde cheat", !AckOk(ack) && !s.IsActive("battery"));

        // reset to game.json
        ctl.HandleUi(Msg(S("battery", null)));
        ctl.HandleUi(Msg(S("broken_god", null)));
        reloaded = new StatusStore(statusFile);
        Check("'Standaard (uit game.json)' wist de lokale status", reloaded.Get(g.Id, key, "battery") == null && reloaded.Get(g.Id, key, "broken_god") == null
            && ctl.EffectiveConfidence(g, bg) == "broken");
        ack = ctl.HandleUi(Msg(T("battery", true)));
        Check("bevestigde cheat na reset weer aan", AckOk(ack) && s.IsActive("battery"));

        ctl.HandleUi(Msg(S("broken_god", "untested")));
        ack = ctl.HandleUi(Msg("{\"type\":\"exportStatus\"}"));
        var export = Directory.GetFiles(Path.GetDirectoryName(statusFile)!, "teststatus-export-*.json").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        var exportText = export != null ? File.ReadAllText(export.FullName) : "";
        Check("teststatus geëxporteerd als JSON", AckOk(ack) && exportText.Contains("\"broken_god\"") && exportText.Contains("\"untested\"") && exportText.Contains("\"gameJson\": \"broken\""), export?.Name);
        ctl.HandleUi(Msg(S("broken_god", null)));
        try { File.Delete(statusFile); if (export != null) File.Delete(export.FullName); } catch { }
    }

    private static int CodeDiff(IProcessMemory mem, ModuleInfo mod, byte[] a, byte[] b)
    {
        int n = 0;
        foreach (var r in mem.Regions(mod.Base, mod.Base + mod.Size).Where(r => r.Executable))
        {
            long s = Math.Max(0, (long)(r.Start - mod.Base)), e = Math.Min(a.Length, (long)(r.Start + r.Size - mod.Base));
            for (long i = s; i < e; i++) if (a[i] != b[i]) n++;
        }
        return n;
    }

    private sealed class SingleCatalog : ICatalogSource
    {
        private readonly Dictionary<string, GameDef> _g;
        public SingleCatalog(params GameDef[] g) { _g = g.ToDictionary(x => x.Id); Entries = g.Select(x => CatalogBuilder.ToEntry(x, x.Id, 0)).ToList(); }
        public string Describe => "selftest";
        public IReadOnlyList<CatalogEntry> Entries { get; }
        public GameDef Load(string id) => _g[id];
    }
}
