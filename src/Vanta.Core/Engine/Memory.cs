namespace Vanta.Core.Engine;

public sealed record ModuleInfo(string Name, ulong Base, uint Size, string? Path);
public readonly record struct MemRegion(ulong Start, ulong Size, bool Executable, bool Readable);

/// <summary>Access to another process' memory. Windows: <see cref="Vanta.Core.Win.WinProcessMemory"/>; tests: <see cref="FakeProcess"/>.</summary>
public interface IProcessMemory : IDisposable
{
    int ProcessId { get; }
    bool IsAlive { get; }
    IReadOnlyList<ModuleInfo> GetModules();
    IEnumerable<MemRegion> Regions(ulong from, ulong to);
    bool Read(ulong address, Span<byte> buffer);
    /// <summary>Writes (temporarily making the page writable) and flushes the instruction cache.</summary>
    bool Write(ulong address, ReadOnlySpan<byte> data);
    /// <summary>Allocates RWX memory within ±2 GB of <paramref name="near"/> (0 = anywhere). Returns 0 on failure.</summary>
    ulong Alloc(uint size, ulong near);
    void Free(ulong address);
}

public static class MemoryExtensions
{
    public static ModuleInfo? FindModule(this IProcessMemory m, string name) =>
        m.GetModules().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    public static byte[]? ReadBytes(this IProcessMemory m, ulong addr, int n)
    {
        var b = new byte[n];
        return m.Read(addr, b) ? b : null;
    }
    public static bool TryReadU64(this IProcessMemory m, ulong addr, out ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        v = 0;
        if (!m.Read(addr, b)) return false;
        v = BitConverter.ToUInt64(b);
        return true;
    }
    public static bool TryReadI32(this IProcessMemory m, ulong addr, out int v)
    {
        Span<byte> b = stackalloc byte[4];
        v = 0;
        if (!m.Read(addr, b)) return false;
        v = BitConverter.ToInt32(b);
        return true;
    }

    /// <summary>PE header of a loaded module: (TimeDateStamp, SizeOfImage), read from process memory.</summary>
    public static (uint timestamp, uint sizeOfImage)? ReadPeInfo(this IProcessMemory m, ModuleInfo mod)
    {
        var dos = m.ReadBytes(mod.Base, 0x40);
        if (dos == null || dos[0] != 'M' || dos[1] != 'Z') return null;
        int lfanew = BitConverter.ToInt32(dos, 0x3C);
        var nt = m.ReadBytes(mod.Base + (ulong)lfanew, 0x58);
        if (nt == null || nt[0] != 'P' || nt[1] != 'E') return null;
        return (BitConverter.ToUInt32(nt, 8), BitConverter.ToUInt32(nt, 0x50));
    }
}
