using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vanta.Core.Update;

/// <summary>Semantic version (major.minor.patch[-pre][+build]); a leading "v" is accepted. Build metadata is ignored.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre = null) : IComparable<SemVer>
{
    private static readonly Regex Rx = new(@"^\s*[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.\d+)?(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?\s*$", RegexOptions.CultureInvariant);

    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        var m = s == null ? null : Rx.Match(s);
        if (m == null || !m.Success) return false;
        int P(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        v = new SemVer(P(1), P(2), P(3), m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }
    public static SemVer Parse(string s) => TryParse(s, out var v) ? v : throw new FormatException("not a valid version: " + s);

    public int CompareTo(SemVer o)
    {
        int c = Major.CompareTo(o.Major); if (c != 0) return c;
        c = Minor.CompareTo(o.Minor); if (c != 0) return c;
        c = Patch.CompareTo(o.Patch); if (c != 0) return c;
        if (Pre == null) return o.Pre == null ? 0 : 1;      // release > pre-release
        if (o.Pre == null) return -1;
        var a = Pre.Split('.'); var b = o.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool na = int.TryParse(a[i], out var ia), nb = int.TryParse(b[i], out var ib);
            c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre != null ? "-" + Pre : "")}";
}

public sealed record ReleaseInfo(SemVer Version, string Tag, string Name, string Notes, string HtmlUrl,
    string ZipName, string ZipUrl, long ZipSize, string? ShaName, string? ShaUrl);

/// <summary>Parses the GitHub "latest release" API response.</summary>
public static class GitHubRelease
{
    public const string Repo = "Rick007110/Vanta";
    public const string LatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    private static readonly Regex ZipRx = new(@"^Vanta-v?\d+(\.\d+)*([-.][0-9A-Za-z.\-]+)?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Null for drafts, pre-releases, unparsable tags or releases without an app zip.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name");
        if (!SemVer.TryParse(tag, out var ver)) return null;
        string? zipName = null, zipUrl = null, shaName = null, shaUrl = null; long zipSize = 0;
        var assets = r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new();
        foreach (var x in assets)
        {
            var n = Str(x, "name") ?? "";
            if (zipName == null && ZipRx.IsMatch(n) && !n.Contains("-src", StringComparison.OrdinalIgnoreCase))
            { zipName = n; zipUrl = Str(x, "browser_download_url"); zipSize = x.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0; }
        }
        if (zipName == null || zipUrl == null) return null;
        foreach (var x in assets)
        {
            var n = Str(x, "name") ?? "";
            if (n.Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase) || (shaName == null && n.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase)))
            { shaName = n; shaUrl = Str(x, "browser_download_url"); }
        }
        return new ReleaseInfo(ver, tag!, Str(r, "name") ?? tag!, Str(r, "body") ?? "", Str(r, "html_url") ?? ReleasesPage, zipName, zipUrl, zipSize, shaName, shaUrl);
    }
    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
}

public sealed record HttpResult(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null);

public enum UpdateState { UpToDate, Available, NoRelease, Offline, RateLimited, Error }

public sealed record UpdateCheck(UpdateState State, ReleaseInfo? Release = null, string? Message = null)
{
    public object ToUi() => new
    {
        state = State switch { UpdateState.UpToDate => "uptodate", UpdateState.Available => "available", UpdateState.NoRelease => "norelease", UpdateState.Offline => "offline", UpdateState.RateLimited => "ratelimited", _ => "error" },
        version = Release?.Version.ToString(), notes = Release?.Notes, url = Release?.HtmlUrl, size = Release?.ZipSize, message = Message,
    };
}

