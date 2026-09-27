namespace Vanta.Core.Stores;

/// <summary>Everything a store provider may look at. Windows implementation in Win/WinStoreEnv.cs; tests use FakeStoreEnv.
/// Registry paths are "HKLM\SOFTWARE\..." / "HKCU\Software\..." (64-bit view; WOW6432Node spelled out explicitly).</summary>
public interface IStoreEnv
{
    string? RegValue(string keyPath, string name);
    IReadOnlyList<string> RegSubKeys(string keyPath);
    bool FileExists(string path);
    bool DirExists(string path);
    string? ReadText(string path);
    IReadOnlyList<string> Files(string dir, string pattern);
    IReadOnlyList<string> SubDirs(string dir);
    /// <summary>Known folders: "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "LocalAppData".</summary>
    string? Folder(string name);
}

/// <summary>In-memory environment for unit tests and fixtures (paths are case-insensitive, '/' or '\').</summary>
public sealed class FakeStoreEnv : IStoreEnv
{
    private static string N(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    public readonly Dictionary<string, Dictionary<string, string>> Reg = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> FilesMap = new();
    public readonly HashSet<string> Dirs = new();
    public readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase);

    public FakeStoreEnv Key(string key, params (string name, string value)[] values)
    {
        key = key.TrimEnd('\\');
        if (!Reg.TryGetValue(key, out var d)) Reg[key] = d = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in values) d[n] = v;
        return this;
    }
    public FakeStoreEnv File(string path, string content = "")
    {
        FilesMap[N(path)] = content;
        for (var d = WinPath.Parent(path); !string.IsNullOrEmpty(d); d = WinPath.Parent(d)) Dirs.Add(N(d));
        return this;
    }
    public FakeStoreEnv Dir(string path) { Dirs.Add(N(path)); return this; }

    public string? RegValue(string keyPath, string name) => Reg.TryGetValue(keyPath.TrimEnd('\\'), out var d) && d.TryGetValue(name, out var v) ? v : null;
    public IReadOnlyList<string> RegSubKeys(string keyPath)
    {
        var prefix = keyPath.TrimEnd('\\') + "\\";
        return Reg.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Substring(prefix.Length).Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public bool FileExists(string path) => FilesMap.ContainsKey(N(path));
    public bool DirExists(string path) => Dirs.Contains(N(path));
    public string? ReadText(string path) => FilesMap.TryGetValue(N(path), out var s) ? s : null;
    public IReadOnlyList<string> Files(string dir, string pattern)
    {
        var d = N(dir) + "\\";
        var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern.ToLowerInvariant()).Replace("\\*", "[^\\\\]*").Replace("\\?", ".") + "$");
        return FilesMap.Keys.Where(k => k.StartsWith(d) && rx.IsMatch(k.Substring(d.Length))).ToList();
    }
    public IReadOnlyList<string> SubDirs(string dir)
    {
        var d = N(dir) + "\\";
        return Dirs.Where(x => x.StartsWith(d) && x.Length > d.Length && !x.Substring(d.Length).Contains('\\')).ToList();
    }
    public string? Folder(string name) => Folders.TryGetValue(name, out var v) ? v : null;
}
