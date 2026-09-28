using System.Globalization;
using System.Text.RegularExpressions;
using Iced.Intel;

namespace Vanta.Core.Engine;

public sealed class AsmException : Exception { public AsmException(string m) : base(m) { } }

public sealed class AsmResult
{
    public byte[] Code { get; init; } = Array.Empty<byte>();
    public Dictionary<string, ulong> Labels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Small x64 assembler for Cheat-Engine-style AA text (subset), built on Iced's encoder.
/// Syntax: "label:", instructions in Intel syntax ("mov dword ptr [rbx+10],63"), db/dw/dd/dq, "nop N".
/// Numbers are HEX by default (CE convention); "#123" is decimal; "(float)1.5" in dd/dq.
/// Memory operands that reference a label/symbol become RIP-relative. Branches always use rel32 (stable sizes).
/// </summary>
public static class MiniAssembler
{
    private static readonly Lazy<Dictionary<Mnemonic, List<Code>>> Index = new(BuildIndex);
    private static readonly Dictionary<string, Mnemonic> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jz"] = Mnemonic.Je, ["jnz"] = Mnemonic.Jne, ["jc"] = Mnemonic.Jb, ["jnc"] = Mnemonic.Jae, ["jnae"] = Mnemonic.Jb,
        ["jnb"] = Mnemonic.Jae, ["jna"] = Mnemonic.Jbe, ["jnbe"] = Mnemonic.Ja, ["jnge"] = Mnemonic.Jl, ["jnl"] = Mnemonic.Jge,
        ["jng"] = Mnemonic.Jle, ["jnle"] = Mnemonic.Jg, ["jpe"] = Mnemonic.Jp, ["jpo"] = Mnemonic.Jnp,
        ["cmovz"] = Mnemonic.Cmove, ["cmovnz"] = Mnemonic.Cmovne, ["sete"] = Mnemonic.Sete, ["setz"] = Mnemonic.Sete, ["setnz"] = Mnemonic.Setne,
        ["pushf"] = Mnemonic.Pushfq, ["popf"] = Mnemonic.Popfq, ["pushfq"] = Mnemonic.Pushfq, ["popfq"] = Mnemonic.Popfq,
        ["movabs"] = Mnemonic.Mov, ["retn"] = Mnemonic.Ret,
    };

    private static Dictionary<Mnemonic, List<Code>> BuildIndex()
    {
        var d = new Dictionary<Mnemonic, List<Code>>();
        foreach (Code c in Enum.GetValues(typeof(Code)))
        {
            if (c == Code.INVALID) continue;
            var oc = c.ToOpCode();
            if (!oc.IsInstruction || !oc.Mode64) continue;
            if (oc.Encoding != EncodingKind.Legacy && oc.Encoding != EncodingKind.VEX) continue;
            bool skip = false;
            for (int i = 0; i < oc.OpCount; i++)
            {
                var k = oc.GetOpKind(i).ToString();
                if (k.StartsWith("mem_offs") || k == "br64_1" || k.StartsWith("br16") || k.StartsWith("br32") || k.StartsWith("seg_") || k.Contains("mib") || k.StartsWith("sibmem")) skip = true;
            }
            if (skip) continue;
            if (!d.TryGetValue(oc.Mnemonic, out var list)) d[oc.Mnemonic] = list = new List<Code>();
            list.Add(c);
        }
        return d;
    }

    // ---------------- operands ----------------
    private enum OpT { Reg, Mem, Imm }
    private sealed class Opnd
    {
        public OpT T;
        public Register Reg;
        public Register Base, Index; public int Scale = 1; public long Disp; public string? Sym; public int Size; // Size: ptr size in bytes (0 = unspecified)
        public long Imm; public string? ImmSym;
        public override string ToString() => T == OpT.Reg ? Reg.ToString() : T == OpT.Imm ? (ImmSym ?? Imm.ToString("X")) : $"[{Base}+{Index}*{Scale}+{Sym}+{Disp:X}]";
    }

    private static readonly Dictionary<string, int> PtrSizes = new(StringComparer.OrdinalIgnoreCase)
    { ["byte"] = 1, ["word"] = 2, ["dword"] = 4, ["qword"] = 8, ["xmmword"] = 16, ["ymmword"] = 32, ["tbyte"] = 10, ["oword"] = 16 };

