using System.Text.Json;
using Vanta.Core;
using Vanta.Core.Engine;
using Vanta.Core.Stores;
using Xunit;

namespace Vanta.Tests;

public class StoreControllerTests
{
    private readonly List<string> _sent = new();
    private readonly List<string> _urls = new();
    private DateTime _now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    private static GameDef Game()
    {
        var g = TestUtil.Tlc();
        g.Id = "store-game"; g.SteamAppId = 2369390; g.LaunchExe = "bin/Game.exe";
        g.ProcessNames = new() { "VoyageSteam-Win64-Shipping.exe" };
        g.Stores = new StoresDef { Steam = new() { AppId = 2369390 }, Ubisoft = new() { Ids = { 5266 } } };
        g.AntiCheatFiles = new() { "bin/EasyAntiCheat/EasyAntiCheat_x64.dll" };
        g.AntiCheatModules = new() { "EasyAntiCheat_x64.dll" };
        return g;
    }

    private static FakeStoreEnv Env(bool ubisoft = true)
    {
        var env = new FakeStoreEnv()
            .Key(@"HKCU\Software\Valve\Steam", ("SteamPath", @"C:\Steam"))
            .File(@"C:\Steam\steamapps\appmanifest_2369390.acf", "\"AppState\" { \"StateFlags\" \"4\" \"installdir\" \"G\" \"SizeOnDisk\" \"0\" \"buildid\" \"0\" }");
        if (ubisoft) env.Key(UbisoftProvider.InstallsKey + @"\5266", ("InstallDir", "D:/Games/G/")).File(@"D:\Games\G\bin\Game.exe");
        return env;
    }

    private (TrainerController c, FakeProvider prov, GameDef g) Make(FakeStoreEnv env, Func<string, Task>? open = null, Action<FakeProcess>? tweak = null)
    {
        var g = Game();
        var prov = new FakeProvider { Factory = pid => { var (p, _, _) = TestUtil.TlcProcess(g); p.ProcessId = pid; tweak?.Invoke(p); return p; } };
        var c = new TrainerController(new MemCatalog(g), prov, new Settings { AttachDelaySec = 0 },
            o => _sent.Add(JsonSerializer.Serialize(o, VJson.Compact)), open ?? (u => { _urls.Add(u); return Task.CompletedTask; }),
            null, new StoreDetector(env)) { Now = () => _now };
        return (c, prov, g);
    }
    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Launch_uses_ubisoft_when_steam_only_owns_it()
    {
        var (c, _, g) = Make(Env());
        var ack = JsonSerializer.Serialize(c.HandleUi(Msg($"{{\"type\":\"launch\",\"reqId\":1,\"gameId\":\"{g.Id}\"}}")), VJson.Compact);
        Assert.Contains("\"ok\":true", ack);
        Assert.Equal(new[] { "uplay://launch/5266/0" }, _urls);
        Assert.Contains(_sent, s => s.Contains("Ubisoft Connect"));
        // debounce: second click does nothing
        c.HandleUi(Msg($"{{\"type\":\"launch\",\"reqId\":2,\"gameId\":\"{g.Id}\"}}"));
        Assert.Single(_urls);
    }

    [Fact]
    public void Ui_game_payload_contains_detected_store()
    {
        var (c, _, g) = Make(Env());
        var json = JsonSerializer.Serialize(c.UiGame(g.Id), VJson.Compact);
        Assert.Contains("\"storeName\":\"Ubisoft Connect\"", json);
        Assert.Contains("SizeOnDisk 0", json);
    }

    [Fact]
    public void Launch_falls_back_to_direct_exe_when_uri_fails()
    {
        var (c, _, g) = Make(Env(), u => { _urls.Add(u); if (u.StartsWith("uplay:")) throw new InvalidOperationException("geen protocol"); return Task.CompletedTask; });
        c.HandleUi(Msg($"{{\"type\":\"launch\",\"reqId\":1,\"gameId\":\"{g.Id}\"}}"));
        Assert.Equal(new[] { "uplay://launch/5266/0", @"D:\Games\G\bin\Game.exe" }, _urls);
    }

    [Fact]
    public void No_install_and_steam_stub_launches_steam()
    {
        var (c, _, g) = Make(Env(ubisoft: false));
        c.HandleUi(Msg($"{{\"type\":\"launch\",\"reqId\":1,\"gameId\":\"{g.Id}\"}}"));
        Assert.Equal(new[] { "steam://run/2369390" }, _urls);
    }

