using Iced.Intel;
using Vanta.Core;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class RuntimeTests
{
    private static byte[] Image(FakeProcess p) => p.Peek(TestUtil.ModBase, 0x100000);

    [Fact]
    public void Infinite_battery_nops_the_subsd_and_restores_exactly()
    {
        var g = TestUtil.Tlc();
        var (proc, orig, sites) = TestUtil.TlcProcess(g);
        var rt = new CheatRuntime(proc, g) { FreeDelay = TimeSpan.Zero };
        var bat = g.Cheats.First(c => c.Id == "inf_battery");
        var a = rt.Apply(bat);
        var site = sites["inf_battery/drain"];
        Assert.Equal(new byte[] { 0xF2, 0x0F, 0x10, 0x00, 0x90, 0x90, 0x90, 0x90, 0xF2, 0x0F, 0x11, 0x00 }, proc.Peek(site, 12));
        Assert.Empty(rt.Restore(a));
        Assert.Equal(orig, Image(proc));
    }

    public static IEnumerable<object[]> CodeCheats() => TestUtil.Tlc().Cheats.Where(c => c.Impl.Type is "aobPatch" or "aobInject").Select(c => new object[] { c.Id });

    /// <summary>Every code cheat of The Last Caretaker applies on a synthetic image, hooks jump into the cave, the cave
    /// jumps back to site+overwrite, and restore leaves the module byte-identical with no allocations left.</summary>
    [Theory, MemberData(nameof(CodeCheats))]
    public void Every_tlc_code_cheat_applies_and_restores(string id)
    {
        var g = TestUtil.Tlc();
        var (proc, orig, sites) = TestUtil.TlcProcess(g);
        var rt = new CheatRuntime(proc, g) { FreeDelay = TimeSpan.Zero };
        var c = g.Cheats.First(x => x.Id == id);
        var a = rt.Apply(c);
        Assert.NotEqual(orig, Image(proc));
        foreach (var h in c.Impl.Hooks ?? new())
        {
            var sa = sites[id + "/" + h.Site];
            var d = Decoder.Create(64, new ByteArrayCodeReader(proc.Peek(sa, 16))); d.IP = sa;
            var jmp = d.Decode();
            Assert.Equal(Mnemonic.Jmp, jmp.Mnemonic);
            ulong cave = jmp.NearBranchTarget;
            Assert.True(proc.Regions(cave, cave + 1).Any(r => r.Executable), "hook target is allocated memory");
            // padding NOPs up to overwrite
            int ow = c.Impl.Sites![h.Site].Overwrite;
            Assert.All(proc.Peek(sa + 5, ow - 5), b => Assert.Equal(0x90, b));
            // follow the cave: it must contain a jmp back to site+overwrite
            var cd = Decoder.Create(64, new ByteArrayCodeReader(proc.Peek(cave, 256))); cd.IP = cave;
            bool back = false;
            for (int i = 0; i < 40 && !back; i++) { var ins = cd.Decode(); if (ins.Mnemonic == Mnemonic.Jmp && ins.NearBranchTarget == sa + (ulong)ow) back = true; }
            Assert.True(back, "cave jumps back to site+overwrite");
        }
        foreach (var ex in c.Impl.Exports ?? new()) Assert.True(rt.Symbols.ContainsKey(ex));
        Assert.Empty(rt.Restore(a));
        Assert.Equal(orig, Image(proc));
        Assert.Equal(0, proc.AllocatedCount);
        foreach (var ex in c.Impl.Exports ?? new()) Assert.False(rt.Symbols.ContainsKey(ex));
    }

    [Fact]
    public void Health_cave_contains_original_bytes_verbatim()
    {
        var g = TestUtil.Tlc();
        var (proc, _, sites) = TestUtil.TlcProcess(g);
        var rt = new CheatRuntime(proc, g);
        var st = sites["inf_health/st"];
        var origSt = proc.Peek(st, 9);
        rt.Apply(g.Cheats.First(c => c.Id == "inf_health"));
        var d = Decoder.Create(64, new ByteArrayCodeReader(proc.Peek(st, 5))); d.IP = st;
        var cave = d.Decode().NearBranchTarget;
        Assert.Equal(origSt, proc.Peek(cave, 9));   // st_code: db ${orig:st}
    }

    [Fact]
    public void Jump_offsets_are_read_from_the_found_instruction()
    {
        var g = TestUtil.Tlc();
        var (proc, _, sites) = TestUtil.TlcProcess(g);
        var rt = new CheatRuntime(proc, g);
        rt.Apply(g.Cheats.First(c => c.Id == "inf_jump"));
        var sa = sites["inf_jump/j"];
        var d = Decoder.Create(64, new ByteArrayCodeReader(proc.Peek(sa, 5))); d.IP = sa;
        var cave = d.Decode().NearBranchTarget;
        var cd = Decoder.Create(64, new ByteArrayCodeReader(proc.Peek(cave, 32))); cd.IP = cave;
        var i1 = cd.Decode(); var i2 = cd.Decode();
        Assert.Equal(0x464UL, i1.MemoryDisplacement64);   // JumpMaxCount = 0x468 - 4
        Assert.Equal(0x63U, i1.Immediate32);
        Assert.Equal(0x468UL, i2.MemoryDisplacement64);
    }

    [Fact]
    public void Not_unique_or_missing_aob_reports_hit_counts_and_writes_nothing()
    {
        var g = TestUtil.Tlc();
        var (proc, orig, _) = TestUtil.TlcProcess(g);
        // duplicate the grenade pattern 2 region twice more -> pattern 1 absent after we break it, pattern 2/3 ambiguous
        var gren = g.Cheats.First(c => c.Id == "inf_grenades");
        var img = Image(proc);
        var p1 = new AobPattern(gren.Impl.Sites!["g"].Patterns[0].Aob).FindAll(img)[0];
        img[p1] = 0x00;                                    // break pattern 1 ("3B CE ...")
        var extra = TestUtil.Concrete("2B CE 89 4D 18 45 84 F6");
        extra.CopyTo(img, 0xF0000); extra.CopyTo(img, 0xF8000);
        var p2 = new FakeProcess(); p2.AddModule(g.MainModule, TestUtil.ModBase, img);
        var rt = new CheatRuntime(p2, g);
        Strings.Lang = "nl";
        var e = Assert.Throws<CheatException>(() => rt.Apply(gren));
        Assert.Equal("Infinite Grenades: geen unieke AOB gevonden (patroon 1: 0 treffer(s), patroon 2: 3 treffer(s), patroon 3: 3 treffer(s)). Niets gepatcht.", e.Message);
        Assert.Equal(0, p2.WriteCount);
    }

    [Fact]
    public void Expect_and_checks_filter_candidates()
    {
        var g = TestUtil.Tlc();
        var (proc, _, sites) = TestUtil.TlcProcess(g);
        // inf_ammo: modrm must be 0x98..0x9F except 0x9C -> make it 0x9C: no candidate
        var sa = sites["inf_ammo/am"];
        proc.Write(sa + 1, new byte[] { 0x9C });
        var rt = new CheatRuntime(proc, g);
        var e = Assert.Throws<CheatException>(() => rt.Apply(g.Cheats.First(c => c.Id == "inf_ammo")));
        Assert.Contains("patroon 1: 0 treffer(s)", e.Message);
    }

    [Fact]
    public void Failed_apply_rolls_back_partial_writes()
    {
        var g = TestUtil.Tlc();
        var craft = g.Cheats.First(c => c.Id == "free_craft");
        var (proc, orig, _) = TestUtil.TlcProcess(g);
        var bad = new FakeFailingProcess(proc, failWriteNumber: 3);   // cave ok, hook ok, patch fails
        var rt = new CheatRuntime(bad, g) { FreeDelay = TimeSpan.Zero };
        Assert.Throws<CheatException>(() => rt.Apply(craft));
        Assert.Equal(orig, Image(proc));
        Assert.Equal(0, proc.AllocatedCount);
    }

    [Fact]
    public void Pointer_chain_module_symbol_and_rip_base()
    {
        var proc = new FakeProcess();
        var img = new byte[0x2000];
        // code: mov rax,[rip+X] at 0x100 -> target 0x1800 (global pointer)
        var ins = new byte[] { 0x48, 0x8B, 0x05, 0, 0, 0, 0 };
        BitConverter.GetBytes(0x1800 - (0x100 + 7)).CopyTo(ins, 3);
        ins.CopyTo(img, 0x100);
        BitConverter.GetBytes(0x200000000UL).CopyTo(img, 0x1800);        // global -> heap object
        proc.AddModule("game.exe", TestUtil.ModBase, img);
        var heap = new byte[0x100];
        BitConverter.GetBytes(1234).CopyTo(heap, 0x10);
        proc.AddRegion(0x200000000, heap);
        var g = new GameDef { Id = "t", Name = "T", ProcessNames = { "game.exe" } };
        var rt = new CheatRuntime(proc, g);
        CheatDef P(string baseJson, params long[] offs) => new() { Id = "v", Name = "V", Type = "number", Impl = new ImplDef { Type = "pointer", Base = System.Text.Json.JsonDocument.Parse(baseJson).RootElement.Clone(), Offsets = offs.ToList(), ValueType = "int32" } };
        Assert.Equal(0x200000010UL, rt.ResolveAddress(P("\"game.exe+0x1800\"", 0x10)));
        Assert.Equal(0x200000010UL, rt.ResolveAddress(P("{\"aob\":\"48 8B 05 ?? ?? ?? ??\",\"ripOffset\":3,\"insnLength\":7}", 0x10)));
        rt.Symbols["obj"] = 0x200000000;
        Assert.Equal(0x200000010UL, rt.ResolveAddress(P("\"sym:obj+10\"")));
        Assert.Equal(1234.0, rt.ReadValue(0x200000010, "int32"));
        // null pointer in chain -> not available (null), not an exception
        BitConverter.GetBytes(0UL).CopyTo(img, 0x1800); proc.Write(TestUtil.ModBase + 0x1800, new byte[8]);
        Assert.Null(rt.ResolveAddress(P("\"game.exe+0x1800\"", 0x10)));
        Assert.Throws<CheatException>(() => rt.ResolveAddress(P("\"sym:missing\"")));
    }

    [Theory]
    [InlineData("int32", 123456.0)] [InlineData("int64", -5.0)] [InlineData("float", 1.5)] [InlineData("double", 2.25)] [InlineData("byte", 200.0)] [InlineData("int16", -300.0)]
    public void Value_types_roundtrip(string vt, double v)
    {
        var proc = new FakeProcess(); proc.AddRegion(0x1000, new byte[16]);
        var rt = new CheatRuntime(proc, new GameDef());
        Assert.True(rt.WriteValue(0x1000, v, vt));
        Assert.Equal(v, rt.ReadValue(0x1000, vt));
    }

    [Fact]
    public void Placeholders_expand()
    {
        var sites = new Dictionary<string, ulong> { ["j"] = 0x1000 };
        var origs = new Dictionary<string, byte[]> { ["j"] = new byte[] { 0xFF, 0x83, 0x68, 0x04, 0x00, 0x00, 0x48, 0x89 } };
        var defs = new Dictionary<string, SiteDef> { ["j"] = new SiteDef { Overwrite = 6 } };
        var r = CheatRuntime.ExpandPlaceholders(new() { "db ${orig:j}", "db ${orig:j:2:2}", "x ${i32:j:2}", "y ${i32:j:2:-4}", "z ${u8:j:1}" }, sites, origs, defs);
        Assert.Equal(new[] { "db FF 83 68 04 00 00", "db 68 04", "x 468", "y 464", "z 83" }, r);
    }
}

/// <summary>Wraps a FakeProcess and fails the Nth write.</summary>
public sealed class FakeFailingProcess : IProcessMemory
{
    private readonly FakeProcess _p; private readonly int _failAt; private int _n;
    public FakeFailingProcess(FakeProcess p, int failWriteNumber) { _p = p; _failAt = failWriteNumber; }
    public int ProcessId => _p.ProcessId; public bool IsAlive => _p.IsAlive;
    public IReadOnlyList<ModuleInfo> GetModules() => _p.GetModules();
    public IEnumerable<MemRegion> Regions(ulong from, ulong to) => _p.Regions(from, to);
    public bool Read(ulong a, Span<byte> b) => _p.Read(a, b);
    public bool Write(ulong a, ReadOnlySpan<byte> d) => ++_n != _failAt && _p.Write(a, d);
    public ulong Alloc(uint s, ulong n) => _p.Alloc(s, n);
    public void Free(ulong a) => _p.Free(a);
    public void Dispose() { }
}
