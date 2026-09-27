using System.Diagnostics;
using System.Text.Json.Nodes;
using Vanta.Core;
using Vanta.Core.Catalog;
using Vanta.Core.Import;
using Xunit;
using Xunit.Abstractions;

namespace Vanta.Tests;

public class ValidatorTests
{
    private static string TlcText => File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "games", "the-last-caretaker", "game.json"));
    private static List<Finding> Errors(string json) => GameValidator.ValidateJson(json).Where(f => f.Level == "error").ToList();
    private static string Mutate(Action<JsonNode> m) { var n = JsonNode.Parse(TlcText)!; m(n); return n.ToJsonString(); }

    [Fact] public void Tlc_definition_is_valid() => Assert.Empty(Errors(TlcText));

    [Fact]
    public void Every_shipped_game_is_valid()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(TestUtil.RepoRoot, "games"), "game.json", SearchOption.AllDirectories))
            Assert.Empty(GameValidator.ValidateFile(f).Where(x => x.Level == "error"));
    }

    [Theory]
    [InlineData("missing antiCheat")]
    [InlineData("bad aob")]
    [InlineData("bad asm")]
    [InlineData("duplicate id")]
    [InlineData("unknown require")]
    [InlineData("short overwrite")]
    [InlineData("bad hotkey")]
    [InlineData("unknown site")]
    [InlineData("unexported symbol")]
    [InlineData("extra property")]
    public void Broken_definitions_are_rejected(string kind)
    {
        var json = Mutate(n =>
        {
            var cheats = n["cheats"]!.AsArray();
            JsonNode C(string id) => cheats.First(c => (string)c!["id"]! == id)!;
            switch (kind)
            {
                case "missing antiCheat": n.AsObject().Remove("antiCheat"); break;
                case "bad aob": C("inf_battery")["impl"]!["sites"]!["drain"]!["patterns"]![0]!["aob"] = "F2 0G"; break;
                case "bad asm": C("no_weight")["impl"]!["asm"]![1] = "  frobnicate xmm0"; break;
                case "duplicate id": C("no_weight")["id"] = "inf_battery"; break;
                case "unknown require": C("inf_petrol")["requires"] = new JsonArray("nope"); break;
                case "short overwrite": C("no_weight")["impl"]!["sites"]!["w"]!["overwrite"] = 3; break;
                case "bad hotkey": C("inf_battery")["hotkey"] = "Ctrl+Banana"; break;
                case "unknown site": C("inf_battery")["impl"]!["patches"]![0]!["site"] = "zzz"; break;
                case "unexported symbol": C("xp_value")["impl"]!["base"] = "sym:not_there"; break;
                case "extra property": n["foo"] = 1; break;
            }
        });
        Assert.NotEmpty(Errors(json));
    }

    [Fact]
    public void Invalid_json_is_an_error_not_an_exception() => Assert.NotEmpty(Errors("{ not json"));

    [Fact]
    public void Blocked_games_are_flagged()
    {
        var f = GameValidator.ValidateJson(Mutate(n => n["antiCheat"] = true));
        Assert.Contains(f, x => x.Path == "antiCheat");
    }
}

public class ImporterTests
{
    private readonly ITestOutputHelper _out;
    public ImporterTests(ITestOutputHelper o) => _out = o;
    // Third-party tables are not part of the repo: set VANTA_TABLES to a folder with them to run these tests.
    private static string Tlc(string f) => Path.Combine(Environment.GetEnvironmentVariable("VANTA_TABLES") ?? Path.Combine(TestUtil.RepoRoot, "..", "tables"), f);

    public static IEnumerable<object[]> Tables() => new[]
    {
        new object[] { "jules146_v1.4b.CT", 20 }, new object[] { "LIOBOSS_v1.2.CT", 20 },
        new object[] { "LIOBOSS_74505.CT", 20 }, new object[] { "groovedexter_v0.8.0.6.ct", 15 },
    };