    [Fact]
    public void Launch_refused_when_anticheat_files_present()
    {
        var env = Env().File(@"D:\Games\G\bin\EasyAntiCheat\EasyAntiCheat_x64.dll");
        var (c, _, g) = Make(env);
        var ack = JsonSerializer.Serialize(c.HandleUi(Msg($"{{\"type\":\"launch\",\"reqId\":1,\"gameId\":\"{g.Id}\"}}")), VJson.Compact);
        Assert.Contains("\"ok\":false", ack);
        Assert.Contains("Anti-cheat", ack);
        Assert.Empty(_urls);
    }

    [Fact]
    public void Attach_refused_when_anticheat_module_loaded()
    {
        var (c, prov, g) = Make(Env(), tweak: p => p.AddModule("EasyAntiCheat_x64.dll", 0x7FF0_0000_0000, new byte[0x1000]));
        prov.Start(10, "VoyageSteam-Win64-Shipping.exe");
        c.Poll(); _now = _now.AddSeconds(1); c.Poll();
        Assert.Equal("error", c.StatusOf(g.Id));
        Assert.Null(c.SessionOf(g.Id));
        Assert.Contains(_sent, s => s.Contains("Anti-cheat running"));
    }

    [Fact]
    public void Attach_refused_when_anticheat_files_next_to_running_exe()
    {
        // FakeProcess module paths are C:\Fake\<name>; with launchExe "bin/Game.exe" the root is C:\
        var env = Env().File(@"C:\bin\EasyAntiCheat\EasyAntiCheat_x64.dll");
        var (c, prov, g) = Make(env);
        prov.Start(10, "VoyageSteam-Win64-Shipping.exe");
        c.Poll(); _now = _now.AddSeconds(1); c.Poll();
        Assert.Equal("error", c.StatusOf(g.Id));
        Assert.Contains(_sent, s => s.Contains("Anti-cheat found"));
    }

    [Fact]
    public void Attach_ok_without_anticheat()
    {
        var (c, prov, g) = Make(Env());
        prov.Start(10, "VoyageSteam-Win64-Shipping.exe");
        c.Poll(); _now = _now.AddSeconds(1); c.Poll();
        Assert.Equal("attached", c.StatusOf(g.Id));
        c.Shutdown();
    }
}

public class VerifierTests
{
    private static string Dummy => Path.Combine(TestUtil.RepoRoot, "tools", "dummy", "vanta_dummy.exe");
    private static GameDef DummyGame() => VJson.LoadGame(Path.Combine(TestUtil.RepoRoot, "tools", "dummy", "selftest.game.json"));

    [Fact]
    public void PeFileMemory_maps_sections_at_rvas()
    {
        using var m = new PeFileMemory();
        var mod = m.Add(Dummy);
        Assert.Equal(0x1_4000_0000UL, mod.Base);
        var pe = m.ReadPeInfo(mod);
        Assert.NotNull(pe);
        Assert.Equal(mod.Size, pe!.Value.sizeOfImage);
        Assert.Contains(m.Regions(mod.Base, mod.Base + mod.Size), r => r.Executable);
        Assert.False(m.Write(mod.Base, new byte[] { 1 }));
        Assert.Equal(0UL, m.Alloc(0x1000, mod.Base));
    }

    [Fact]
    public void Verifier_finds_all_dummy_sites_unique()
    {
        var g = DummyGame();
        using var m = new PeFileMemory();
        m.Add(Dummy, g.MainModule);
        var (lines, ok) = Verifier.Report(g, m, new Dictionary<string, string> { [g.MainModule] = Dummy }, null);
        Assert.True(ok, string.Join("\n", lines));
        Assert.Contains(lines, l => l.StartsWith("RESULT: 5/5"));
        Assert.Contains(lines, l => l.Contains("headSha256="));
    }

    [Fact]
    public void Verifier_reports_missing_and_duplicate_patterns()
    {
        var g = DummyGame();
        var c = g.Cheats.First(x => x.Impl.Sites != null);
        var site = c.Impl.Sites!.First().Value;
        site.Patterns = new() { new PatternDef { Aob = "DE AD BE EF 13 37 DE AD" }, new PatternDef { Aob = "00 00 00 00" } };
        using var m = new PeFileMemory();
        m.Add(Dummy, g.MainModule);
        var (lines, ok) = Verifier.Report(g, m, null, null);
        Assert.False(ok);
        var line = lines.First(l => l.StartsWith("[FAIL] " + c.Id));
        Assert.Contains("pattern 1: 0 hit(s)", line);
        Assert.Matches(@"pattern 2: \d+ hit\(s\)", line);
    }

    [Fact]
    public void Locate_modules_in_folder_tree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vanta-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.Copy(Dummy, Path.Combine(dir, "bin", "vanta_dummy.exe"));
        try
        {
            var g = DummyGame();
            var files = Verifier.LocateModules(g, dir);
            Assert.True(files.ContainsKey(g.MainModule));
        }
        finally { Directory.Delete(dir, true); }
    }
}
