using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vanta.Core.Engine;

namespace Vanta.Core.Win;

internal static class Native
{
    public const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_QUERY_INFORMATION = 0x0400, SYNCHRONIZE = 0x00100000;
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, MEM_FREE = 0x10000;
    public const uint PAGE_NOACCESS = 0x01, PAGE_EXECUTE_READWRITE = 0x40, PAGE_GUARD = 0x100;
    public const uint TH32CS_SNAPMODULE = 0x8, TH32CS_SNAPMODULE32 = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress, AllocationBase; public uint AllocationProtect, __alignment1;
        public ulong RegionSize; public uint State, Protect, Type, __alignment2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MODULEENTRY32W
    {
        public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
        public IntPtr modBaseAddr; public uint modBaseSize; public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] public static extern unsafe bool ReadProcessMemory(IntPtr h, ulong addr, byte* buf, nuint size, out nuint read);
    [DllImport("kernel32", SetLastError = true)] public static extern unsafe bool WriteProcessMemory(IntPtr h, ulong addr, byte* buf, nuint size, out nuint written);
    [DllImport("kernel32", SetLastError = true)] public static extern bool VirtualProtectEx(IntPtr h, ulong addr, nuint size, uint prot, out uint old);
    [DllImport("kernel32", SetLastError = true)] public static extern nuint VirtualQueryEx(IntPtr h, ulong addr, out MEMORY_BASIC_INFORMATION64 mbi, nuint len);
    [DllImport("kernel32", SetLastError = true)] public static extern ulong VirtualAllocEx(IntPtr h, ulong addr, nuint size, uint type, uint prot);
    [DllImport("kernel32", SetLastError = true)] public static extern bool VirtualFreeEx(IntPtr h, ulong addr, nuint size, uint type);
    [DllImport("kernel32", SetLastError = true)] public static extern bool FlushInstructionCache(IntPtr h, ulong addr, nuint size);
    [DllImport("kernel32", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32", SetLastError = true)] public static extern bool IsWow64Process(IntPtr h, out bool wow);
    [DllImport("kernel32", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W e);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W e);
}

/// <summary>Windows implementation of <see cref="IProcessMemory"/> (x64 targets).</summary>
public sealed class WinProcessMemory : IProcessMemory
{
    private IntPtr _h;
    public int ProcessId { get; }
    private List<ModuleInfo>? _mods; private DateTime _modsAt;

    public WinProcessMemory(int pid)
    {
        ProcessId = pid;
        _h = Native.OpenProcess(Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION | Native.SYNCHRONIZE, false, pid);
        if (_h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (Environment.Is64BitOperatingSystem && Native.IsWow64Process(_h, out var wow) && wow)
        {
            Native.CloseHandle(_h); _h = IntPtr.Zero;
            throw new InvalidOperationException(Strings.Get("x86.unsupported"));
        }
    }

    public bool IsAlive => _h != IntPtr.Zero && Native.WaitForSingleObject(_h, 0) == 0x102; // WAIT_TIMEOUT = still running

    public IReadOnlyList<ModuleInfo> GetModules()
    {
        if (_mods != null && (DateTime.UtcNow - _modsAt).TotalSeconds < 5) return _mods;
        var list = new List<ModuleInfo>();
        var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, ProcessId);
        if (snap == new IntPtr(-1)) return list;
        try
        {
            var e = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
            if (Native.Module32FirstW(snap, ref e))
                do list.Add(new ModuleInfo(e.szModule, (ulong)e.modBaseAddr, e.modBaseSize, e.szExePath));
                while (Native.Module32NextW(snap, ref e));
        }
        finally { Native.CloseHandle(snap); }
        _mods = list; _modsAt = DateTime.UtcNow;
        return list;
    }

    public IEnumerable<MemRegion> Regions(ulong from, ulong to)
    {
        ulong addr = from;
        var size = (nuint)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION64>();
        while (addr < to)
        {
            if (Native.VirtualQueryEx(_h, addr, out var mbi, size) == 0) yield break;
            ulong next = mbi.BaseAddress + mbi.RegionSize;
            if (mbi.State == Native.MEM_COMMIT)
            {
                bool readable = (mbi.Protect & 0xFF) != Native.PAGE_NOACCESS && (mbi.Protect & Native.PAGE_GUARD) == 0;
                bool exec = (mbi.Protect & 0xF0) != 0;
                yield return new MemRegion(mbi.BaseAddress, mbi.RegionSize, exec, readable);
            }
            if (next <= addr) yield break;
            addr = next;
        }
    }

    public unsafe bool Read(ulong address, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return Native.ReadProcessMemory(_h, address, p, (nuint)buffer.Length, out var n) && n == (nuint)buffer.Length;
    }

    public unsafe bool Write(ulong address, ReadOnlySpan<byte> data)
    {
        if (!Native.VirtualProtectEx(_h, address, (nuint)data.Length, Native.PAGE_EXECUTE_READWRITE, out var old)) return false;
        bool ok;
        fixed (byte* p = data) ok = Native.WriteProcessMemory(_h, address, p, (nuint)data.Length, out var n) && n == (nuint)data.Length;
        Native.VirtualProtectEx(_h, address, (nuint)data.Length, old, out _);
        Native.FlushInstructionCache(_h, address, (nuint)data.Length);
        return ok;
    }

    public ulong Alloc(uint size, ulong near)
    {
        const ulong gran = 0x10000, range = 0x7FF00000;
        if (near == 0) return Native.VirtualAllocEx(_h, 0, size, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
        ulong lo = near > range ? near - range : gran, hi = near + range;
        var mbiSize = (nuint)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION64>();
        // upward from near
        for (ulong a = (near + gran - 1) & ~(gran - 1); a < hi;)
        {
            if (Native.VirtualQueryEx(_h, a, out var mbi, mbiSize) == 0) break;
            if (mbi.State == Native.MEM_FREE)
            {
                ulong start = (Math.Max(a, mbi.BaseAddress) + gran - 1) & ~(gran - 1);
                if (start + size <= mbi.BaseAddress + mbi.RegionSize)
                {
                    var r = Native.VirtualAllocEx(_h, start, size, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
                    if (r != 0) return r;
                }
            }
            ulong next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= a) break;
            a = next;
        }
        // downward
        for (ulong a = near & ~(gran - 1); a > lo;)
        {
            if (Native.VirtualQueryEx(_h, a, out var mbi, mbiSize) == 0) break;
            if (mbi.State == Native.MEM_FREE)
            {
                ulong end = Math.Min(a + gran, mbi.BaseAddress + mbi.RegionSize);
                if (end >= size)
                {
                    ulong start = (end - size) & ~(gran - 1);
                    if (start >= mbi.BaseAddress && start >= lo)
                    {
                        var r = Native.VirtualAllocEx(_h, start, size, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
                        if (r != 0) return r;
                    }
                }
            }
            if (mbi.AllocationBase != 0 && mbi.State != Native.MEM_FREE) a = mbi.AllocationBase - gran;
            else a = mbi.BaseAddress > gran ? mbi.BaseAddress - gran : 0;
        }
        return 0;
    }

    public void Free(ulong address) => Native.VirtualFreeEx(_h, address, 0, Native.MEM_RELEASE);

    public void Dispose()
    {
        if (_h != IntPtr.Zero) { Native.CloseHandle(_h); _h = IntPtr.Zero; }
    }
}

public sealed class WinProcessProvider : IProcessProvider
{
    public IReadOnlyList<ProcInfo> List()
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add(new ProcInfo(p.Id, p.ProcessName + ".exe")); } catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    public IProcessMemory Open(int pid)
    {
        try { return new WinProcessMemory(pid); }
        catch (Win32Exception e) { throw new InvalidOperationException($"Win32-fout {e.NativeErrorCode}: {e.Message}"); }
    }

    public (string? fileVersion, string? productVersion) FileVersion(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return (null, null);
            var v = FileVersionInfo.GetVersionInfo(path);
            return (v.FileVersion, v.ProductVersion);
        }
        catch { return (null, null); }
    }
}
