using Vanta.Core;
using Vanta.Core.Engine;
using Vanta.Core.Stores;
using Xunit;

namespace Vanta.Tests;

public class StoreTests
{
    private static string Fx(string name) => File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "tests", "Vanta.Tests", "Fixtures", "stores", name));

    private static GameDef Fc5() => new()
    {
        Id = "far-cry-5", Name = "Far Cry 5", SteamAppId = 552520, ProcessNames = { "FarCry5.exe" }, LaunchExe = "bin/FarCry5.exe",
        Stores = new StoresDef { Steam = new() { AppId = 552520 }, Ubisoft = new() { Ids = { 856 } } },
    };
    private static GameDef Fc6() => new()
    {
        Id = "far-cry-6", Name = "Far Cry 6", SteamAppId = 2369390, ProcessNames = { "FarCry6.exe" }, LaunchExe = "bin/FarCry6.exe",
        Stores = new StoresDef { Steam = new() { AppId = 2369390 }, Ubisoft = new() { Ids = { 5266, 920 } }, Xbox = new() { PackageFamilyName = "Ubisoft.FarCry6_abcd1234" } },
    };

    private static FakeStoreEnv SteamEnv() => new FakeStoreEnv()
        .Key(@"HKCU\Software\Valve\Steam", ("SteamPath", "c:/program files (x86)/steam"))
        .File(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", Fx("libraryfolders.vdf"))
        .File(@"D:\SteamLibrary\steamapps\appmanifest_552520.acf", Fx("appmanifest_552520.acf"))
        .File(@"D:\SteamLibrary\steamapps\appmanifest_2369390.acf", Fx("appmanifest_2369390.acf"))
        .File(@"D:\SteamLibrary\steamapps\common\FarCry5\bin\FarCry5.exe")
        .Dir(@"D:\SteamLibrary\steamapps\common\Far Cry 6");

    [Fact]
    public void Vdf_parses_nested_objects_and_escapes()
    {
        var kv = Vdf.Parse(Fx("libraryfolders.vdf"));
        var lf = (Dictionary<string, object>)kv["libraryfolders"];
        Assert.Equal(@"D:\SteamLibrary", ((Dictionary<string, object>)lf["1"])["path"]);
        Assert.Equal("0", ((Dictionary<string, object>)((Dictionary<string, object>)lf["1"])["apps"])["2369390"]);
    }

    [Fact]
    public void Steam_libraries_from_vdf()
    {
        var libs = SteamProvider.Libraries(SteamEnv());
        Assert.Contains(libs, l => l.Equals(@"c:\program files (x86)\steam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"D:\SteamLibrary", libs);
        Assert.Equal(2, libs.Count);   // root and library 0 are the same folder
    }

    [Fact]
    public void Steam_detects_installed_game_with_build()
    {
        var i = new SteamProvider().Detect(Fc5(), SteamEnv());
        Assert.NotNull(i);
        Assert.True(i!.Installed);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\FarCry5", i.InstallDir);
        Assert.Equal("steam://run/552520", i.LaunchUri);
        Assert.Equal("18766066", i.Build);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\FarCry5\bin\FarCry5.exe", i.Exe);
    }

    [Fact]
    public void Steam_owned_but_not_installed_via_steam_is_not_an_install()
    {
        var i = new SteamProvider().Detect(Fc6(), SteamEnv());
        Assert.NotNull(i);
        Assert.False(i!.Installed);
        Assert.Contains("SizeOnDisk 0", i.Note);
    }

    [Fact]
    public void Steam_missing_exe_is_not_installed()
    {
        var env = SteamEnv();
        env.FilesMap.Remove(@"d:\steamlibrary\steamapps\common\farcry5\bin\farcry5.exe");
        Assert.False(new SteamProvider().Detect(Fc5(), env)!.Installed);
    }

    [Fact]
    public void Ubisoft_detects_second_id_and_builds_uplay_uri()
    {
        var env = new FakeStoreEnv()
            .Key(UbisoftProvider.InstallsKey + @"\920", ("InstallDir", "D:/Games/Far Cry 6/"))
            .File(@"D:\Games\Far Cry 6\bin\FarCry6.exe");
        var i = new UbisoftProvider().Detect(Fc6(), env)!;
        Assert.True(i.Installed);
        Assert.Equal(@"D:\Games\Far Cry 6", i.InstallDir);
        Assert.Equal("uplay://launch/920/0", i.LaunchUri);
    }

    [Fact]
    public void Ubisoft_registry_entry_without_files_is_ignored()
    {
        var env = new FakeStoreEnv().Key(UbisoftProvider.InstallsKey + @"\5266", ("InstallDir", "D:/Gone/"));
        Assert.Null(new UbisoftProvider().Detect(Fc6(), env));
    }

    [Fact]
    public void Detector_owned_on_steam_installed_via_ubisoft_prefers_ubisoft()
    {
        var env = SteamEnv()
            .Key(UbisoftProvider.InstallsKey + @"\5266", ("InstallDir", "D:/Games/Far Cry 6/"))
            .File(@"D:\Games\Far Cry 6\bin\FarCry6.exe");
        var r = new StoreDetector(env).Detect(Fc6());
        Assert.Equal("ubisoft", r.Primary!.Store);
        Assert.Equal("uplay://launch/5266/0", r.Primary.LaunchUri);
        Assert.Equal("steam", r.OwnedOnly!.Store);
    }

    [Fact]
    public void Detector_caches_and_fresh_bypasses_cache()
    {
        var env = SteamEnv();
        var now = new DateTime(2026, 1, 1);
        var d = new StoreDetector(env) { Now = () => now };
        Assert.Equal("steam", d.Detect(Fc5()).Primary!.Store);
        env.FilesMap.Clear();
        Assert.NotNull(d.Detect(Fc5()).Primary);             // cached
        Assert.Null(d.Detect(Fc5(), fresh: true).Primary);    // re-scanned
    }

    [Fact]
    public void Epic_manifest_detection()
    {
        var g = new GameDef { Id = "t", Name = "T", ProcessNames = { "Game.exe" }, Stores = new() { Epic = new() { AppNames = { "TestAppName123" } } } };
        var env = new FakeStoreEnv { Folders = { ["ProgramData"] = @"C:\ProgramData" } }
            .File(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\ABC.item", Fx("epic_manifest.item"))
            .File(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\other.item", "{\"AppName\":\"Other\",\"InstallLocation\":\"X:\\\\o\"}")
            .File(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\broken.item", "{ not json")
            .File(@"E:\Epic Games\TestGame\bin\Game.exe");
        var i = new EpicProvider().Detect(g, env)!;
        Assert.Equal(@"E:\Epic Games\TestGame", i.InstallDir);
        Assert.Equal("com.epicgames.launcher://apps/TestAppName123?action=launch&silent=true", i.LaunchUri);
        Assert.Equal(@"E:\Epic Games\TestGame\bin\Game.exe", i.Exe);
        Assert.Equal("1.0.7", i.Build);
    }

    [Fact]
    public void Gog_registry_prefers_direct_exe()
    {
        var g = new GameDef { Id = "t", Name = "T", ProcessNames = { "Game.exe" }, Stores = new() { Gog = new() { Ids = { "1207658924" } } } };
        var env = new FakeStoreEnv()
            .Key(GogProvider.GamesKey + @"\1207658924", ("path", @"C:\GOG Games\Test"), ("exe", @"C:\GOG Games\Test\Game.exe"), ("ver", "1.2"))
            .File(@"C:\GOG Games\Test\Game.exe");
        var i = new GogProvider().Detect(g, env)!;
        Assert.True(i.PreferDirect);
        Assert.Equal(@"C:\GOG Games\Test\Game.exe", i.Exe);
        Assert.Equal("goggalaxy://openGameView/1207658924", i.LaunchUri);
        Assert.Contains("\"launch\":\"exe\"", System.Text.Json.JsonSerializer.Serialize(i.ToUi()));
    }

    [Fact]
    public void Ea_detects_via_registry_key()
    {
        var g = new GameDef { Id = "t", Name = "T", ProcessNames = { "Game.exe" }, Stores = new() { Ea = new() { OfferIds = { "Origin.OFR.50.0001234" }, RegistryKeys = new() { new() { Key = @"HKLM\SOFTWARE\WOW6432Node\EA Games\Test" } } } } };
        var env = new FakeStoreEnv().Key(@"HKLM\SOFTWARE\WOW6432Node\EA Games\Test", ("Install Dir", @"F:\EA Games\TestEA\")).Dir(@"F:\EA Games\TestEA");
        var i = new EaProvider().Detect(g, env)!;
        Assert.Equal(@"F:\EA Games\TestEA", i.InstallDir);
        Assert.Equal("origin2://game/launch?offerIds=Origin.OFR.50.0001234", i.LaunchUri);
    }

    [Fact]
    public void Ea_detects_via_localcontent_manifest()
    {
        var g = new GameDef { Id = "t", Name = "T", ProcessNames = { "Game.exe" }, Stores = new() { Ea = new() { OfferIds = { "Origin.OFR.50.0001234" } } } };
        var env = new FakeStoreEnv { Folders = { ["ProgramData"] = @"C:\ProgramData" } }
            .File(@"C:\ProgramData\Origin\LocalContent\TestEA\Origin.OFR.50.0001234.mfst", Fx("origin_game.mfst"))
            .Dir(@"F:\EA Games\TestEA");
        var i = new EaProvider().Detect(g, env)!;
        Assert.Equal(@"F:\EA Games\TestEA", i.InstallDir);
        // a different offer id must not match
        g.Stores.Ea.OfferIds = new() { "Origin.OFR.50.000123" };
        Assert.Null(new EaProvider().Detect(g, env));
    }

    [Fact]
    public void Xbox_package_detection_reads_manifest_app_id()
    {
        var env = new FakeStoreEnv()
            .Key(XboxProvider.PackagesKey + @"\Ubisoft.FarCry6_1.8.0.0_x64__abcd1234", ("PackageRootFolder", @"C:\XboxGames\Far Cry 6\Content"))
            .Key(XboxProvider.PackagesKey + @"\Other.App_1.0.0.0_x64__zzzz", ("PackageRootFolder", @"C:\x"))
            .File(@"C:\XboxGames\Far Cry 6\Content\AppxManifest.xml", Fx("AppxManifest.xml"));
        var i = new XboxProvider().Detect(Fc6(), env)!;
        Assert.Equal(@"shell:AppsFolder\Ubisoft.FarCry6_abcd1234!Game", i.LaunchUri);
        Assert.True(i.Protected);
        Assert.Equal("1.8.0.0", i.Build);
        Assert.Equal("Ubisoft.FarCry6_abcd1234", XboxProvider.FamilyName("Ubisoft.FarCry6_1.8.0.0_x64__abcd1234"));
    }

    [Fact]
    public void Providers_ignore_games_without_their_ids()
    {
        var g = new GameDef { Id = "t", Name = "T", ProcessNames = { "x.exe" }, Stores = new() };
        var env = new FakeStoreEnv();
        foreach (var p in StoreDetector.DefaultProviders) Assert.Null(p.Detect(g, env));
    }

    [Fact]
    public void AntiCheat_files_and_root_from_exe()
    {
        var g = Fc5(); g.AntiCheatFiles = new() { "bin/EasyAntiCheat/EasyAntiCheat_x64.dll", "bin/EACLaunch.exe" };
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\FarCry5", AntiCheatGuard.RootFromExe(g, @"D:\SteamLibrary\steamapps\common\FarCry5\bin\FarCry5.exe"));
        var env = new FakeStoreEnv().File(@"D:\G\bin\EACLaunch.exe");
        Assert.Equal("bin/EACLaunch.exe", AntiCheatGuard.FoundFile(g, env, @"D:\G"));
        Assert.Null(AntiCheatGuard.FoundFile(g, new FakeStoreEnv(), @"D:\G"));
        g.AntiCheatModules = new() { "EasyAntiCheat_x64.dll" };
        Assert.Equal("easyanticheat_x64.dll", AntiCheatGuard.FoundModule(g, new[] { "FarCry5.exe", "easyanticheat_x64.dll" }));
    }

    [Fact]
    public void FileFingerprint_head_tail_and_pe_fields()
    {
        var dummy = Path.Combine(TestUtil.RepoRoot, "tools", "dummy", "vanta_dummy.exe");
        var fp = FileFingerprint.Compute(dummy);
        Assert.Equal(new FileInfo(dummy).Length, fp.Size);
        Assert.NotNull(fp.PeTimestamp);
        Assert.Equal(64, fp.HeadSha256.Length);
        var data = new byte[3 << 20]; data[^1] = 1;
        var ms = new MemoryStream(data);
        var f2 = FileFingerprint.Compute(ms);
        Assert.NotEqual(f2.HeadSha256, f2.TailSha256);
        Assert.Null(f2.PeTimestamp);
        Assert.Contains("\"fileSize\": 3145728", f2.ToJson("x", null));
    }
}
