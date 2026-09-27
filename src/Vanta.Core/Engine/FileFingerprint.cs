using System.Security.Cryptography;

namespace Vanta.Core.Engine;

/// <summary>Cheap, stable fingerprint of a (large) module file: size + SHA-256 of the first and last MiB + PE header fields.</summary>
public sealed record FileFingerprint(long Size, string HeadSha256, string TailSha256, uint? PeTimestamp, uint? SizeOfImage)
{
    public const int Chunk = 1 << 20;

    public static FileFingerprint Compute(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Compute(fs);
    }

    public static FileFingerprint Compute(Stream s)
    {
        long len = s.Length;
        var head = new byte[(int)Math.Min(Chunk, len)];
        s.Position = 0; s.ReadExactly(head);
        var tail = new byte[(int)Math.Min(Chunk, len)];
        s.Position = len - tail.Length; s.ReadExactly(tail);
        uint? ts = null, soi = null;
        if (head.Length >= 0x40 && head[0] == 'M' && head[1] == 'Z')
        {
            int pe = BitConverter.ToInt32(head, 0x3C);
            if (pe > 0 && pe + 0x58 < head.Length && head[pe] == 'P' && head[pe + 1] == 'E')
            {
                ts = BitConverter.ToUInt32(head, pe + 8);
                soi = BitConverter.ToUInt32(head, pe + 24 + 56);     // OptionalHeader.SizeOfImage (same offset in PE32+)
            }
        }
        return new FileFingerprint(len, Convert.ToHexString(SHA256.HashData(head)).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(tail)).ToLowerInvariant(), ts, soi);
    }

    public override string ToString() =>
        $"fileSize={Size}, headSha256={HeadSha256}, tailSha256={TailSha256}, peTimestamp={PeTimestamp?.ToString() ?? "?"}, moduleSize={SizeOfImage?.ToString() ?? "?"}";

    /// <summary>JSON snippet for supportedVersions.</summary>
    public string ToJson(string label, string? fileVersion) =>
        "{ \"label\": \"" + label + "\"" + (fileVersion != null ? ", \"fileVersion\": \"" + fileVersion + "\"" : "") +
        $", \"fileSize\": {Size}, \"headSha256\": \"{HeadSha256}\", \"tailSha256\": \"{TailSha256}\"" +
        (PeTimestamp != null ? $", \"peTimestamp\": {PeTimestamp}" : "") + (SizeOfImage != null ? $", \"moduleSize\": {SizeOfImage}" : "") + " }";
}