    public static bool TryRegister(string s, out Register r)
    {
        r = Register.None;
        s = s.Trim();
        if (s.Length < 2 || s.Length > 6 || !char.IsLetter(s[0])) return false;
        var m = Regex.Match(s, @"^r(\d+)b$", RegexOptions.IgnoreCase);
        if (m.Success) s = "r" + m.Groups[1].Value + "l";
        if (!Enum.TryParse(s, true, out r) || r == Register.None) return false;
        return r.IsGPR() || r.IsXMM() || r.IsYMM();
    }

    public static bool TryNumber(string s, out long v)
    {
        v = 0; s = s.Trim();
        if (s.Length == 0) return false;
        bool neg = false;
        if (s[0] == '-') { neg = true; s = s[1..].Trim(); }
        else if (s[0] == '+') s = s[1..].Trim();
        bool ok;
        if (s.StartsWith('#')) ok = long.TryParse(s[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        else
        {
            if (s.StartsWith('$')) s = s[1..];
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            ok = s.Length > 0 && s.Length <= 16 && s.All(Uri.IsHexDigit) && ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) && (v = (long)u) == v;
        }
        if (neg) v = -v;
        return ok;
    }

    private static Opnd ParseOperand(string text, ISet<string> symbols)
    {
        var s = text.Trim();
        int size = 0;
        var pm = Regex.Match(s, @"^(byte|word|dword|qword|xmmword|ymmword|tbyte|oword)\s*(ptr)?\s*(?=\[)", RegexOptions.IgnoreCase);
        if (pm.Success) { size = PtrSizes[pm.Groups[1].Value]; s = s[pm.Length..].Trim(); }
        if (s.StartsWith('['))
        {
            if (!s.EndsWith(']')) throw new AsmException($"ongeldige geheugenoperand '{text}'");
            var o = new Opnd { T = OpT.Mem, Size = size };
            var inner = s[1..^1].Replace(" ", "");
            foreach (Match t in Regex.Matches(inner, @"([+-]?)([^+-]+)"))
            {
                bool neg = t.Groups[1].Value == "-";
                var term = t.Groups[2].Value;
                if (term.Contains('*'))
                {
                    var parts = term.Split('*');
                    string rs = parts[0], sc = parts[1];
                    if (!TryRegister(rs, out var ir)) (rs, sc) = (sc, rs);
                    if (!TryRegister(rs, out ir) || !int.TryParse(sc, out var scale) || neg) throw new AsmException($"ongeldige index '{term}'");
                    o.Index = ir; o.Scale = scale;
                }
                else if (TryRegister(term, out var r))
                {
                    if (neg) throw new AsmException($"'-{term}' not allowed");
                    if (o.Base == Register.None) o.Base = r; else if (o.Index == Register.None) o.Index = r; else throw new AsmException($"too many registers in '{text}'");
                }
                else if (symbols.Contains(term) && !neg && o.Sym == null) o.Sym = term;
                else if (TryNumber(term, out var n)) o.Disp += neg ? -n : n;
                else if (!neg && o.Sym == null) o.Sym = term;       // unknown symbol: reported at resolve time
                else throw new AsmException($"onbekende term '{term}' in '{text}'");
            }
            if (o.Sym != null && (o.Base != Register.None || o.Index != Register.None)) throw new AsmException($"symbol + register in one operand is not supported: '{text}'");
            return o;
        }
        if (size != 0) throw new AsmException($"ongeldige operand '{text}'");
        if (TryRegister(s, out var reg)) return new Opnd { T = OpT.Reg, Reg = reg };
        var cast = Regex.Match(s, @"^\((float|double|int)\)\s*(-?[\d.]+(e[+-]?\d+)?)$", RegexOptions.IgnoreCase);
        if (cast.Success)
        {
            var kind = cast.Groups[1].Value.ToLowerInvariant();
            if (kind == "int") return new Opnd { T = OpT.Imm, Imm = long.Parse(cast.Groups[2].Value, CultureInfo.InvariantCulture) };
            double d = double.Parse(cast.Groups[2].Value, CultureInfo.InvariantCulture);
            return new Opnd { T = OpT.Imm, Imm = kind == "float" ? (uint)BitConverter.SingleToInt32Bits((float)d) : BitConverter.DoubleToInt64Bits(d) };
        }
        if (!symbols.Contains(s) && TryNumber(s, out var imm)) return new Opnd { T = OpT.Imm, Imm = imm };
        // symbol (+/- number)
        var sm = Regex.Match(s, @"^([A-Za-z_.@][\w.@]*)\s*([+-]\s*[#$]?[0-9A-Fa-fx]+)?$");
        if (sm.Success)
        {
            long add = 0;
            if (sm.Groups[2].Success && !TryNumber(sm.Groups[2].Value.Replace(" ", ""), out add)) throw new AsmException($"ongeldige operand '{text}'");
            return new Opnd { T = OpT.Imm, ImmSym = sm.Groups[1].Value, Imm = add };
        }
        throw new AsmException($"ongeldige operand '{text}'");
    }

    // ---------------- matching ----------------
    private static bool RegMatches(string k, Register r)
    {
        if (k.EndsWith("_or_mem")) k = k[..^7];
        else if (k.EndsWith("_reg")) k = k[..^4];
        else if (k.EndsWith("_opcode")) k = k[..^7];
        else if (k.EndsWith("_vvvv")) k = k[..^5];
        else if (k.EndsWith("_is4") || k.EndsWith("_is5")) k = k[..^4];
        return k switch
        {
            "r8" => r.IsGPR8(), "r16" => r.IsGPR16(), "r32" => r.IsGPR32(), "r64" => r.IsGPR64(),
            "xmm" => r.IsXMM(), "ymm" => r.IsYMM(),
            "al" => r == Register.AL, "cl" => r == Register.CL, "ax" => r == Register.AX, "dx" => r == Register.DX,
            "eax" => r == Register.EAX, "rax" => r == Register.RAX,
            _ => false,
        };
    }

    private static bool FitsS8(long v) => v >= sbyte.MinValue && v <= sbyte.MaxValue;
    private static bool FitsS32(long v) => v >= int.MinValue && v <= int.MaxValue;

    private static OpKind? ImmKind(string k, long v, int opBits, bool sym)
    {
        // for 32/16/8-bit operations, values like FFFFFFFF are the same as -1
        long sv = opBits switch { 8 => (sbyte)v, 16 => (short)v, 32 => (int)v, _ => v };
        bool fitsOp = opBits switch { 8 => v >= -128 && v <= 0xFF, 16 => v >= -32768 && v <= 0xFFFF, 32 => v >= int.MinValue && v <= uint.MaxValue, _ => true };
        if (sym) return k == "imm64" ? OpKind.Immediate64 : null;
        return k switch
        {
            "imm8" => v >= -128 && v <= 0xFF ? OpKind.Immediate8 : null,
            "imm8sex16" => fitsOp && FitsS8(sv) ? OpKind.Immediate8to16 : null,
            "imm8sex32" => fitsOp && FitsS8(sv) ? OpKind.Immediate8to32 : null,
            "imm8sex64" => FitsS8(v) ? OpKind.Immediate8to64 : null,
            "imm16" => v >= -32768 && v <= 0xFFFF ? OpKind.Immediate16 : null,
            "imm32" => v >= int.MinValue && v <= uint.MaxValue ? OpKind.Immediate32 : null,
            "imm32sex64" => FitsS32(v) ? OpKind.Immediate32to64 : null,
            "imm64" => OpKind.Immediate64,
            "imm8_const_1" => v == 1 ? OpKind.Immediate8 : null,
            _ => null,
        };
    }

    private static int OpBits(OpCodeInfo oc, IList<Opnd> ops)
    {
        foreach (var o in ops) if (o.T == OpT.Reg && o.Reg.IsGPR()) return o.Reg.GetSize() * 8;
        return oc.OperandSize != 0 ? oc.OperandSize : 32;
    }

    private sealed class Line
    {
        public int No; public string Text = ""; public string? Label; public string? Mnem; public List<Opnd> Ops = new();
        public string Kind = "";   // label | insn | data | nop
        public byte[]? Data; public List<(int offset, string sym, long add, int size)>? DataRelocs;
        public List<Code> Candidates = new(); public Code Chosen; public int Size;
        public int Offset;
    }

    public static AsmResult Assemble(IEnumerable<string> source, ulong origin, IReadOnlyDictionary<string, ulong>? externals = null)
    {
        var ext = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        if (externals != null) foreach (var kv in externals) ext[kv.Key] = kv.Value;
        var srcLines = ResolveAnonymousLabels(source.SelectMany(l => l.Split('\n')).ToList());

        // pass 0: collect labels
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<Line>();
        for (int i = 0; i < srcLines.Count; i++)
        {
            var t = Regex.Replace(srcLines[i], @"//.*$", "");
            t = Regex.Replace(t, @"\{[^}]*\}", "").Trim();
            while (true)
            {
                var lm = Regex.Match(t, @"^([A-Za-z_.@][\w.@]*):(?!:)");
                if (!lm.Success) break;
                var name = lm.Groups[1].Value;
                if (!labels.Add(name)) throw new AsmException($"line {i + 1}: label '{name}' defined twice");
                lines.Add(new Line { No = i + 1, Text = t, Label = name, Kind = "label" });
                t = t[lm.Length..].Trim();
            }
            if (t.Length == 0) continue;
            lines.Add(new Line { No = i + 1, Text = t, Kind = "raw" });
        }
        var symbols = new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);
        foreach (var k in ext.Keys) symbols.Add(k);

        // pass 1: parse + choose encodings (sizes independent of addresses)
        int offset = 0;
        foreach (var ln in lines)
        {
            ln.Offset = offset;
            if (ln.Kind == "label") continue;
            try { ParseLine(ln, symbols); }
            catch (AsmException e) { throw new AsmException($"regel {ln.No} '{ln.Text}': {e.Message}"); }
            if (ln.Kind == "insn") ChooseEncoding(ln, origin + (ulong)offset);
            offset += ln.Size;
        }
        // label addresses
        var addr = new Dictionary<string, ulong>(ext, StringComparer.OrdinalIgnoreCase);
        var own = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var ln in lines.Where(l => l.Kind == "label")) { addr[ln.Label!] = origin + (ulong)ln.Offset; own[ln.Label!] = origin + (ulong)ln.Offset; }

