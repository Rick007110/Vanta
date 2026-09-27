using System.Text.Json;
using Vanta.Core;
using Vanta.Core.Catalog;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public sealed class MemCatalog : ICatalogSource
{
    private readonly Dictionary<string, GameDef> _g;
    public MemCatalog(params GameDef[] games) { _g = games.ToDictionary(g => g.Id); Entries = games.Select(g => CatalogBuilder.ToEntry(g, g.Id, 0)).ToList(); }
    public string Describe => "mem";
    public IReadOnlyList<CatalogEntry> Entries { get; }
    public int Loads;
    public GameDef Load(string id) { Loads++; return _g[id]; }
}

public sealed class FakeProvider : IProcessProvider
{
    public readonly List<ProcInfo> Procs = new();
    public readonly Dictionary<int, FakeProcess> Mem = new();
    public Func<int, FakeProcess>? Factory;
    public string? FileVer = "0.8.5.651238";
    public int Opens;
    public IReadOnlyList<ProcInfo> List() => Procs.ToList();
    public IProcessMemory Open(int pid) { Opens++; if (!Mem.TryGetValue(pid, out var m)) Mem[pid] = m = Factory!(pid); return m; }
    public (string?, string?) FileVersion(string? path) => (FileVer, FileVer);
    public void Start(int pid, string name) => Procs.Add(new ProcInfo(pid, name));
    public void Kill(int pid) { Procs.RemoveAll(p => p.Pid == pid); if (Mem.TryGetValue(pid, out var m)) m.IsAlive = false; }
}

public class ControllerTests
{
    private readonly List<string> _sent = new();
    private readonly List<string> _urls = new();
    private DateTime _now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    private (TrainerController c, FakeProvider prov, GameDef g) Make(GameDef? extra = null)
    {
        var g = TestUtil.Tlc();
        var games = extra == null ? new[] { g } : new[] { g, extra };
        var prov = new FakeProvider { Factory = pid => { var (p, _, _) = TestUtil.TlcProcess(g); p.ProcessId = pid; return p; } };
        var c = new TrainerController(new MemCatalog(games), prov, new Settings { AttachDelaySec = 4 },
            o => _sent.Add(JsonSerializer.Serialize(o, VJson.Compact)), u => { _urls.Add(u); return Task.CompletedTask; }) { Now = () => _now };
        return (c, prov, g);
    }

    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private void Advance(double s) => _now = _now.AddSeconds(s);

    [Fact]
    public void Attaches_after_delay_toggles_and_reapplies_after_game_restart()
    {
        var (c, prov, g) = Make();
        Assert.Equal("notfound", c.StatusOf(g.Id));
        prov.Start(100, "VoyageSteam-Win64-Shipping.exe");
        c.Poll();
        Assert.Equal("attaching", c.StatusOf(g.Id));
        Assert.Equal(0, prov.Opens);
        Advance(5); c.Poll();
        Assert.Equal("attached", c.StatusOf(g.Id));
        Assert.True(c.SessionOf(g.Id)!.IsActive("xp_hook"));            // autoEnable value hooks

        var site = new AobPattern("F2 0F 10 00 F2 0F 5C C6 F2 0F 11 00 B0 01").FindAll(prov.Mem[100].Peek(TestUtil.ModBase, 0x100000))[0];
        var ack = JsonSerializer.Serialize(c.HandleUi(Msg($"{{\"type\":\"toggle\",\"reqId\":7,\"gameId\":\"{g.Id}\",\"id\":\"inf_battery\",\"enabled\":true}}")), VJson.Compact);
        Assert.Contains("\"ok\":true", ack);
        Assert.Equal(0x90, prov.Mem[100].Peek(TestUtil.ModBase + (ulong)site + 4, 1)[0]);

        // game exits -> abandon, status notfound; restart -> reattach and re-apply
        prov.Kill(100); c.Tick(); c.Poll();
        Assert.Equal("notfound", c.StatusOf(g.Id));
        prov.Start(200, "VoyageSteam-Win64-Shipping.exe");
        c.Poll(); Advance(5); c.Poll();
        Assert.Equal("attached", c.StatusOf(g.Id));
        Assert.True(c.SessionOf(g.Id)!.IsActive("inf_battery"));
        Assert.Equal(0x90, prov.Mem[200].Peek(TestUtil.ModBase + (ulong)site + 4, 1)[0]);
        Assert.Contains(_sent, s => s.Contains("opnieuw toegepast") || s.Contains("reapplied") || s.Contains("\"type\":\"log\""));

        c.Shutdown();
        Assert.Equal(0xF2, prov.Mem[200].Peek(TestUtil.ModBase + (ulong)site + 4, 1)[0]);
        Assert.Equal(0, prov.Mem[200].AllocatedCount);
    }

    [Fact]
    public void Failed_cheat_reports_dutch_error_with_hit_counts()
    {
        var (c, prov, g) = Make();
        prov.Factory = pid => { var p = new FakeProcess { ProcessId = pid }; p.AddModule(g.MainModule, TestUtil.ModBase, new byte[0x10000]); return p; };
        prov.Start(1, "VoyageSteam-Win64-Shipping.exe"); c.Poll(); Advance(5); c.Poll();
        Assert.Equal("attached", c.StatusOf(g.Id));
        var ack = JsonSerializer.Serialize(c.HandleUi(Msg($"{{\"type\":\"toggle\",\"gameId\":\"{g.Id}\",\"id\":\"inf_battery\",\"enabled\":true}}")), VJson.Compact);
        Assert.Contains("\"ok\":false", ack);
        Assert.Contains("geen unieke AOB gevonden (patroon 1: 0 treffer(s), patroon 2: 0 treffer(s))", ack);
        Assert.Contains(_sent, s => s.Contains("\"type\":\"state\"") && s.Contains("geen unieke AOB"));
    }

