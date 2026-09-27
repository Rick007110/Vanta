using Vanta.Core;
using Xunit;

namespace Vanta.Tests;

public class HotkeyParserTests
{
    [Theory]
    [InlineData("F5", 0u, 0x74u)]
    [InlineData("Ctrl+F8", HotkeyParser.MOD_CONTROL, 0x77u)]
    [InlineData("Numpad+", 0u, 0x6Bu)]
    [InlineData("Shift+Numpad-", HotkeyParser.MOD_SHIFT, 0x6Du)]
    [InlineData("Ctrl+Alt+Shift+Win+A", HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_ALT | HotkeyParser.MOD_SHIFT | HotkeyParser.MOD_WIN, 0x41u)]
    [InlineData("F24", 0u, 0x87u)]
    [InlineData("Numpad7", 0u, 0x67u)]
    [InlineData("ctrl+1", HotkeyParser.MOD_CONTROL, 0x31u)]
    [InlineData("PageDown", 0u, 0x22u)]
    public void Parses(string combo, uint mods, uint vk)
    {
        Assert.True(HotkeyParser.TryParse(combo, out var m, out var v));
        Assert.Equal(mods, m); Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("")] [InlineData("F25")] [InlineData("Hyper+F1")] [InlineData("Banana")] [InlineData("Ctrl+")]
    public void Rejects(string combo) => Assert.False(HotkeyParser.TryParse(combo, out _, out _));

    [Fact]
    public void Every_default_hotkey_in_the_catalog_is_registrable()
    {
        foreach (var c in TestUtil.Tlc().Cheats)
            foreach (var hk in new[] { c.Hotkey, c.HotkeyInc, c.HotkeyDec }.Where(h => h != null))
                Assert.True(HotkeyParser.TryParse(hk, out _, out _), hk);
    }
}

public class ArtCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vanta-art-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Falls_back_through_file_names_caches_and_remembers_misses()
    {
        var calls = new List<string>();
        var jpg = new byte[1000]; jpg[0] = 0xFF;
        Task<byte[]?> Dl(string url, CancellationToken _) { lock (calls) calls.Add(url); return Task.FromResult(url.EndsWith("/1783560/library_600x900.jpg") && url.Contains("cdn.cloudflare") ? jpg : null); }
        var art = new ArtCache(_dir, Dl);
        var a = await art.GetAsync(1783560, "cover");
        Assert.NotNull(a);
        Assert.Equal("image/jpeg", a!.Value.type);
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/steam/apps/1783560/library_600x900_2x.jpg", calls[0]);
        Assert.True(File.Exists(Path.Combine(_dir, "1783560_cover.jpg")));
        int n = calls.Count;
        Assert.NotNull(await new ArtCache(_dir, Dl).GetAsync(1783560, "cover"));   // from disk
        Assert.Equal(n, calls.Count);
        Assert.Null(await art.GetAsync(1783560, "logo"));                          // miss
        Assert.True(File.Exists(Path.Combine(_dir, "1783560_logo.miss")));
        n = calls.Count;
        Assert.Null(await art.GetAsync(1783560, "logo"));                          // negative cache: no new requests
        Assert.Equal(n, calls.Count);
        Assert.Null(await art.GetAsync(0, "cover"));
        Assert.Null(await art.GetAsync(1, "nope"));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_download()
    {
        int count = 0;
        async Task<byte[]?> Dl(string url, CancellationToken _) { Interlocked.Increment(ref count); await Task.Delay(50); return new byte[500]; }
        var art = new ArtCache(_dir, Dl);
        var r = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => art.GetAsync(42, "hero")));
        Assert.All(r, x => Assert.NotNull(x));
        Assert.Equal(1, count);
    }
}
