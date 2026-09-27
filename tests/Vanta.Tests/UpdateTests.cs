using System.IO.Compression;
using Vanta.Core.Update;
using Xunit;

namespace Vanta.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("v0.2.0", "0.2.0", 0)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("0.2", "0.2.0", 0)]
    [InlineData("0.2.0+build.7", "0.2.0", 0)]
    [InlineData("0.2.0.0", "0.2.0", 0)]
    public void Compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));

    [Theory, InlineData(""), InlineData("latest"), InlineData("v"), InlineData("1.x")]
    public void Rejects_garbage(string s) => Assert.False(SemVer.TryParse(s, out _));
}

public class UpdateCheckTests
{
    internal static string ReleaseJson(string tag = "v0.2.0", bool pre = false, bool sha = true, string zip = "Vanta-v0.2.0.zip") => $$"""
    { "tag_name": "{{tag}}", "name": "Vanta {{tag}}", "draft": false, "prerelease": {{(pre ? "true" : "false")}},
      "html_url": "https://github.com/Rick007110/Vanta/releases/tag/{{tag}}", "body": "- Far Cry 5 en 6",
      "assets": [
        { "name": "Vanta-v0.2.0-src.zip", "size": 10, "browser_download_url": "https://example.test/src.zip" },
        { "name": "{{zip}}", "size": 64000000, "browser_download_url": "https://example.test/{{zip}}" }
        {{(sha ? $$""", { "name": "{{zip}}.sha256", "size": 90, "browser_download_url": "https://example.test/{{zip}}.sha256" }""" : "")}}
      ] }
    """;

    private static UpdateChecker Checker(string current, Func<HttpResult> r) => new(SemVer.Parse(current), (_, _) => Task.FromResult(r()));

    [Fact]
    public void Parses_release_assets_and_ignores_source_zip()
    {
        var r = GitHubRelease.Parse(ReleaseJson())!;
        Assert.Equal("0.2.0", r.Version.ToString());
        Assert.Equal("Vanta-v0.2.0.zip", r.ZipName);
        Assert.Equal("Vanta-v0.2.0.zip.sha256", r.ShaName);
        Assert.Equal(64000000, r.ZipSize);
        Assert.Contains("Far Cry", r.Notes);
    }

    [Fact] public void Prerelease_is_ignored() => Assert.Null(GitHubRelease.Parse(ReleaseJson(pre: true)));

    [Fact]
    public async Task Newer_release_is_available()
    {
        var c = await Checker("0.1.0", () => new HttpResult(200, ReleaseJson())).CheckAsync();
        Assert.Equal(UpdateState.Available, c.State);
        Assert.Equal("0.2.0", c.Release!.Version.ToString());
    }

    [Fact]
    public async Task Same_version_is_up_to_date() =>
        Assert.Equal(UpdateState.UpToDate, (await Checker("0.2.0", () => new HttpResult(200, ReleaseJson())).CheckAsync()).State);

    [Fact]
    public async Task Offline_is_quiet() =>
        Assert.Equal(UpdateState.Offline, (await Checker("0.1.0", () => throw new HttpRequestException("no route")).CheckAsync()).State);

    [Fact]
    public async Task Rate_limit_is_quiet()
    {
        var h = new Dictionary<string, string> { ["x-ratelimit-remaining"] = "0" };
        Assert.Equal(UpdateState.RateLimited, (await Checker("0.1.0", () => new HttpResult(403, "{}", h)).CheckAsync()).State);
        Assert.Equal(UpdateState.RateLimited, (await Checker("0.1.0", () => new HttpResult(403, "{\"message\":\"API rate limit exceeded\"}")).CheckAsync()).State);
    }

    [Fact]
    public async Task No_release_yet() =>
        Assert.Equal(UpdateState.NoRelease, (await Checker("0.1.0", () => new HttpResult(404, "{}")).CheckAsync()).State);

    [Fact]
    public async Task Garbage_is_an_error_not_an_exception() =>
        Assert.Equal(UpdateState.Error, (await Checker("0.1.0", () => new HttpResult(200, "<html>")).CheckAsync()).State);

    [Theory]
    [InlineData("3f2a", null)]
    public void Sum_file_needs_full_hash(string text, string? expected) => Assert.Equal(expected, Sha256Util.FromSumFile(text, "Vanta-v0.2.0.zip"));