/// <summary>Unauthenticated GitHub check. Never throws: offline / rate limits / odd responses become quiet states.</summary>
public sealed class UpdateChecker
{
    private readonly Func<string, CancellationToken, Task<HttpResult>> _get;
    public SemVer Current { get; }
    public UpdateChecker(SemVer current, Func<string, CancellationToken, Task<HttpResult>> get) { Current = current; _get = get; }

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        HttpResult r;
        try { r = await _get(GitHubRelease.LatestUrl, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(UpdateState.Offline, Message: "time-out"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(UpdateState.Offline, Message: e.Message); }
        if (r.Status == 404) return new(UpdateState.NoRelease);
        if (r.Status is 403 or 429)
        {
            var remaining = r.Headers != null && r.Headers.TryGetValue("x-ratelimit-remaining", out var rem) ? rem : null;
            if (r.Status == 429 || remaining == "0" || r.Body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return new(UpdateState.RateLimited);
            return new(UpdateState.Error, Message: "HTTP " + r.Status);
        }
        if (r.Status < 200 || r.Status >= 300) return new(UpdateState.Error, Message: "HTTP " + r.Status);
        ReleaseInfo? rel;
        try { rel = GitHubRelease.Parse(r.Body); } catch (JsonException) { return new(UpdateState.Error, Message: "ongeldige JSON"); }
        if (rel == null) return new(UpdateState.NoRelease);
        return rel.Version > Current ? new(UpdateState.Available, rel) : new(UpdateState.UpToDate, rel);
    }
}

public static class Sha256Util
{
    public static string OfFile(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
    /// <summary>Reads "hash  name" / "hash *name" / bare "hash" lines (sha256sum format). Returns the hash for fileName (or the only hash).</summary>
    public static string? FromSumFile(string text, string fileName)
    {
        string? only = null; int count = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Regex.Match(line, @"^([0-9a-fA-F]{64})(?:\s+\*?(.+))?$");
            if (!m.Success) continue;
            count++; only = m.Groups[1].Value.ToLowerInvariant();
            if (m.Groups[2].Success && Path.GetFileName(m.Groups[2].Value.Trim()).Equals(fileName, StringComparison.OrdinalIgnoreCase)) return only;
        }
        return count == 1 && !Regex.IsMatch(text, @"[0-9a-fA-F]{64}\s+\S") ? only : null;
    }
}

public sealed record PendingUpdate(string Version, string Zip, string Sha256, string Notes, DateTime Downloaded);

/// <summary>Update state on disk: %LOCALAPPDATA%\Vanta\updates (pending.json, failed.json, downloads, backup, started markers).</summary>
public sealed class UpdateStore
{
    public string Dir { get; }
    public UpdateStore(string dataDir) { Dir = Path.Combine(dataDir, "updates"); }
    public string PendingFile => Path.Combine(Dir, "pending.json");
    public string FailedFile => Path.Combine(Dir, "failed.json");
    public string BackupDir => Path.Combine(Dir, "backup");
    public string StagingDir => Path.Combine(Dir, "staging");
    public string HelperDir => Path.Combine(Dir, "helper");
    public string DownloadPath(ReleaseInfo r) => Path.Combine(Dir, "download", r.Version.ToString(), r.ZipName);
    public string StartedMarker(string version) => Path.Combine(Dir, "started-" + version + ".ok");

    public PendingUpdate? LoadPending()
    {
        try { return File.Exists(PendingFile) ? JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingFile), Json.Options) : null; }
        catch { return null; }
    }
    public void SavePending(PendingUpdate p) { Directory.CreateDirectory(Dir); File.WriteAllText(PendingFile, JsonSerializer.Serialize(p, Json.Options)); }
    public void ClearPending() { try { File.Delete(PendingFile); } catch { } }

    public HashSet<string> Failed()
    {
        try { return File.Exists(FailedFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FailedFile)) ?? new() : new(); }
        catch { return new(); }
    }
    public bool IsFailed(string version) => Failed().Contains(version);
    public void MarkFailed(string version)
    {
        var f = Failed(); f.Add(version);
        Directory.CreateDirectory(Dir); File.WriteAllText(FailedFile, JsonSerializer.Serialize(f));
    }
    public void MarkStarted(string version) { try { Directory.CreateDirectory(Dir); File.WriteAllText(StartedMarker(version), DateTime.UtcNow.ToString("o")); } catch { } }

    /// <summary>A pending update that should be applied now: newer than current, not failed before, zip present with the right hash.</summary>
    public PendingUpdate? ApplicablePending(SemVer current)
    {
        var p = LoadPending(); if (p == null) return null;
        if (!SemVer.TryParse(p.Version, out var v) || v <= current || IsFailed(p.Version) || !File.Exists(p.Zip)) { ClearPending(); return null; }
        try { if (!Sha256Util.OfFile(p.Zip).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase)) { ClearPending(); return null; } }
        catch { return null; }
        return p;
    }

    /// <summary>Downloads the zip + its .sha256 and verifies. Throws with a Dutch message on any problem; a bad file is deleted.</summary>
    public async Task<PendingUpdate> DownloadAsync(ReleaseInfo r, Func<string, CancellationToken, Task<string>> getText,
        Func<string, string, IProgress<double>?, CancellationToken, Task> downloadFile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (r.ShaUrl == null) throw new InvalidOperationException(Strings.Get("update.noSha"));
        var expected = Sha256Util.FromSumFile(await getText(r.ShaUrl, ct).ConfigureAwait(false), r.ZipName)
            ?? throw new InvalidOperationException(Strings.Get("update.noHash", r.ZipName));
        var path = DownloadPath(r);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!(File.Exists(path) && Sha256Util.OfFile(path) == expected))
        {
            var tmp = path + ".part";
            await downloadFile(r.ZipUrl, tmp, progress, ct).ConfigureAwait(false);
            var got = Sha256Util.OfFile(tmp);
            if (got != expected) { try { File.Delete(tmp); } catch { } throw new InvalidOperationException(Strings.Get("update.shaMismatch", expected[..12], got[..12])); }
            File.Move(tmp, path, true);
        }
        var p = new PendingUpdate(r.Version.ToString(), path, expected, r.Notes, DateTime.UtcNow);
        SavePending(p);
        return p;
    }
}