        // pass 2: encode with real addresses
        var output = new byte[offset];
        foreach (var ln in lines)
        {
            if (ln.Kind == "label") continue;
            try
            {
                byte[] bytes;
                if (ln.Kind == "insn") bytes = EncodeFinal(ln, origin + (ulong)ln.Offset, addr);
                else
                {
                    bytes = (byte[])ln.Data!.Clone();
                    foreach (var (o, sym, add, sz) in ln.DataRelocs ?? new())
                    {
                        if (!addr.TryGetValue(sym, out var a)) throw new AsmException($"unknown symbol '{sym}'");
                        ulong v = a + (ulong)add;
                        if (sz == 4 && v > uint.MaxValue) throw new AsmException($"address of '{sym}' does not fit in dd");
                        BitConverter.GetBytes(v).AsSpan(0, sz).CopyTo(bytes.AsSpan(o));
                    }
                }
                if (bytes.Length != ln.Size) throw new AsmException($"internal error: length {bytes.Length} != {ln.Size}");
                bytes.CopyTo(output, ln.Offset);
            }
            catch (AsmException e) when (!e.Message.StartsWith("regel ")) { throw new AsmException($"regel {ln.No} '{ln.Text}': {e.Message}"); }
        }
        return new AsmResult { Code = output, Labels = own };
    }

    private static void ParseLine(Line ln, ISet<string> symbols)
    {
        var t = ln.Text;
        var m = Regex.Match(t, @"^(\S+)\s*(.*)$");
        var mn = m.Groups[1].Value.ToLowerInvariant();
        var rest = m.Groups[2].Value.Trim();
        switch (mn)
        {
            case "db": case "dw": case "dd": case "dq":
            {
                int sz = mn switch { "db" => 1, "dw" => 2, "dd" => 4, _ => 8 };
                var data = new List<byte>(); var rel = new List<(int, string, long, int)>();
                // CE accepts both "dd 1,2,3" and "dd (float)1 (float)2" (whitespace-separated)
                var items = !rest.Contains(',') ? Regex.Split(rest.Trim(), @"(?<!\))\s+(?!\()|(?<=\S)\s+(?=\()").Where(x => x.Length > 0).ToArray() : SplitOperands(rest).ToArray();
                foreach (var raw in items)
                {
                    var it = raw.Trim();
                    if (it.Length == 0) continue;
                    if (mn == "db" && it.StartsWith("'") && it.EndsWith("'") && it.Length >= 2) { data.AddRange(System.Text.Encoding.ASCII.GetBytes(it[1..^1])); continue; }
                    var fm = Regex.Match(it, @"^\((float|double)\)\s*(-?[\d.]+(e[+-]?\d+)?)$", RegexOptions.IgnoreCase);
                    if (fm.Success)
                    {
                        double d = double.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture);
                        if (fm.Groups[1].Value.ToLower() == "float") { if (sz != 4) throw new AsmException("(float) only in dd"); data.AddRange(BitConverter.GetBytes((float)d)); }
                        else { if (sz != 8) throw new AsmException("(double) only in dq"); data.AddRange(BitConverter.GetBytes(d)); }
                        continue;
                    }
                    if (!symbols.Contains(it) && TryNumber(it, out var v)) { data.AddRange(BitConverter.GetBytes(v).AsSpan(0, sz).ToArray()); continue; }
                    if (sz >= 4 && Regex.IsMatch(it, @"^[A-Za-z_.@][\w.@]*$")) { rel.Add((data.Count, it, 0, sz)); data.AddRange(new byte[sz]); continue; }
                    throw new AsmException($"ongeldige waarde '{it}'");
                }
                ln.Kind = "data"; ln.Data = data.ToArray(); ln.DataRelocs = rel; ln.Size = ln.Data.Length;
                return;
            }
            case "nop" when rest.Length > 0:
            {
                if (!TryNumber(rest, out var n) || n < 1 || n > 4096) throw new AsmException("nop N: invalid count");
                ln.Kind = "data"; ln.Data = Enumerable.Repeat((byte)0x90, (int)n).ToArray(); ln.Size = (int)n; return;
            }
            case "alloc": case "label": case "registersymbol": case "unregistersymbol": case "dealloc": case "aobscanmodule": case "aobscan": case "define": case "assert": case "globalalloc": case "readmem": case "reassemble": case "createthread": case "loadlibrary":
                throw new AsmException($"AA command '{mn}' is not supported here (use the fields of the definition)");
        }
        ln.Kind = "insn";
        ln.Mnem = mn;
        // CE/MASM size hints on branch targets: "jne short label", "jmp near label"
        rest = Regex.Replace(rest, @"^(short|near)\s+(?=[A-Za-z_.@])", "", RegexOptions.IgnoreCase);
        var opsText = rest.Length == 0 ? new List<string>() : SplitOperands(rest).ToList();
        // CE shorthand "imul r32,imm" = "imul r32,r32,imm"
        if (mn == "imul" && opsText.Count == 2 && TryRegister(opsText[0].Trim(), out _) && !TryRegister(opsText[1].Trim(), out _) && !opsText[1].Contains('['))
            opsText.Insert(1, opsText[0]);
        ln.Ops = opsText.Select(o => ParseOperand(o, symbols)).ToList();
    }

    /// <summary>CE anonymous labels: "@@:" defines one, "@f"/"@F" refers to the next, "@b"/"@B" to the previous.</summary>
    private static List<string> ResolveAnonymousLabels(List<string> src)
    {
        if (!src.Any(l => l.Contains("@@"))) return src;
        var defs = new List<int>();
        for (int i = 0; i < src.Count; i++) if (Regex.IsMatch(Regex.Replace(src[i], @"//.*$", ""), @"^\s*@@:")) defs.Add(i);
        var outp = new List<string>(src.Count);
        for (int i = 0; i < src.Count; i++)
        {
            var l = src[i];
            int idx = defs.IndexOf(i);
            if (idx >= 0) l = Regex.Replace(l, @"^(\s*)@@:", $"$1__anon{idx}:");
            l = Regex.Replace(l, @"(?<![\w@])@[fF]\b", _ => { var n = defs.FindIndex(d => d > i); if (n < 0) throw new AsmException($"regel {i + 1}: @f zonder volgend @@"); return $"__anon{n}"; });
            l = Regex.Replace(l, @"(?<![\w@])@[bB]\b", _ => { var n = defs.FindLastIndex(d => d < i || (d == i && idx < 0)); if (n < 0) throw new AsmException($"regel {i + 1}: @b zonder vorig @@"); return $"__anon{n}"; });
            outp.Add(l);
        }
        return outp;
    }

    private static IEnumerable<string> SplitOperands(string s)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '[' || s[i] == '(') depth++;
            else if (s[i] == ']' || s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    private static Mnemonic ResolveMnemonic(string mn)
    {
        if (Aliases.TryGetValue(mn, out var a)) return a;
        if (Enum.TryParse<Mnemonic>(mn, true, out var m) && m != Mnemonic.INVALID) return m;
        throw new AsmException($"onbekende instructie '{mn}'");
    }

    private static bool TryBuild(Code code, IList<Opnd> ops, ulong ip, IReadOnlyDictionary<string, ulong>? addr, out Instruction ins, out string? why)
    {
        ins = default; why = null;
        var oc = code.ToOpCode();
        if (oc.OpCount != ops.Count) { why = "aantal operanden"; return false; }
        ins.Code = code;
        int bits = OpBits(oc, ops);
        for (int i = 0; i < ops.Count; i++)
        {
            var k = oc.GetOpKind(i).ToString();
            var o = ops[i];
            switch (o.T)
            {
                case OpT.Reg:
                    if (!(k.EndsWith("_or_mem") || k.EndsWith("_reg") || k.EndsWith("_opcode") || (k.EndsWith("_vvvv") && oc.Encoding == EncodingKind.VEX) || k is "al" or "cl" or "ax" or "dx" or "eax" or "rax") || !RegMatches(k, o.Reg)) { why = "register"; return false; }
                    ins.SetOpKind(i, OpKind.Register); ins.SetOpRegister(i, o.Reg);
                    break;
                case OpT.Mem:
                    if (!(k.EndsWith("_or_mem") || k == "mem" || k.StartsWith("mem_") && !k.StartsWith("mem_offs"))) { why = "geheugen"; return false; }
                    ins.SetOpKind(i, OpKind.Memory);
                    if (o.Sym != null)
                    {
                        ulong target = ip;   // pass 1 placeholder (rel32 either way)
                        if (addr != null) { if (!addr.TryGetValue(o.Sym, out target)) throw new AsmException($"unknown symbol '{o.Sym}'"); }
                        ins.MemoryBase = Register.RIP; ins.MemoryDisplacement64 = target + (ulong)o.Disp; ins.MemoryDisplSize = 8;
                    }
                    else
                    {
                        ins.MemoryBase = o.Base; ins.MemoryIndex = o.Index; ins.MemoryIndexScale = o.Scale;
                        ins.MemoryDisplacement64 = (ulong)o.Disp;
                        ins.MemoryDisplSize = o.Disp == 0 ? 0 : FitsS8(o.Disp) ? 1 : 8;
                    }
                    break;
                case OpT.Imm:
                    if (k == "br64_4")
                    {
                        ulong target = ip + 16;
                        if (addr != null)
                        {
                            if (o.ImmSym == null) target = (ulong)o.Imm;
                            else if (!addr.TryGetValue(o.ImmSym, out target)) throw new AsmException($"unknown label '{o.ImmSym}'");
                            else target += (ulong)o.Imm;
                        }
                        ins.SetOpKind(i, OpKind.NearBranch64); ins.NearBranch64 = target;
                        break;
                    }
                    var kind = ImmKind(k, o.Imm, bits, o.ImmSym != null);
                    if (kind == null) { why = "immediate"; return false; }
                    ins.SetOpKind(i, kind.Value);
                    long val = o.Imm;
                    if (o.ImmSym != null && addr != null) { if (!addr.TryGetValue(o.ImmSym, out var a)) throw new AsmException($"unknown symbol '{o.ImmSym}'"); val = (long)(a + (ulong)o.Imm); }
                    long sv = kind switch
                    {
                        OpKind.Immediate8to32 or OpKind.Immediate8to16 => (sbyte)val,
                        OpKind.Immediate8 => (byte)val,
                        OpKind.Immediate16 => (ushort)val,
                        OpKind.Immediate32 => (uint)val,
                        _ => val,
                    };
                    ins.SetImmediate(i, sv);
                    break;
            }
        }
        // explicit ptr size must match the instruction's memory size
        foreach (var o in ops)
        {
            if (o.T != OpT.Mem || o.Size == 0) continue;
            int ms = ins.MemorySize.GetSize();
            if (ms != 0 && ms != o.Size) { why = "ptr-grootte"; return false; }
        }
        return true;
    }

    private sealed class ByteWriter : CodeWriter { public readonly List<byte> B = new(); public override void WriteByte(byte value) => B.Add(value); }

    private static byte[]? TryEncode(in Instruction ins, ulong ip, out string? err)
    {
        var w = new ByteWriter();
        var enc = Encoder.Create(64, w);
        if (!enc.TryEncode(ins, ip, out _, out err)) return null;
        return w.B.ToArray();
    }

    private static void ChooseEncoding(Line ln, ulong ip)
    {
        var mn = ResolveMnemonic(ln.Mnem!);
        if (!Index.Value.TryGetValue(mn, out var codes)) throw new AsmException($"instruction '{ln.Mnem}' not available in 64-bit");
        var ok = new List<(Code code, int len, int memSize)>();
        string? lastWhy = null, encErr = null;
        foreach (var c in codes)
        {
            if (!TryBuild(c, ln.Ops, ip, null, out var ins, out var why)) { lastWhy = why ?? lastWhy; continue; }
            var b = TryEncode(ins, ip, out var err);
            if (b == null) { encErr = err; continue; }
            ok.Add((c, b.Length, ln.Ops.Any(o => o.T == OpT.Mem) ? ins.MemorySize.GetSize() : 0));
        }
        if (ok.Count == 0) throw new AsmException($"no valid encoding for '{ln.Text}' ({encErr ?? lastWhy})");
        // ambiguous memory size (e.g. "mov [x],0"): CE defaults to dword
        var mem = ln.Ops.FirstOrDefault(o => o.T == OpT.Mem);
        if (mem != null && mem.Size == 0 && !ln.Ops.Any(o => o.T == OpT.Reg))
        {
            var sizes = ok.Select(x => x.memSize).Distinct().ToList();
            if (sizes.Count > 1) ok = ok.Where(x => x.memSize == (sizes.Contains(4) ? 4 : sizes.Max())).ToList();
        }
        var best = ok.OrderBy(x => x.len).First();
        ln.Chosen = best.code; ln.Size = best.len;
    }

    private static byte[] EncodeFinal(Line ln, ulong ip, IReadOnlyDictionary<string, ulong> addr)
    {
        if (!TryBuild(ln.Chosen, ln.Ops, ip, addr, out var ins, out var why)) throw new AsmException($"encoding failed ({why})");
        var b = TryEncode(ins, ip, out var err) ?? throw new AsmException($"encoding failed: {err} (target too far for rel32?)");
        return b;
    }

    /// <summary>jmp rel32 from <paramref name="from"/> to <paramref name="to"/>, padded with NOPs to <paramref name="length"/> (≥5).</summary>
    public static byte[] JmpRel32(ulong from, ulong to, int length = 5)
    {
        if (length < 5) throw new AsmException("at least 5 bytes needed for a jmp");
        long rel = (long)to - (long)(from + 5);
        if (rel < int.MinValue || rel > int.MaxValue) throw new AsmException("target too far for jmp rel32 (±2 GB)");
        var b = new byte[length];
        b[0] = 0xE9;
        BitConverter.GetBytes((int)rel).CopyTo(b, 1);
        for (int i = 5; i < length; i++) b[i] = 0x90;
        return b;
    }

    /// <summary>14-byte absolute jmp: jmp [rip+0]; dq target.</summary>
    public static byte[] JmpAbs64(ulong to, int length = 14)
    {
        if (length < 14) throw new AsmException("at least 14 bytes needed for an absolute jmp");
        var b = new byte[length];
        b[0] = 0xFF; b[1] = 0x25;
        BitConverter.GetBytes(to).CopyTo(b, 6);
        for (int i = 14; i < length; i++) b[i] = 0x90;
        return b;
    }
}