    [Fact]
    public void Sum_file_formats()
    {
        var h = new string('a', 64); var o = new string('b', 64);
        Assert.Equal(h, Sha256Util.FromSumFile($"{h}  Vanta-v0.2.0.zip\n", "Vanta-v0.2.0.zip"));
        Assert.Equal(h, Sha256Util.FromSumFile($"{o} *other.zip\r\n{h} *Vanta-v0.2.0.zip\r\n", "Vanta-v0.2.0.zip"));
        Assert.Equal(h, Sha256Util.FromSumFile(h.ToUpperInvariant() + "\n", "Vanta-v0.2.0.zip"));
        Assert.Null(Sha256Util.FromSumFile($"{o}  other.zip\n", "Vanta-v0.2.0.zip"));
    }
}

public sealed class UpdateFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vanta-upd-" + Guid.NewGuid().ToString("N"));
    private string App => Path.Combine(_root, "app");
    private string Data => Path.Combine(_root, "data");
    public UpdateFlowTests() { Directory.CreateDirectory(App); Directory.CreateDirectory(Data); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void OldApp()
    {
        File.WriteAllText(Path.Combine(App, "Vanta.exe"), "exe v0.1");
        Directory.CreateDirectory(Path.Combine(App, "games", "the-last-caretaker"));
        File.WriteAllText(Path.Combine(App, "games", "the-last-caretaker", "game.json"), "tlc v0.1");
        Directory.CreateDirectory(Path.Combine(App, "games", "my-own-game"));
        File.WriteAllText(Path.Combine(App, "games", "my-own-game", "game.json"), "mine");
        File.WriteAllText(Path.Combine(Data, "settings.json"), "{\"language\":\"nl\"}");
        Directory.CreateDirectory(Path.Combine(Data, "games", "user-game"));
        File.WriteAllText(Path.Combine(Data, "games", "user-game", "game.json"), "user");
    }

    private string NewZip(bool withExe = true, string top = "Vanta/")
    {
        var zip = Path.Combine(_root, "Vanta-v0.2.0.zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Add(string name, string text) { using var w = new StreamWriter(z.CreateEntry(top + name).Open()); w.Write(text); }
        if (withExe) Add("Vanta.exe", "exe v0.2");
        Add("games/the-last-caretaker/game.json", "tlc v0.2");
        Add("games/far-cry-6/game.json", "fc6");
        Add("LEESMIJ.txt", "leesmij");
        return zip;
    }

    private static string R(params string[] p) => File.ReadAllText(Path.Combine(p));

    [Fact]
    public void Apply_installs_merges_and_keeps_user_data()
    {
        OldApp(); var zip = NewZip(); var store = new UpdateStore(Data);
        UpdateApplier.Apply(zip, Sha256Util.OfFile(zip), App, store);
        Assert.Equal("exe v0.2", R(App, "Vanta.exe"));
        Assert.Equal("tlc v0.2", R(App, "games", "the-last-caretaker", "game.json"));
        Assert.Equal("fc6", R(App, "games", "far-cry-6", "game.json"));
        Assert.Equal("mine", R(App, "games", "my-own-game", "game.json"));        // not in the package: untouched
        Assert.Equal("{\"language\":\"nl\"}", R(Data, "settings.json"));
        Assert.Equal("user", R(Data, "games", "user-game", "game.json"));
        Assert.Equal("exe v0.1", R(store.BackupDir, "Vanta.exe"));
    }

    [Fact]
    public void Wrong_hash_changes_nothing()
    {
        OldApp(); var zip = NewZip(); var store = new UpdateStore(Data);
        Assert.Throws<InvalidOperationException>(() => UpdateApplier.Apply(zip, new string('0', 64), App, store));
        Assert.Equal("exe v0.1", R(App, "Vanta.exe"));
    }

    [Fact]
    public void Package_without_exe_is_refused_before_touching_the_app()
    {
        OldApp(); var zip = NewZip(withExe: false); var store = new UpdateStore(Data);
        Assert.Throws<InvalidOperationException>(() => UpdateApplier.Apply(zip, Sha256Util.OfFile(zip), App, store));
        Assert.Equal("exe v0.1", R(App, "Vanta.exe"));
        Assert.False(Directory.Exists(store.BackupDir));
    }

    [Fact]
    public void Zip_without_top_folder_works()
    {
        OldApp(); var zip = NewZip(top: ""); var store = new UpdateStore(Data);
        UpdateApplier.Apply(zip, Sha256Util.OfFile(zip), App, store);
        Assert.Equal("exe v0.2", R(App, "Vanta.exe"));
    }

    [Fact]
    public void Rollback_restores_previous_version_and_removes_new_only_files()
    {
        OldApp(); var zip = NewZip(); var store = new UpdateStore(Data);
        var content = UpdateApplier.Apply(zip, Sha256Util.OfFile(zip), App, store);
        UpdateApplier.Rollback(store.BackupDir, App, content);
        Assert.Equal("exe v0.1", R(App, "Vanta.exe"));
        Assert.Equal("tlc v0.1", R(App, "games", "the-last-caretaker", "game.json"));
        Assert.False(File.Exists(Path.Combine(App, "games", "far-cry-6", "game.json")));
        Assert.False(Directory.Exists(Path.Combine(App, "games", "far-cry-6")));
        Assert.Equal("mine", R(App, "games", "my-own-game", "game.json"));
    }

    [Fact]
    public void Zip_slip_is_refused()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) using (var w = new StreamWriter(z.CreateEntry("../evil.txt").Open())) w.Write("x");
        Assert.Throws<InvalidOperationException>(() => UpdateApplier.Extract(zip, Path.Combine(_root, "st")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task Download_verifies_hash_and_writes_pending()
    {
        var src = NewZip(); var sha = Sha256Util.OfFile(src);
        var rel = GitHubRelease.Parse(UpdateCheckTests.ReleaseJson())!;
        var store = new UpdateStore(Data);
        var p = await store.DownloadAsync(rel, (_, _) => Task.FromResult($"{sha}  Vanta-v0.2.0.zip\n"), (_, dest, _, _) => { File.Copy(src, dest, true); return Task.CompletedTask; });
        Assert.Equal(sha, p.Sha256);
        Assert.True(File.Exists(p.Zip));
        Assert.Equal("0.2.0", store.LoadPending()!.Version);
        Assert.NotNull(store.ApplicablePending(SemVer.Parse("0.1.0")));
        Assert.Null(store.ApplicablePending(SemVer.Parse("0.2.0")));          // already on that version: pending is dropped
        Assert.Null(store.LoadPending());
    }

    [Fact]
    public async Task Download_with_bad_hash_is_deleted_and_not_pending()
    {
        var src = NewZip();
        var rel = GitHubRelease.Parse(UpdateCheckTests.ReleaseJson())!;
        var store = new UpdateStore(Data);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DownloadAsync(rel, (_, _) => Task.FromResult(new string('1', 64) + "  Vanta-v0.2.0.zip"),
            (_, dest, _, _) => { File.Copy(src, dest, true); return Task.CompletedTask; }));
        Assert.Null(store.LoadPending());
        Assert.False(File.Exists(store.DownloadPath(rel)));
        Assert.False(File.Exists(store.DownloadPath(rel) + ".part"));
    }

    [Fact]
    public async Task Release_without_sha_is_refused()
    {
        var rel = GitHubRelease.Parse(UpdateCheckTests.ReleaseJson(sha: false))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdateStore(Data).DownloadAsync(rel, (_, _) => Task.FromResult(""), (_, _, _, _) => Task.CompletedTask));
    }

    [Fact]
    public void Failed_version_is_not_applied_again()
    {
        var src = NewZip(); var store = new UpdateStore(Data);
        store.SavePending(new PendingUpdate("0.2.0", src, Sha256Util.OfFile(src), "", DateTime.UtcNow));
        store.MarkFailed("0.2.0");
        Assert.Null(store.ApplicablePending(SemVer.Parse("0.1.0")));
    }

    [Fact]
    public void Tampered_pending_zip_is_not_applied()
    {
        var src = NewZip(); var store = new UpdateStore(Data);
        store.SavePending(new PendingUpdate("0.2.0", src, Sha256Util.OfFile(src), "", DateTime.UtcNow));
        File.AppendAllText(src, "x");
        Assert.Null(store.ApplicablePending(SemVer.Parse("0.1.0")));
    }
}
