using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Vanta.Core.Account;

public sealed record AccountUser(string Id, string Username, string? AvatarUrl);
/// <summary>Supabase session: short-lived access token (JWT, <paramref name="Expires"/> = unix seconds) plus a
/// refresh token (rotated on every refresh).</summary>
public sealed record AccountSession(string Token, long Expires, AccountUser User, string? RefreshToken = null)
{
    /// <summary>Access token expired (or expires within <paramref name="margin"/> seconds).</summary>
    public bool Expired(DateTimeOffset now, int margin = 0) => Expires > 0 && now.ToUnixTimeSeconds() >= Expires - margin;
    /// <summary>Can still be used, possibly after a refresh.</summary>
    public bool Usable(DateTimeOffset now) => RefreshToken != null || !Expired(now);
}

/// <summary>Where the session token lives between runs.</summary>
public interface ITokenStore
{
    AccountSession? Load();
    void Save(AccountSession s);
    void Clear();
}

public sealed class MemoryTokenStore : ITokenStore
{
    private AccountSession? _s;
    public AccountSession? Load() => _s;
    public void Save(AccountSession s) => _s = s;
    public void Clear() => _s = null;
}

/// <summary>
/// Session in %LOCALAPPDATA%\Vanta\account.bin, encrypted with Windows DPAPI (CurrentUser scope):
/// only the same Windows user on the same PC can decrypt it. Uses crypt32 directly (no extra package).
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Vanta.Account.v1");
    public string File { get; }
    public DpapiTokenStore(string? file = null) => File = file ?? Path.Combine(Settings.DataDir, "account.bin");

    public AccountSession? Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            var plain = Unprotect(System.IO.File.ReadAllBytes(File));
            return JsonSerializer.Deserialize<AccountSession>(plain, Json.Options);
        }
        catch { return null; }   // other user / corrupt: treat as logged out
    }

    public void Save(AccountSession s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllBytes(tmp, Protect(JsonSerializer.SerializeToUtf8Bytes(s, Json.Options)));
        System.IO.File.Move(tmp, File, true);
    }

    public void Clear() { try { System.IO.File.Delete(File); } catch { } }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? desc, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data) => Run(data, true);
    public static byte[] Unprotect(byte[] data) => Run(data, false);

    private static byte[] Run(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows-only");
        var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = hIn.AddrOfPinnedObject() };
            var ent = new Blob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Vanta", ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException("DPAPI error " + Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { hIn.Free(); hEnt.Free(); }
    }
}