    [Fact]
    public void Launch_is_debounced_and_never_launches_a_running_game()
    {
        var (c, prov, g) = Make();
        var launch = Msg($"{{\"type\":\"launch\",\"gameId\":\"{g.Id}\"}}");
        c.HandleUi(launch); c.HandleUi(launch); c.HandleUi(launch);
        Assert.Equal(new[] { "steam://run/1783560" }, _urls);
        Assert.Equal("launching", c.StatusOf(g.Id));
        prov.Start(5, "VoyageSteam-Win64-Shipping.exe"); c.Poll();
        c.HandleUi(launch);
        Assert.Single(_urls);
        prov.Kill(5); c.Poll();
        Advance(1); c.HandleUi(launch);
        Assert.Equal(2, _urls.Count);
        Advance(61); c.Poll();                                             // launch timeout
        Assert.Equal("notfound", c.StatusOf(g.Id));
    }

    [Fact]
    public void Anti_cheat_or_online_only_games_are_refused()
    {
        var blocked = new GameDef { Id = "mp", Name = "Online Shooter", SteamAppId = 1, ProcessNames = { "shooter.exe" }, AntiCheat = true,
            Cheats = { new CheatDef { Id = "x", Name = "X", Impl = new ImplDef { Type = "aobPatch" } } } };
        var (c, prov, _) = Make(blocked);
        prov.Factory = _ => new FakeProcess();
        Assert.Equal("blocked", c.StatusOf("mp"));
        var ack = JsonSerializer.Serialize(c.HandleUi(Msg("{\"type\":\"launch\",\"gameId\":\"mp\"}")), VJson.Compact);
        Assert.Contains("\"ok\":false", ack);
        Assert.Empty(_urls);
        prov.Start(9, "shooter.exe"); c.Poll(); Advance(10); c.Poll();
        c.HandleUi(Msg("{\"type\":\"attach\",\"gameId\":\"mp\"}"));
        Assert.Equal(0, prov.Opens);                                        // never even opened
        Assert.Equal("blocked", c.StatusOf("mp"));
    }

    [Fact]
    public void Version_mismatch_waits_for_user_confirmation()
    {
        var (c, prov, g) = Make();
        g.SupportedVersions = new() { new VersionDef { Label = "0.8.5", FileVersion = "0.8.5.651238" } };
        prov.FileVer = "0.9.0.1";
        prov.Start(3, "VoyageSteam-Win64-Shipping.exe"); c.Poll(); Advance(5); c.Poll();
        Assert.Equal("wrongversion", c.StatusOf(g.Id));
        Assert.Contains(_sent, s => s.Contains("\"process\":\"wrongversion\"") && s.Contains("fileVersion=0.9.0.1"));
        c.HandleUi(Msg($"{{\"type\":\"attach\",\"gameId\":\"{g.Id}\"}}"));
        Assert.Equal("attached", c.StatusOf(g.Id));
        prov.FileVer = "0.8.5.651238";
        Assert.Equal("ok", VersionCheck.Check(g, prov.Mem[3], prov.Mem[3].GetModules()[0], ("0.8.5.651238", null)).state);
    }

    [Fact]
    public void Library_is_lazy_and_game_payload_hides_hidden_cheats()
    {
        var (c, _, g) = Make();
        var cat = new MemCatalog(g);
        var lib = JsonSerializer.Serialize(c.Library(), VJson.Compact);
        Assert.Contains("\"lazy\":true", lib);
        Assert.DoesNotContain("inf_battery", lib);
        var game = JsonSerializer.Serialize(c.UiGame(g.Id), VJson.Compact);
        Assert.Contains("inf_battery", game);
        Assert.DoesNotContain("\"xp_hook\"", game);
        Assert.Contains("\"hotkey\":\"F5\"", game);
    }

    [Fact]
    public void Hotkeys_follow_settings_and_drive_cheats()
    {
        var (c, prov, g) = Make();
        c.HandleUi(Msg($"{{\"type\":\"saveSettings\",\"settings\":{{\"gameId\":\"{g.Id}\",\"hotkeys\":{{\"inf_battery\":\"Ctrl+F1\",\"inf_ammo\":\"\"}}}}}}"));
        var b = c.HotkeyBindings();
        Assert.Contains(b, x => x.Combo == "Ctrl+F1" && x.CheatId == "inf_battery");
        Assert.DoesNotContain(b, x => x.CheatId == "inf_ammo");
        Assert.Contains(b, x => x.Combo == "Numpad+" && x.CheatId == "xp_value" && x.Kind == "inc");
        prov.Start(1, "VoyageSteam-Win64-Shipping.exe"); c.Poll(); Advance(5); c.Poll();
        c.OnHotkey(b.First(x => x.CheatId == "inf_battery"));
        Assert.True(c.SessionOf(g.Id)!.IsActive("inf_battery"));
        c.OnHotkey(b.First(x => x.CheatId == "inf_battery"));
        Assert.False(c.SessionOf(g.Id)!.IsActive("inf_battery"));
    }
}
