using Xunit;
using Vanta.Core;
using Vanta.Core.Engine;

namespace Vanta.Tests;

public class AvatarCatalogTests
{
    private static GameDef Load() => VJson.LoadGame(Path.Combine(TestUtil.RepoRoot, "games", "avatar-frontiers-of-pandora", "game.json"));

    [Fact]
    public void Every_inject_script_assembles()
    {
        var g = Load();
        foreach (var c in g.Cheats.Where(c => c.Impl.Type == "aobInject"))
        {
            var ext = new Dictionary<string, ulong>();
            ulong at = 0x140100000;
            foreach (var name in c.Impl.Sites!.Keys) { ext[name] = at; ext[name + "_ret"] = at + 0x10; at += 0x1000; }
            var r = MiniAssembler.Assemble(c.Impl.Asm!, 0x140000000, ext);
            Assert.True(r.Code.Length > 0, c.Id);
        }
    }

    [Fact]
    public void Inventory_hook_keeps_the_original_instructions_and_ends_with_rcx_zero()
    {
        var c = Load().Cheats.Single(x => x.Id == "inv_hook");
        Assert.Equal("mov rax,[rax+18]", c.Impl.Asm![1]);
        var i = c.Impl.Asm.IndexOf("jmp inv_ret");
        Assert.Equal("xor ecx,ecx", c.Impl.Asm[i - 1]);
        Assert.Equal(new[] { "push rcx", "push rdx", "push r8", "push r10", "push r11" }, c.Impl.Asm.Skip(2).Take(5));
        Assert.Equal(new[] { "pop r11", "pop r10", "pop r8", "pop rdx", "pop rcx" }, c.Impl.Asm.Skip(i - 6).Take(5));
    }

    [Fact]
    public void Stat_freezes_copy_from_the_maximum_and_never_restore()
    {
        var g = Load();
        foreach (var id in new[] { "inf_breath", "ikran_stamina", "ikran_health" })
        {
            var c = g.Cheats.Single(x => x.Id == id);
            Assert.Equal("experimental", c.Confidence);
            Assert.Equal(c.Impl.Offsets![0] + 4, c.Impl.FreezeFrom!.Offsets![0]);
            Assert.False(c.Impl.Restore);
            Assert.Contains("afop_hook", c.Requires!);
        }
        Assert.Contains("health_ptr", g.Cheats.Single(x => x.Id == "afop_hook").Impl.Exports!);
    }
}
