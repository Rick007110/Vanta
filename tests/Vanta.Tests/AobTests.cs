using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class AobTests
{
    [Fact]
    public void Parses_wildcards_and_nibbles()
    {
        var p = new AobPattern("F2 0F ?? 1? ?F * ??");
        Assert.Equal(7, p.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0, 0xF0, 0x0F, 0, 0 }, p.Mask);
        Assert.True(p.MatchAt(new byte[] { 0xF2, 0x0F, 0x99, 0x1A, 0xBF, 1, 2 }, 0));
        Assert.False(p.MatchAt(new byte[] { 0xF2, 0x0F, 0x99, 0x2A, 0xBF, 1, 2 }, 0));
        Assert.Equal(3, new AobPattern("F20F10").Length);
        Assert.Throws<FormatException>(() => new AobPattern("F2 0G"));
        Assert.Throws<FormatException>(() => new AobPattern(""));
    }

    [Fact]
    public void FindAll_matches_naive_search_on_random_data()
    {
        var rnd = new Random(1234);
        var data = new byte[200_000];
        rnd.NextBytes(data);
        foreach (var text in new[] { "?? 0F 5C", "F2 0F", "00 ?? 00", "?? ?? 7?", "AB" })
        {
            var p = new AobPattern(text);
            var naive = Enumerable.Range(0, data.Length - p.Length + 1).Where(i => p.MatchAt(data, i)).ToList();
            Assert.Equal(naive, p.FindAll(data));
        }
    }

    [Fact]
    public void Scan_finds_patterns_across_chunk_boundaries_and_respects_executable_flag()
    {
        var img = new byte[AobScanner.ChunkSize * 2 + 0x1000];
        var pat = TestUtil.Concrete("F2 0F 10 00 F2 0F 5C C6 F2 0F 11 00");
        pat.CopyTo(img, AobScanner.ChunkSize - 5);             // straddles the first chunk boundary
        pat.CopyTo(img, img.Length - pat.Length);               // at the very end
        var proc = new FakeProcess();
        var mod = proc.AddModule("game.exe", 0x140000000, img);
        proc.AddRegion(0x150000000, (byte[])img.Clone(), exec: false);
        var hits = AobScanner.ScanModule(proc, mod, new AobPattern("F2 0F 10 00 F2 0F 5C C6"));
        Assert.Equal(new ulong[] { 0x140000000 + (ulong)AobScanner.ChunkSize - 5, 0x140000000 + (ulong)(img.Length - pat.Length) }, hits);
        var data = AobScanner.Scan(proc, new AobPattern("F2 0F 10 00 F2 0F 5C C6"), 0x150000000, (ulong)img.Length);
        Assert.Empty(data);                                      // non-executable region skipped by default
        Assert.Equal(2, AobScanner.Scan(proc, new AobPattern("F2 0F 10 00 F2 0F 5C C6"), 0x150000000, (ulong)img.Length, anyMemory: true).Count);
    }

    [Fact]
    public void Unreadable_regions_are_skipped()
    {
        var proc = new FakeProcess();
        proc.AddRegion(0x10000, new byte[] { 1, 2, 3, 4 }, exec: true, readable: false);
        proc.AddRegion(0x20000, new byte[] { 1, 2, 3, 4 }, exec: true);
        Assert.Equal(new ulong[] { 0x20000 }, AobScanner.Scan(proc, new AobPattern("01 02 03"), 0, 0x30000));
    }
}
