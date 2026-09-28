using Vanta.Core;
using Vanta.Core.Catalog;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class FarCryCatalogTests
{
    private static GameDef Load(string id) => VJson.LoadGame(Path.Combine(TestUtil.RepoRoot, "games", id, "game.json"));

    [Theory, InlineData("far-cry-5"), InlineData("far-cry-6")]
    public void Valid_without_warnings(string id)
    {
        var f = GameValidator.ValidateFile(Path.Combine(TestUtil.RepoRoot, "games", id, "game.json"));
        Assert.Empty(f.Where(x => x.Level is "error" or "warn" or "warning"));
    }

    [Theory, InlineData("far-cry-5", 552520L, "FarCry5.exe", "FC_m64.dll"), InlineData("far-cry-6", 2369390L, "FarCry6.exe", "FC_m64d3d12.dll")]
    public void Store_and_module_metadata(string id, long steam, string exe, string module)
    {
        var g = Load(id);
        Assert.Equal(steam, g.Stores!.Steam!.AppId);
        Assert.NotEmpty(g.Stores.Ubisoft!.Ids);
        Assert.Equal(exe, g.ProcessNames[0]);
        Assert.Equal(module, g.MainModule);
        Assert.False(g.Blocked);
        Assert.Contains("Solo campaign", g.Scope!);
        Assert.Contains("solo-campagne", g.I18n!["nl"].Scope!);
        Assert.StartsWith("bin/", g.LaunchExe);
    }

    [Fact]
    public void Fc5_refuses_old_eac_installs()
    {
        var g = Load("far-cry-5");
        Assert.Contains("bin/EasyAntiCheat/EasyAntiCheat_x64.dll", g.AntiCheatFiles!);
        Assert.Contains("EasyAntiCheat_x64.dll", g.AntiCheatModules!);
    }

    [Theory, InlineData("far-cry-5"), InlineData("far-cry-6")]
    public void Every_cheat_is_untested_or_experimental_without_author_names(string id)
    {
        var g = Load(id);
        Assert.Null(g.Author); Assert.Null(g.Source);
        foreach (var c in g.Cheats)
        {
            Assert.NotEqual("confirmed", c.Confidence);
            Assert.False(string.IsNullOrWhiteSpace(c.ConfidenceNote));
            Assert.DoesNotContain("Bron:", c.ConfidenceNote);
        }
    }

    [Theory, InlineData("far-cry-5"), InlineData("far-cry-6")]
    public void Hotkeys_unique(string id)
    {
        var hk = Load(id).Cheats.Where(c => c.Hotkey != null).Select(c => c.Hotkey!.ToLowerInvariant()).ToList();
        Assert.Equal(hk.Count, hk.Distinct().Count());
    }

    [Fact]
    public void Fc6_site_relative_calls_replace_fixed_rvas()
    {
        var text = File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "games", "far-cry-6", "game.json"));
        Assert.DoesNotContain("+228E570", text);
        Assert.DoesNotContain("+5C1D40", text);
        // "call aobxp-265C4" == original "E8 34 9A FD FF" located at aobxp+3
        var r = MiniAssembler.Assemble(new[] { "call aobxp-265C4" }, 0x180000003, new Dictionary<string, ulong> { ["aobxp"] = 0x180000000 });
        Assert.Equal("E8 34 9A FD FF", AobScanner.ToHex(r.Code));
        var o = MiniAssembler.Assemble(new[] { "call site-291CAA4" }, 0x180000003, new Dictionary<string, ulong> { ["site"] = 0x180000000 });
        Assert.Equal("E8 54 35 6E FD", AobScanner.ToHex(o.Code));
    }
}

public class UiPayloadPrivacyTests
{
    [Fact]
    public void Game_payload_has_no_author_source_or_internal_notes()
    {
        var g = VJson.LoadGame(Path.Combine(TestUtil.RepoRoot, "games", "the-last-caretaker", "game.json"));
        g.Author = "Iemand"; g.Source = "tabel.CT";
        var c = new TrainerController(new MemCatalog(g), new FakeProvider(), new Settings(), _ => { });
        var json = System.Text.Json.JsonSerializer.Serialize(c.UiGame(g.Id), VJson.Compact);
        Assert.DoesNotContain("\"author\"", json);
        Assert.DoesNotContain("\"source\"", json);
        Assert.DoesNotContain("tableVersion", json);
        Assert.DoesNotContain("Iemand", json);
        Assert.DoesNotContain("openbaar Cheat Engine-script", json);          // internal confidenceNote stays out
        Assert.Contains("Not verified for this version.", json);          // neutral user-facing warning
    }
}
