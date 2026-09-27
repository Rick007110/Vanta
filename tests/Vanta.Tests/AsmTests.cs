using System.Text.Json;
using Iced.Intel;
using Vanta.Core.Engine;
using Xunit;

namespace Vanta.Tests;

public class AsmTests
{
    public static IEnumerable<object[]> Reference()
    {
        var arr = JsonSerializer.Deserialize<string[][]>(File.ReadAllText(TestUtil.Data("asm_expected.json")))!;
        return arr.Select(a => new object[] { a[0], a[1] });
    }

    /// <summary>Expected bytes were produced by GNU as (x86_64-w64-mingw32-as, Intel syntax); see tests/asmref.</summary>
    [Theory, MemberData(nameof(Reference))]
    public void Encodes_like_gnu_as(string line, string expectedHex)
    {
        var code = MiniAssembler.Assemble(new[] { line }, 0x140000000).Code;
        Assert.Equal(expectedHex, string.Concat(code.Select(b => b.ToString("X2"))));
    }

    private static List<Instruction> Decode(byte[] code, ulong ip)
    {
        var d = Decoder.Create(64, new ByteArrayCodeReader(code)); d.IP = ip;
        var list = new List<Instruction>();
        while (d.IP < ip + (ulong)code.Length) list.Add(d.Decode());
        return list;
    }

    [Fact]
    public void Labels_jumps_and_rip_relative_data_resolve_correctly()
    {
        ulong org = 0x13FFF0000, site = 0x140001234;
        var res = MiniAssembler.Assemble(new[]
        {
            "code:", "  imul ebx,[data+8]", "  add eax,ebx", "  mov [data],r14", "  jne skip", "  nop", "skip:", "  jmp site_ret",
            "data:", "  dq 0", "  dd 1", "  dd (float)1.5",
        }, org, new Dictionary<string, ulong> { ["site_ret"] = site + 5 });
        var ins = Decode(res.Code[..(int)(res.Labels["data"] - org)], org);
        ulong data = res.Labels["data"];
        Assert.Equal(Mnemonic.Imul, ins[0].Mnemonic);
        Assert.Equal(data + 8, ins[0].MemoryDisplacement64);          // RIP-relative -> absolute target
        Assert.True(ins[0].IsIPRelativeMemoryOperand);
        Assert.Equal(data, ins[2].MemoryDisplacement64);
        Assert.Equal(res.Labels["skip"], ins[3].NearBranchTarget);
        Assert.Equal(site + 5, ins[5].NearBranchTarget);
        var tail = res.Code[(int)(data - org)..];
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 }, tail[..12]);
        Assert.Equal(1.5f, BitConverter.ToSingle(tail, 12));
    }

    [Fact]
    public void Numbers_are_hex_by_default_and_hash_is_decimal()
    {
        Assert.Equal(MiniAssembler.Assemble(new[] { "mov eax,#99" }, 0).Code, MiniAssembler.Assemble(new[] { "mov eax,63" }, 0).Code);
        Assert.Equal(MiniAssembler.Assemble(new[] { "mov eax,$10" }, 0).Code, MiniAssembler.Assemble(new[] { "mov eax,0x10" }, 0).Code);
    }

    [Fact]
    public void Ambiguous_memory_size_defaults_to_dword_and_explicit_size_is_respected()
    {
        Assert.Equal("C7 00 00 00 00 00", AobScanner.ToHex(MiniAssembler.Assemble(new[] { "mov [rax],0" }, 0).Code));
        Assert.Equal("C6 00 00", AobScanner.ToHex(MiniAssembler.Assemble(new[] { "mov byte ptr [rax],0" }, 0).Code));
        Assert.Equal("48 C7 00 00 00 00 00", AobScanner.ToHex(MiniAssembler.Assemble(new[] { "mov qword ptr [rax],0" }, 0).Code));
    }

    [Fact]
    public void Nop_and_db_directives()
    {
        Assert.Equal("90 90 90 F3 0F", AobScanner.ToHex(MiniAssembler.Assemble(new[] { "nop 3", "db F3 0F" }, 0).Code));
    }

    [Theory]
    [InlineData("mov eax,[nowhere]", "onbekend")]
    [InlineData("jmp nowhere", "onbekend")]
    [InlineData("frobnicate eax", "onbekende instructie")]
    [InlineData("mov eax,rbx", "geen geldige codering")]
    [InlineData("alloc(x,100)", "onbekende instructie")]
    [InlineData("x:|x:", "dubbel")]
    public void Reports_errors(string src, string fragment)
    {
        var e = Assert.Throws<AsmException>(() => MiniAssembler.Assemble(src.Split('|'), 0x1000));
        Assert.Contains(fragment, e.Message);
    }

    [Fact]
    public void Jump_encodings()
    {
        Assert.Equal("E9 FB 0F 00 00 90 90", AobScanner.ToHex(MiniAssembler.JmpRel32(0x1000, 0x2000, 7)));
        Assert.Throws<AsmException>(() => MiniAssembler.JmpRel32(0x1000, 0x2_0000_0000));
        var abs = MiniAssembler.JmpAbs64(0x7FF612345678, 15);
        Assert.Equal("FF 25 00 00 00 00 78 56 34 12 F6 7F 00 00 90", AobScanner.ToHex(abs));
        var j = Decode(abs[..6], 0x1000)[0];
        Assert.Equal(Mnemonic.Jmp, j.Mnemonic);
        Assert.Equal(0x1006UL, j.MemoryDisplacement64);               // jmp [rip+0] -> the qword that follows
        Assert.Equal(0x7FF612345678UL, BitConverter.ToUInt64(abs, 6));
    }
}
