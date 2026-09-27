using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

/// <summary>CE auto-assembler conveniences (reference bytes from GNU as 2.4x, intel syntax).</summary>
public class AsmCeSyntaxTests
{
    private static string Hex(params string[] src) => AobScanner.ToHex(MiniAssembler.Assemble(src, 0x140001000).Code);

    [Fact] public void Float_immediate_in_mov_mem() =>
        Assert.Equal("C7 81 A8 00 00 00 00 00 00 3F", Hex("mov [rcx+000000A8],(float)0.5"));

    [Fact] public void Float_immediate_rounding_matches_single() =>
        Assert.Equal("C7 47 78 8F C2 F5 3E", Hex("mov [rdi+78],(float)0.48"));

    [Fact] public void Imul_two_operand_immediate_shorthand() =>
        Assert.Equal("45 6B FF 64", Hex("imul r15d,64"));

    [Fact] public void Dd_whitespace_separated_floats()
    {
        var b = MiniAssembler.Assemble(new[] { "dd (float)1.030 (float)1.030 (float)1.015 (float)1" }, 0).Code;
        Assert.Equal(16, b.Length);
        Assert.Equal(1.03f, BitConverter.ToSingle(b, 0));
        Assert.Equal(1.015f, BitConverter.ToSingle(b, 8));
        Assert.Equal(1f, BitConverter.ToSingle(b, 12));
    }

    [Fact]
    public void Anonymous_labels_forward_and_backward()
    {
        var r = MiniAssembler.Assemble(new[] { "@@:", "nop", "jne @f", "jmp @b", "@@:", "ret" }, 0x1000);
        // label branches are always rel32 (sizes independent of addresses): 0 nop | 1 jne -> 12 | 7 jmp -> 0 | 12 ret
        Assert.Equal("90 0F 85 05 00 00 00 E9 F4 FF FF FF C3", AobScanner.ToHex(r.Code));
    }

    [Fact] public void Short_keyword_on_branch_target() =>
        Assert.Equal("0F 85 00 00 00 00", Hex("jne short target", "target:"));

    [Fact]
    public void Module_plus_offset_resolves_through_externals()
    {
        var r = MiniAssembler.Assemble(new[] { "call Game.dll+1000" }, 0x140002000, new Dictionary<string, ulong> { ["Game.dll"] = 0x140000000 });
        Assert.Equal("E8 FB EF FF FF", AobScanner.ToHex(r.Code));   // target 0x140001000
    }
}
