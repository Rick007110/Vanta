using Vanta.Core;
using Vanta.Core.Engine;

namespace Vanta.Tests;

public static class TestUtil
{
    public const ulong ModBase = 0x140000000;
    public static string Data(string rel) => Path.Combine(AppContext.BaseDirectory, "data", rel);
    public static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    public static GameDef Tlc() => VJson.LoadGame(Path.Combine(RepoRoot, "games", "the-last-caretaker", "game.json"));

    /// <summary>Byte pattern -> concrete bytes (wildcards filled with <paramref name="fill"/>).</summary>
    public static byte[] Concrete(string aob, byte fill = 0x02)
    {
        var p = new AobPattern(aob);
        return p.Bytes.Select((b, i) => (byte)((b & p.Mask[i]) | (fill & ~p.Mask[i]))).ToArray();
    }

    /// <summary>A fake module image (0xCC filled) with each site's first pattern placed at a distinct offset.</summary>
    public static (FakeProcess proc, byte[] image, Dictionary<string, ulong> siteAddr) TlcProcess(GameDef g, int size = 0x100000)
    {
        var img = Enumerable.Repeat((byte)0xCC, size).ToArray();
        var addr = new Dictionary<string, ulong>();
        int pos = 0x1000;
        foreach (var c in g.Cheats)
            foreach (var (name, site) in c.Impl.Sites ?? new())
            {
                var bytes = Concrete(site.Patterns[0].Aob);
                bytes.CopyTo(img, pos);
                long at = pos + site.Patterns[0].Offset;
                if (site.Expect != null)
                {
                    // wildcard bytes at the site must still satisfy "expect" (e.g. FF 90 = call [rax+disp])
                    var ex = new AobPattern(site.Expect); var eb = Concrete(site.Expect);
                    for (int i = 0; i < eb.Length; i++) if (ex.Mask[i] != 0) img[at + i] = (byte)((img[at + i] & ~ex.Mask[i]) | (eb[i] & ex.Mask[i]));
                }
                addr[c.Id + "/" + name] = ModBase + (ulong)at;
                pos += 0x400;
            }
        var proc = new FakeProcess();
        proc.AddModule(g.MainModule, ModBase, img);
        return (proc, (byte[])img.Clone(), addr);
    }
}