/// <summary>
/// File side of an update: verify, extract to staging, back up the app folder, copy the new files over it (merge: files that
/// are not in the package, such as user-added game folders, are left alone), and roll back from the backup.
/// Never touches the data folder (%LOCALAPPDATA%\Vanta) apart from its own updates\ subfolder.
/// </summary>
public static class UpdateApplier
{
    public const string ExeName = "Vanta.exe";

    public static string Extract(string zip, string staging)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(staging, e.FullName.Replace('\\', '/')));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("invalid path in zip: " + e.FullName);
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        return ContentRoot(staging);
    }

    /// <summary>The folder that holds Vanta.exe (the zip normally has a single top-level "Vanta" folder).</summary>
    public static string ContentRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, ExeName))) return staging;
        var dirs = Directory.GetDirectories(staging);
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], ExeName))) return dirs[0];
        throw new InvalidOperationException(Strings.Get("update.noExe"));
    }

    public static void Backup(string appDir, string backupDir)
    {
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
        CopyTree(appDir, backupDir, skip: null);
    }

    public static void Install(string contentRoot, string appDir) => CopyTree(contentRoot, appDir, skip: null);

    /// <summary>Restores every backed-up file; files that only exist in the new version are removed where they were not there before.</summary>
    public static void Rollback(string backupDir, string appDir, string? contentRoot = null)
    {
        if (!Directory.Exists(backupDir)) throw new InvalidOperationException("no backup to roll back to");
        if (contentRoot != null && Directory.Exists(contentRoot))
            foreach (var f in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(contentRoot, f);
                if (!File.Exists(Path.Combine(backupDir, rel))) TryDelete(Path.Combine(appDir, rel));
            }
        if (contentRoot != null && Directory.Exists(contentRoot))
            foreach (var d in Directory.GetDirectories(contentRoot, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            {
                var rel = Path.GetRelativePath(contentRoot, d); var target = Path.Combine(appDir, rel);
                if (!Directory.Exists(Path.Combine(backupDir, rel)) && Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                    try { Directory.Delete(target); } catch { }
            }
        CopyTree(backupDir, appDir, skip: null);
    }

    /// <summary>Full apply: verify hash, extract, back up, install. On an install error the backup is restored before rethrowing.</summary>
    public static string Apply(string zip, string sha256, string appDir, UpdateStore store, Action<string>? log = null)
    {
        log ??= _ => { };
        var got = Sha256Util.OfFile(zip);
        if (!got.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings.Get("update.pkgMismatch"));
        log("extracting to " + store.StagingDir);
        var content = Extract(zip, store.StagingDir);
        log("backing up " + appDir);
        Backup(appDir, store.BackupDir);
        try { log("installeren"); Install(content, appDir); }
        catch (Exception e) { log("install failed: " + e.Message + " - rolling back"); Rollback(store.BackupDir, appDir, content); throw; }
        return content;
    }

    public static bool CanWrite(string dir)
    {
        try { var p = Path.Combine(dir, ".vanta-write-test-" + Guid.NewGuid().ToString("N")); File.WriteAllText(p, ""); File.Delete(p); return true; }
        catch { return false; }
    }

    private static void CopyTree(string from, string to, Func<string, bool>? skip)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            if (skip != null && skip(rel)) continue;
            var dest = Path.Combine(to, rel);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(f, dest, true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); }   // exe may still be closing
            }
        }
    }
    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
