using System.Text.Json;
using Vanta.Core;
using Vanta.Core.Catalog;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class StatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-status-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _sent = new();
    private DateTime _now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string File_ => Path.Combine(_dir, "status.json");

    private (TrainerController c, FakeProvider prov, GameDef g) Make(Action<GameDef>? tweak = null)
    {
        var g = TestUtil.Tlc();
        tweak?.Invoke(g);
        var prov = new FakeProvider { Factory = pid => { var (p, _, _) = TestUtil.TlcProcess(g); p.ProcessId = pid; return p; } };
        var c = new TrainerController(new MemCatalog(g), prov, new Settings { AttachDelaySec = 4 },
            o => _sent.Add(JsonSerializer.Serialize(o, VJson.Compact)), status: new StatusStore(File_)) { Now = () => _now };
        return (c, prov, g);
    }
    private void Attach(TrainerController c, FakeProvider prov, GameDef g)
    {
        prov.Start(100, "VoyageSteam-Win64-Shipping.exe"); c.Poll(); _now = _now.AddSeconds(5); c.Poll();
        Assert.Equal("attached", c.StatusOf(g.Id));
    }
    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string S(object o) => JsonSerializer.Serialize(o, VJson.Compact);
    private string Toggle(TrainerController c, GameDef g, string id, bool en, bool force = false) =>
        S(c.HandleUi(Msg($"{{\"type\":\"toggle\",\"gameId\":\"{g.Id}\",\"id\":\"{id}\",\"enabled\":{(en ? "true" : "false")}{(force ? ",\"force\":true" : "")}}}")));
    private string SetStatus(TrainerController c, GameDef g, string id, string? st) =>
        S(c.HandleUi(Msg($"{{\"type\":\"setStatus\",\"gameId\":\"{g.Id}\",\"id\":\"{id}\",\"status\":{(st == null ? "null" : $"\"{st}\"")}}}")));

    [Fact]
    public void Schema_validator_and_loader_accept_broken()
    {
        var json = File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "tools", "dummy", "selftest.game.json"));
        Assert.DoesNotContain(GameValidator.ValidateJson(json), f => f.Level == "error");
        var bad = json.Replace("\"confidence\": \"broken\"", "\"confidence\": \"kapot\"");
        Assert.Contains(GameValidator.ValidateJson(bad), f => f.Level == "error");
        var g = VJson.LoadGame(Path.Combine(TestUtil.RepoRoot, "tools", "dummy", "selftest.game.json"));
        Assert.Equal("broken", g.Cheats.Single(c => c.Id == "broken_god").Confidence);

        // loader tolerance: case-insensitive, unknown -> untested
        var tmp = Path.Combine(_dir, "g.json"); Directory.CreateDirectory(_dir);
        File.WriteAllText(tmp, json.Replace("\"confidence\": \"broken\"", "\"confidence\": \"Broken\"").Replace("\"confidence\": \"confirmed\"", "\"confidence\": \"nonsense\""));
        var g2 = VJson.LoadGame(tmp);
        Assert.Equal("broken", g2.Cheats.Single(c => c.Id == "broken_god").Confidence);
        Assert.Equal("untested", g2.Cheats.Single(c => c.Id == "battery").Confidence);
    }

    [Fact]
    public void Validator_warns_about_autoEnable_or_required_broken_cheats()
    {
        var g = TestUtil.Tlc();
        var hook = g.Cheats.First(c => c.AutoEnable);
        hook.Confidence = "broken";
        var f = GameValidator.Semantic(g);
        Assert.Contains(f, x => x.Level == "warn" && x.Message.Contains("autoEnable"));
        Assert.Contains(f, x => x.Level == "warn" && x.Message.Contains("require this 'broken'"));
    }

    [Fact]
    public void Store_persists_per_game_version_and_cheat()
    {
        var g = TestUtil.Tlc();
        var s = new StatusStore(File_);
        var k1 = StatusStore.VersionKey("fileVersion=1.0.0.1, peTimestamp=123, moduleSize=4096, fileSize=99", null);
        var k2 = StatusStore.VersionKey("fileVersion=1.0.0.2, peTimestamp=456, moduleSize=4096", null);
        Assert.Equal("fileVersion=1.0.0.1, peTimestamp=123, moduleSize=4096", k1);
        s.Set(g, k1, "inf_battery", "works", "0.8.5");
        s.Set(g, k2, "inf_battery", "broken");
        s.Set(g, k1, "god", "untested");
        s.Save();
        Assert.Throws<ArgumentException>(() => s.Set(g, k1, "god", "kapot"));

        var r = new StatusStore(File_);
        Assert.Equal("works", r.Get(g.Id, k1, "inf_battery"));
        Assert.Equal("broken", r.Get(g.Id, k2, "inf_battery"));
        Assert.Equal("untested", r.Get(g.Id, k1, "god"));
        Assert.Null(r.Get(g.Id, k2, "god"));
        Assert.Null(r.Get("other", k1, "inf_battery"));
        Assert.Equal("0.8.5", r.Data.Games[g.Id].Versions[k1].Label);

        r.Set(g, k2, "inf_battery", null);
        r.Save();
        r = new StatusStore(File_);
        Assert.Null(r.Get(g.Id, k2, "inf_battery"));
        Assert.False(r.Data.Games[g.Id].Versions.ContainsKey(k2));      // empty version entries are removed

        Assert.Equal("label:0.8.5", StatusStore.VersionKey(null, "0.8.5"));
        Assert.Equal("confirmed", StatusStore.Effective("broken", "works"));
        Assert.Equal("broken", StatusStore.Effective("confirmed", "broken"));
        Assert.Equal("untested", StatusStore.Effective("confirmed", "untested"));
        Assert.Equal("experimental", StatusStore.Effective("experimental", null));
    }

    [Fact]
    public void Corrupt_status_file_starts_empty_and_keeps_a_backup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");
        var s = new StatusStore(File_);
        Assert.Empty(s.Data.Games);
        Assert.True(File.Exists(File_ + ".bad"));
    }

    [Fact]
    public void Broken_cheat_is_refused_unless_forced_and_greyed_in_payload()
    {
        var (c, prov, g) = Make(x => x.Cheats.First(ch => ch.Id == "inf_battery").Confidence = "broken");
        var ui = S(c.UiGame(g.Id));
        Assert.Contains("\"id\":\"inf_battery\"", ui);
        Assert.Contains("Does not work in this version.", ui);
        Assert.Contains("\"confidence\":\"broken\",\"baseConfidence\":\"broken\"", ui);
        Assert.DoesNotContain("\"localStatus\":\"", ui);

        Attach(c, prov, g);
        var ack = Toggle(c, g, "inf_battery", true);
        Assert.Contains("\"ok\":false", ack);
        Assert.Contains("does not work in this version", ack);
        Assert.False(c.SessionOf(g.Id)!.IsActive("inf_battery"));

        // hotkey is refused too
        var hk = c.HotkeyBindings().FirstOrDefault(b => b.CheatId == "inf_battery");
        if (hk != null) { c.OnHotkey(hk); Assert.False(c.SessionOf(g.Id)!.IsActive("inf_battery")); }

        ack = Toggle(c, g, "inf_battery", true, force: true);
        Assert.Contains("\"ok\":true", ack);
        Assert.True(c.SessionOf(g.Id)!.IsActive("inf_battery"));
        if (hk != null) { c.OnHotkey(hk); Assert.False(c.SessionOf(g.Id)!.IsActive("inf_battery")); }   // turning off always works
    }

    [Fact]
    public void Local_status_overrides_confidence_and_is_saved_under_the_attached_fingerprint()
    {
        var (c, prov, g) = Make();
        var bat = g.Cheats.First(ch => ch.Id == "inf_battery");
        Assert.Equal("label:" + g.SupportedVersions[0].Label, c.StatusKey(g));   // not attached yet

        Attach(c, prov, g);
        var key = c.StatusKey(g);
        Assert.StartsWith("fileVersion=0.8.5.651238", key);

        Assert.Contains("\"ok\":true", SetStatus(c, g, "inf_battery", "broken"));
        Assert.Equal("broken", c.EffectiveConfidence(g, bat));
        Assert.Contains(_sent, s => s.Contains("\"type\":\"game\"") && s.Contains("\"confidence\":\"broken\",\"baseConfidence\":\"" + bat.Confidence + "\",\"localStatus\":\"broken\""));
        Assert.Contains("\"ok\":false", Toggle(c, g, "inf_battery", true));

        Assert.Contains("\"ok\":true", SetStatus(c, g, "inf_battery", "works"));
        Assert.Equal("confirmed", c.EffectiveConfidence(g, bat));
        Assert.Contains("\"ok\":true", Toggle(c, g, "inf_battery", true));

        var reloaded = new StatusStore(File_);
        Assert.Equal("works", reloaded.Get(g.Id, key, "inf_battery"));
        Assert.Equal(key, reloaded.Data.Games[g.Id].LastVersion);

        // a new controller (app restart, game not running) uses the last seen version
        var c2 = new TrainerController(new MemCatalog(g), prov, new Settings(), _ => { }, status: new StatusStore(File_));
        Assert.Equal(key, c2.StatusKey(g));
        Assert.Equal("confirmed", c2.EffectiveConfidence(g, bat));

        Assert.Contains("\"ok\":true", SetStatus(c, g, "inf_battery", null));
        Assert.Equal(bat.Confidence, c.EffectiveConfidence(g, bat));
        Assert.Null(new StatusStore(File_).Get(g.Id, key, "inf_battery"));

        Assert.Contains("\"ok\":false", SetStatus(c, g, "inf_battery", "kapot"));
        Assert.Contains("\"ok\":false", SetStatus(c, g, "does_not_exist", "works"));
    }

    [Fact]
    public void Export_writes_json_file_and_sends_it_to_the_ui()
    {
        var (c, prov, g) = Make();
        Attach(c, prov, g);
        SetStatus(c, g, "inf_battery", "broken");
        SetStatus(c, g, "no_weight", "works");
        _sent.Clear();
        Assert.Contains("\"ok\":true", S(c.HandleUi(Msg("{\"type\":\"exportStatus\"}"))));
        var msg = _sent.Single(s => s.Contains("\"type\":\"statusExport\""));
        var el = JsonDocument.Parse(msg).RootElement;
        var path = el.GetProperty("path").GetString()!;
        Assert.True(File.Exists(path));
        Assert.StartsWith(_dir, path);
        Assert.Matches(@"teststatus-export-\d{8}-\d{6}\.json$", path);
        var doc = JsonDocument.Parse(el.GetProperty("json").GetString()!).RootElement;
        Assert.Equal(Branding.Version, doc.GetProperty("vanta").GetString());
        var ver = doc.GetProperty("games").GetProperty(g.Id).GetProperty("versions").EnumerateObject().Single();
        Assert.StartsWith("fileVersion=", ver.Name);
        var cheats = ver.Value.GetProperty("cheats");
        Assert.Equal("broken", cheats.GetProperty("inf_battery").GetProperty("status").GetString());
        Assert.Equal(g.Cheats.First(x => x.Id == "inf_battery").Confidence, cheats.GetProperty("inf_battery").GetProperty("gameJson").GetString());
        Assert.Equal("works", cheats.GetProperty("no_weight").GetProperty("status").GetString());
        Assert.Equal(File.ReadAllText(path), el.GetProperty("json").GetString());
    }
}
