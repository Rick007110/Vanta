using System.IO.MemoryMappedFiles;

namespace Vanta.Core.Engine;

/// <summary>Read-only "process" made of PE files on disk, each mapped at its own base like the loader would
/// (sections at their RVAs). Lets the verifier run the exact same AOB/site resolution as the live engine
/// without the game running. Writes and allocations always fail.</summary>
public sealed class PeFileMemory : IProcessMemory
{
    private sealed class Section { public ulong Va; public uint VSize; public long Raw; public uint RawSize; public bool Exec; }
    private sealed class Image
    {
        public ModuleInfo Mod = null!;
        public MemoryMappedFile Map = null!;
        public MemoryMappedViewAccessor View = null!;
        public long FileLen;
        public uint HeaderSize;
        public List<Section> Sections = new();
    }

    private readonly List<Image> _images = new();
    public int ProcessId => 0;
    public bool IsAlive => true;
    public IReadOnlyList<ModuleInfo> GetModules() => _images.Select(i => i.Mod).ToList();

    /// <summary>Maps the file at the next free base (1 GiB aligned slots from 0x140000000).</summary>
    public ModuleInfo Add(string path, string? asName = null)
    {
        var fi = new FileInfo(path);
        var map = MemoryMappedFile.CreateFromFile(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var img = new Image { Map = map, View = view, FileLen = fi.Length };
        try
        {
            if (view.ReadUInt16(0) != 0x5A4D) throw new InvalidDataException("not a PE file (MZ missing)");
            int pe = view.ReadInt32(0x3C);
            if (view.ReadUInt32(pe) != 0x4550) throw new InvalidDataException("not a PE file (PE missing)");
            int nSec = view.ReadUInt16(pe + 6);
            int optSize = view.ReadUInt16(pe + 20);
            int opt = pe + 24;
            uint sizeOfImage = view.ReadUInt32(opt + 56);
            img.HeaderSize = view.ReadUInt32(opt + 60);
            ulong slot = 0x1_4000_0000UL + (ulong)_images.Count * 0x4000_0000UL;
            if (_images.Count > 0) { var last = _images[^1].Mod; slot = Math.Max(slot, (last.Base + last.Size + 0x3FFF_FFFFUL) & ~0x3FFF_FFFFUL); }
            int st = opt + optSize;
            for (int i = 0; i < nSec; i++)
            {
                int s = st + i * 40;
                img.Sections.Add(new Section
                {
                    Va = slot + view.ReadUInt32(s + 12), VSize = view.ReadUInt32(s + 8),
                    RawSize = view.ReadUInt32(s + 16), Raw = view.ReadUInt32(s + 20),
                    Exec = (view.ReadUInt32(s + 36) & 0x20000000) != 0,
                });
            }
            img.Mod = new ModuleInfo(asName ?? fi.Name, slot, sizeOfImage, fi.FullName);
        }
        catch { view.Dispose(); map.Dispose(); throw; }
        _images.Add(img);
        return img.Mod;
    }

    public IEnumerable<MemRegion> Regions(ulong from, ulong to)
    {
        foreach (var img in _images)
        {
            var b = img.Mod.Base;
            if (b + img.HeaderSize > from && b < to) yield return new MemRegion(b, img.HeaderSize, false, true);
            foreach (var s in img.Sections.OrderBy(x => x.Va))
            {
                ulong size = Math.Max(s.VSize, s.RawSize);
                if (s.Va + size > from && s.Va < to) yield return new MemRegion(s.Va, size, s.Exec, true);
            }
        }
    }

    public bool Read(ulong address, Span<byte> buffer)
    {
        var img = _images.FirstOrDefault(i => address >= i.Mod.Base && address < i.Mod.Base + i.Mod.Size);
        if (img == null) return false;
        int done = 0;
        while (done < buffer.Length)
        {
            ulong a = address + (ulong)done;
            long fileOff; long avail; bool zero = false;
            var s = img.Sections.FirstOrDefault(x => a >= x.Va && a < x.Va + Math.Max(x.VSize, x.RawSize));
            if (s != null)
            {
                ulong rel = a - s.Va;
                if (rel < s.RawSize) { fileOff = s.Raw + (long)rel; avail = s.RawSize - (long)rel; }
                else { fileOff = 0; avail = Math.Max(s.VSize, s.RawSize) - (long)rel; zero = true; }
            }
            else if (a - img.Mod.Base < img.HeaderSize) { fileOff = (long)(a - img.Mod.Base); avail = img.HeaderSize - fileOff; }
            else return false;
            int n = (int)Math.Min(avail, buffer.Length - done);
            if (n <= 0) return false;
            var dst = buffer.Slice(done, n);
            if (zero) dst.Clear();
            else
            {
                if (fileOff + n > img.FileLen) return false;
                ReadView(img.View, fileOff, dst);
            }
            done += n;
        }
        return true;
    }

    private static unsafe void ReadView(MemoryMappedViewAccessor v, long off, Span<byte> dst)
    {
        byte* p = null;
        v.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try { new ReadOnlySpan<byte>(p + v.PointerOffset + off, dst.Length).CopyTo(dst); }
        finally { v.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    public bool Write(ulong address, ReadOnlySpan<byte> data) => false;
    public ulong Alloc(uint size, ulong near) => 0;
    public void Free(ulong address) { }
    public void Dispose() { foreach (var i in _images) { i.View.Dispose(); i.Map.Dispose(); } _images.Clear(); }
}