    [SkippableTheory, MemberData(nameof(Tables))]
    public void Community_tables_convert_to_valid_definitions(string file, int minConverted)
    {
        Skip.IfNot(File.Exists(Tlc(file)), "community table not present");
        var r = CtImporter.Import(File.ReadAllText(Tlc(file)), "The Last Caretaker", new ImportOptions { Id = "tlc-import", Process = "VoyageSteam-Win64-Shipping.exe", SteamAppId = 1783560 });
        _out.WriteLine($"{file}: {r.Converted}/{r.Items.Count}");
        Assert.True(r.Converted >= minConverted, $"{r.Converted} converted");
        var json = System.Text.Json.JsonSerializer.Serialize(r.Game, VJson.Options);
        Assert.Empty(GameValidator.ValidateJson(json).Where(f => f.Level == "error"));
        Assert.Contains(r.Items, i => i.status != "ok");      // things that can't be converted are listed
        Assert.Contains("ok", r.Report(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Lua_wrapped_scripts_are_flagged_not_converted()
    {
        var f = Environment.GetEnvironmentVariable("VANTA_LUA_TABLE") ?? "";
        Skip.IfNot(File.Exists(f), "table not present");
        var r = CtImporter.Import(File.ReadAllText(f), "The Last Caretaker");
        Assert.Contains(r.Items, i => i.detail.Contains("lua", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Converts_a_minimal_aa_injection_and_pointer()
    {
        const string ct = """
<?xml version="1.0" encoding="utf-8"?>
<CheatTable CheatEngineTableVersion="45">
  <CheatEntries>
    <CheatEntry><ID>1</ID><Description>"God mode"</Description><VariableType>Auto Assembler Script</VariableType>
      <Hotkeys><Hotkey><Action>Toggle Activation</Action><Keys><Key>17</Key><Key>112</Key></Keys></Hotkey></Hotkeys>
      <AssemblerScript>[ENABLE]
aobscanmodule(hp,Game.exe,F3 0F 11 43 10 48 8B 5C 24)
alloc(newmem,$1000,hp)
label(ret)
label(ptr)
registersymbol(ptr)
newmem:
  mov [ptr],rbx
  movss xmm0,[rbx+10]
  movss [rbx+10],xmm0
  jmp ret
ptr:
  dq 0
hp:
  jmp newmem
ret:
registersymbol(hp)
[DISABLE]
hp:
  db F3 0F 11 43 10
unregistersymbol(*)
dealloc(*)
</AssemblerScript>
    </CheatEntry>
    <CheatEntry><ID>2</ID><Description>"Health"</Description><VariableType>Float</VariableType><Address>ptr</Address><Offsets><Offset>10</Offset></Offsets></CheatEntry>
    <CheatEntry><ID>3</ID><Description>"Money"</Description><VariableType>4 Bytes</VariableType><Address>"Game.exe"+01234560</Address><Offsets><Offset>28</Offset><Offset>10</Offset></Offsets></CheatEntry>
    <CheatEntry><ID>4</ID><Description>"Lua thing"</Description><VariableType>Auto Assembler Script</VariableType><AssemblerScript>{$lua}
print(1)</AssemblerScript></CheatEntry>
  </CheatEntries>
</CheatTable>
""";
        var r = CtImporter.Import(ct, "Game", new ImportOptions { Process = "Game.exe" });
        Assert.Equal(3, r.Converted);
        var god = r.Game.Cheats.First(c => c.Name == "God mode");
        Assert.Equal("aobInject", god.Impl.Type);
        Assert.Equal(5, god.Impl.Sites!.Values.First().Overwrite);
        Assert.Contains("ptr", god.Impl.Exports!);
        Assert.Equal("Ctrl+F1", god.Hotkey);
        var hp = r.Game.Cheats.First(c => c.Name == "Health");
        Assert.Equal("float", hp.Impl.ValueType);
        Assert.Contains(god.Id, hp.Requires!);
        var money = r.Game.Cheats.First(c => c.Name == "Money");
        Assert.Equal(new long[] { 0x10, 0x28 }, money.Impl.Offsets);            // CE lists offsets last-first
        Assert.Contains(r.Items, i => i.entry == "Lua thing" && i.status != "ok");
        var json = System.Text.Json.JsonSerializer.Serialize(r.Game, VJson.Options);
        Assert.Empty(GameValidator.ValidateJson(json).Where(f => f.Level == "error"));
    }
}

public class CatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-cat-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;
    public CatalogTests(ITestOutputHelper o) { _out = o; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void MakeGames(int n)
    {
        var tlc = File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "games", "the-last-caretaker", "game.json"));
        for (int i = 0; i < n; i++)
        {
            var node = JsonNode.Parse(tlc)!;
            node["id"] = $"game-{i:D4}"; node["name"] = $"Test Game {i:D4}"; node["steamAppId"] = 100000 + i;
            node["processNames"] = new JsonArray($"Game{i}.exe"); node["module"] = $"Game{i}.exe";
            node["antiCheat"] = i % 100 == 0;
            Directory.CreateDirectory(Path.Combine(_dir, $"game-{i:D4}"));
            File.WriteAllText(Path.Combine(_dir, $"game-{i:D4}", "game.json"), node.ToJsonString());
        }
    }

    [Fact]
    public void Index_for_1200_games_is_built_once_then_loads_fast_and_lazily()
    {
        MakeGames(1200);
        var sw = Stopwatch.StartNew();
        var first = new LocalCatalog(_dir);
        _out.WriteLine($"build 1200: {sw.ElapsedMilliseconds} ms");
        Assert.True(first.IndexRebuilt);
        Assert.Equal(1200, first.Entries.Count);
        Assert.True(File.Exists(Path.Combine(_dir, CatalogBuilder.IndexFile)));

        sw.Restart();
        var second = new LocalCatalog(_dir);
        var ms = sw.ElapsedMilliseconds;
        _out.WriteLine($"load index 1200: {ms} ms");
        Assert.False(second.IndexRebuilt);
        Assert.True(ms < 1500, $"index load took {ms} ms");
        Assert.Equal(12, second.Entries.Count(e => e.AntiCheat));
        Assert.Equal(16, second.Entries[0].CheatCount);            // visible cheats only

        var g = second.Load("game-0042");
        Assert.Equal("Test Game 0042", g.Name);
        Assert.Same(g, second.Load("game-0042"));                 // cached
        Assert.Throws<KeyNotFoundException>(() => second.Load("nope"));
    }

    [Fact]
    public void Stale_index_is_rebuilt_when_games_are_added_or_changed()
    {
        MakeGames(3);
        var a = new LocalCatalog(_dir);
        Assert.Equal(3, a.Entries.Count);
        Directory.CreateDirectory(Path.Combine(_dir, "zz-new"));
        var tlc = File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "games", "the-last-caretaker", "game.json"));
        File.WriteAllText(Path.Combine(_dir, "zz-new", "game.json"), tlc);
        var b = new LocalCatalog(_dir);
        Assert.True(b.IndexRebuilt);
        Assert.Equal(4, b.Entries.Count);
        // modify a game -> stale
        var f = Path.Combine(_dir, "game-0001", "game.json");
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(5));
        Assert.True(new LocalCatalog(_dir).IndexRebuilt);
    }

    [Fact]
    public void Broken_game_files_are_reported_but_do_not_break_the_catalog()
    {
        MakeGames(2);
        Directory.CreateDirectory(Path.Combine(_dir, "broken"));
        File.WriteAllText(Path.Combine(_dir, "broken", "game.json"), "{ nope");
        var c = new LocalCatalog(_dir);
        Assert.Equal(2, c.Entries.Count);
        Assert.NotEmpty(c.Problems);
    }
}
