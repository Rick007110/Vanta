using System.Runtime.Versioning;
using Microsoft.Win32;
using Vanta.Core.Stores;

namespace Vanta.Core.Win;

/// <summary>Real registry + file system for the store providers (64-bit registry view; WOW6432Node paths are explicit).</summary>
[SupportedOSPlatform("windows")]
public sealed class WinStoreEnv : IStoreEnv
{
    private static RegistryKey? Open(string keyPath)
    {
        var i = keyPath.IndexOf('\\');
        var hive = i < 0 ? keyPath : keyPath[..i];
        var sub = i < 0 ? "" : keyPath[(i + 1)..];
        var root = hive.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException("hive: " + hive),
        };
        var b = RegistryKey.OpenBaseKey(root, RegistryView.Registry64);
        return sub.Length == 0 ? b : b.OpenSubKey(sub, false);
    }

    public string? RegValue(string keyPath, string name)
    {
        try { using var k = Open(keyPath); return k?.GetValue(name)?.ToString(); } catch { return null; }
    }
    public IReadOnlyList<string> RegSubKeys(string keyPath)
    {
        try { using var k = Open(keyPath); return k?.GetSubKeyNames() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }
    public bool FileExists(string path) { try { return File.Exists(path); } catch { return false; } }
    public bool DirExists(string path) { try { return Directory.Exists(path); } catch { return false; } }
    public string? ReadText(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 16 << 20 ? File.ReadAllText(path) : null; } catch { return null; }
    }
    public IReadOnlyList<string> Files(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }
    public IReadOnlyList<string> SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }
    public string? Folder(string name) => name switch
    {
        "ProgramData" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ProgramFiles" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "ProgramFiles(x86)" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "LocalAppData" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        _ => null,
    };
}
