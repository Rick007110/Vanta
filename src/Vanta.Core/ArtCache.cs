using System.Collections.Concurrent;

namespace Vanta.Core;

/// <summary>
/// Steam artwork by app id (library cover, hero, logo) cached under %LOCALAPPDATA%\Vanta\art.
/// Tries several CDN file names per kind; failures are remembered for a day so a missing image never hammers the CDN.
/// </summary>
public sealed class ArtCache
{
    public static readonly string[] Hosts =
    {
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/{1}",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/{1}",
    };
    public static readonly Dictionary<string, string[]> Files = new()
    {
        ["cover"] = new[] { "library_600x900_2x.jpg", "library_600x900.jpg", "header.jpg", "capsule_616x353.jpg", "capsule_231x87.jpg" },
        ["hero"] = new[] { "library_hero.jpg", "header.jpg", "capsule_616x353.jpg" },
        ["logo"] = new[] { "logo.png" },
        ["header"] = new[] { "header.jpg", "capsule_616x353.jpg", "capsule_231x87.jpg" },
    };
    public static readonly TimeSpan MissRetry = TimeSpan.FromHours(24);

    private readonly string _dir;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _download;
    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<string, Task<(byte[] data, string type)?>> _inflight = new();

    public ArtCache(string dir, Func<string, CancellationToken, Task<byte[]?>>? download = null)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _download = download ?? DefaultDownload;
    }

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"{Branding.Name}/{Branding.Version}");
        return h;
    });

    private static async Task<byte[]?> DefaultDownload(string url, CancellationToken ct)
    {
        using var r = await Http.Value.GetAsync(url, ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) return null;
        var b = await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return b.Length > 256 ? b : null;
    }

    public static string ContentType(string file) => file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

    public Task<(byte[] data, string type)?> GetAsync(long appId, string kind, CancellationToken ct = default)
    {
        if (appId <= 0 || !Files.ContainsKey(kind)) return Task.FromResult<(byte[], string)?>(null);
        var key = $"{appId}_{kind}";
        return _inflight.GetOrAdd(key, k => Run(appId, kind, k, ct));
    }

    private async Task<(byte[] data, string type)?> Run(long appId, string kind, string key, CancellationToken ct)
    {
        try { return await Fetch(appId, kind, key, ct).ConfigureAwait(false); }
        catch (Exception) { return null; }
        finally { _inflight.TryRemove(key, out _); }
    }

    private async Task<(byte[] data, string type)?> Fetch(long appId, string kind, string key, CancellationToken ct)
    {
        foreach (var ext in new[] { ".jpg", ".png" })
        {
            var f = Path.Combine(_dir, key + ext);
            if (File.Exists(f)) return (await File.ReadAllBytesAsync(f, ct).ConfigureAwait(false), ContentType(f));
        }
        var miss = Path.Combine(_dir, key + ".miss");
        if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissRetry) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var file in Files[kind])
                foreach (var host in Hosts)
                {
                    byte[]? data = null;
                    try { data = await _download(string.Format(host, appId, file), ct).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }
                    if (data == null) continue;
                    var ext = Path.GetExtension(file);
                    var target = Path.Combine(_dir, key + ext);
                    var tmp = target + ".tmp";
                    await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
                    File.Move(tmp, target, true);
                    if (File.Exists(miss)) File.Delete(miss);
                    return (data, ContentType(file));
                }
            await File.WriteAllTextAsync(miss, DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);
            return null;
        }
        finally { _gate.Release(); }
    }
}
