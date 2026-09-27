namespace Vanta.Core.Engine;

/// <summary>In-memory process for tests and demo mode: modules/segments are byte arrays at fixed addresses.</summary>
public sealed class FakeProcess : IProcessMemory
{
    private sealed class Seg { public ulong Start; public byte[] Data = Array.Empty<byte>(); public bool Exec; public bool Readable = true; public bool Allocated; }
    private readonly List<Seg> _segs = new();
    private readonly List<ModuleInfo> _mods = new();
    public int ProcessId { get; set; } = 4242;
    public bool IsAlive { get; set; } = true;
    public int WriteCount { get; private set; }
    public List<(ulong addr, byte[] data)> WriteLog { get; } = new();
    public ulong AllocCursorOverride { get; set; }

    public ModuleInfo AddModule(string name, ulong baseAddr, byte[] image, bool executable = true)
    {
        _segs.Add(new Seg { Start = baseAddr, Data = image, Exec = executable });
        var m = new ModuleInfo(name, baseAddr, (uint)image.Length, "C:\\Fake\\" + name);
        _mods.Add(m);
        return m;
    }
    public void AddRegion(ulong start, byte[] data, bool exec = false, bool readable = true) =>
        _segs.Add(new Seg { Start = start, Data = data, Exec = exec, Readable = readable });

    public IReadOnlyList<ModuleInfo> GetModules() => _mods;

    public IEnumerable<MemRegion> Regions(ulong from, ulong to) =>
        _segs.Where(s => s.Start < to && s.Start + (ulong)s.Data.Length > from).OrderBy(s => s.Start)
             .Select(s => new MemRegion(s.Start, (ulong)s.Data.Length, s.Exec, s.Readable)).ToList();

    private Seg? Find(ulong addr, int len) =>
        _segs.FirstOrDefault(s => addr >= s.Start && addr + (ulong)len <= s.Start + (ulong)s.Data.Length);

    public bool Read(ulong address, Span<byte> buffer)
    {
        if (!IsAlive) return false;
        var s = Find(address, buffer.Length);
        if (s == null || !s.Readable) return false;
        s.Data.AsSpan((int)(address - s.Start), buffer.Length).CopyTo(buffer);
        return true;
    }

    public bool Write(ulong address, ReadOnlySpan<byte> data)
    {
        if (!IsAlive) return false;
        var s = Find(address, data.Length);
        if (s == null) return false;
        data.CopyTo(s.Data.AsSpan((int)(address - s.Start)));
        WriteCount++;
        WriteLog.Add((address, data.ToArray()));
        return true;
    }

    public ulong Alloc(uint size, ulong near)
    {
        if (!IsAlive) return 0;
        // place after the highest segment near "near" (keeps rel32 reachable, like VirtualAllocEx near a module)
        ulong baseAddr = AllocCursorOverride != 0 ? AllocCursorOverride
            : (_segs.Where(s => near == 0 || (long)(s.Start - near) is > -0x7000_0000 and < 0x7000_0000).Select(s => s.Start + (ulong)s.Data.Length).DefaultIfEmpty(0x10000000UL).Max() + 0xFFFF) & ~0xFFFFUL;
        _segs.Add(new Seg { Start = baseAddr, Data = new byte[size], Exec = true, Allocated = true });
        if (AllocCursorOverride != 0) AllocCursorOverride += 0x10000;
        return baseAddr;
    }

    public void Free(ulong address) => _segs.RemoveAll(s => s.Allocated && s.Start == address);
    public int AllocatedCount => _segs.Count(s => s.Allocated);
    public byte[] Peek(ulong addr, int n) { var b = new byte[n]; Read(addr, b); return b; }
    public void Dispose() { }
}
