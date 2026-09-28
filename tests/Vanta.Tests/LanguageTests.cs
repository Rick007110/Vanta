using System.Text.Json;
using Vanta.Core;
using Vanta.Core.Catalog;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

/// <summary>English is the default language; Dutch is an explicit choice in Settings.</summary>
public class LanguageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-lang-" + Guid.NewGuid().ToString("N"));
    public LanguageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static GameDef Load(string id) => VJson.LoadGame(Path.Combine(TestUtil.RepoRoot, "games", id, "game.json"));
    private static JsonElement Msg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Fresh_install_is_english()
    {
        Assert.Equal("en", new Settings().Language);
        Assert.Equal("en", Settings.Load(Path.Combine(_dir, "missing.json")).Language);
    }

    [Fact]
    public void Old_settings_with_the_former_dutch_default_become_english()
    {
        var f = Path.Combine(_dir, "settings.json");
        File.WriteAllText(f, "{\"language\":\"nl\",\"autoAttach\":false}");
        var s = Settings.Load(f);
        Assert.Equal("en", s.Language);
        Assert.False(s.AutoAttach);                         // the rest is kept
    }

    [Fact]
    public void An_explicit_choice_is_kept_and_saving_in_settings_marks_it()
    {
        var f = Path.Combine(_dir, "settings.json");
        File.WriteAllText(f, "{\"language\":\"nl\",\"languageChosen\":true}");
        Assert.Equal("nl", Settings.Load(f).Language);

        var s = new Settings();
        var c = new TrainerController(new MemCatalog(Load("far-cry-6")), new FakeProvider(), s, _ => { });
        c.HandleUi(Msg("{\"type\":\"saveSettings\",\"settings\":{\"language\":\"nl\"}}"));
        Assert.Equal(("nl", true), (s.Language, s.LanguageChosen));
        s.Save(f);
        Assert.Equal("nl", Settings.Load(f).Language);
    }

    [Fact]
    public void Game_texts_are_english_by_default_and_dutch_when_chosen()
    {
        var g = Load("far-cry-6");
        string Ui(string lang) => JsonSerializer.Serialize(new TrainerController(new MemCatalog(g), new FakeProvider(),
            new Settings { Language = lang, LanguageChosen = true }, _ => { }).UiGame(g.Id), VJson.Compact);
        var en = JsonDocument.Parse(Ui("en")).RootElement.GetProperty("game");
        var nl = JsonDocument.Parse(Ui("nl")).RootElement.GetProperty("game");
        Assert.Equal("Solo campaign only. Do not use in co-op or online.", en.GetProperty("scope").GetString());
        Assert.Equal("Alleen voor de solo-campagne. Niet gebruiken in co-op of online.", nl.GetProperty("scope").GetString());
        Assert.StartsWith("Steam build 11359732", en.GetProperty("version").GetString());
        Assert.StartsWith("Steam-build 11359732", nl.GetProperty("version").GetString());
        Assert.StartsWith("Far Cry 6 has no anti-cheat", en.GetProperty("notes")[0].GetString());
        Assert.StartsWith("Far Cry 6 heeft geen anti-cheat", nl.GetProperty("notes")[0].GetString());
        JsonElement Cheat(JsonElement game, string id) => game.GetProperty("cheats").EnumerateArray().First(c => c.GetProperty("id").GetString() == id);
        Assert.Equal("Infinite Stamina", Cheat(en, "inf_stamina").GetProperty("name").GetString());
        Assert.Equal("Oneindig uithoudingsvermogen", Cheat(nl, "inf_stamina").GetProperty("name").GetString());
        Assert.Equal("Sprinting and climbing cost no stamina.", Cheat(en, "inf_stamina").GetProperty("description").GetString());
        Assert.Equal("Sprinten en klimmen kosten geen uithoudingsvermogen.", Cheat(nl, "inf_stamina").GetProperty("description").GetString());
        Assert.Equal("Not verified for this version.", Cheat(en, "inf_stamina").GetProperty("note").GetString());
        Assert.Equal("Niet geverifieerd voor deze versie.", Cheat(nl, "inf_stamina").GetProperty("note").GetString());
    }

    [Theory, InlineData("the-last-caretaker"), InlineData("far-cry-5"), InlineData("far-cry-6"), InlineData("mafia-3-de"), InlineData("windrose"), InlineData("phasmophobia"), InlineData("avatar-frontiers-of-pandora")]
    public void Every_visible_text_has_a_dutch_translation_or_is_the_same(string id)
    {
        var g = Load(id);
        var nl = g.I18n!["nl"];
        Assert.Equal(g.Notes!.Count, nl.Notes!.Count);
        Assert.Equal(g.Scope == null, nl.Scope == null);
        foreach (var c in g.Cheats.Where(c => !c.Hidden))
        {
            var t = c.I18n != null && c.I18n.TryGetValue("nl", out var x) ? x : null;
            if (c.Description != null && c.Description.Length > 0) Assert.True(t?.Description != null, $"{id}/{c.Id}: description nl");
            if (c.Hint != null) Assert.True(t?.Hint != null, $"{id}/{c.Id}: hint nl");
            if (c.Sub != null) Assert.True(t?.Sub != null, $"{id}/{c.Id}: sub nl");
        }
    }

    [Fact]
    public void Engine_messages_follow_the_language()
    {
        var g = Load("the-last-caretaker");
        new TrainerController(new MemCatalog(g), new FakeProvider(), new Settings(), _ => { });
        Assert.Equal("Not attached to the game.", Strings.Get("not.attached"));
        new TrainerController(new MemCatalog(g), new FakeProvider(), new Settings { Language = "nl", LanguageChosen = true }, _ => { });
        try { Assert.Equal("Niet gekoppeld aan de game.", Strings.Get("not.attached")); }
        finally { Strings.Lang = "en"; }
    }
}
