using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Core;

public static class EventTokenIssuerExpiry
{
    private sealed record Metadata(string Fingerprint, DateTimeOffset ExpiresAt);
    private static string PathName => AchievementLabsPaths.LocalFile("event-token-expiry.bin");
    private static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static async Task SaveAsync(string token, DateTimeOffset expiry, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Metadata(Fingerprint(token), expiry));
        byte[] protectedBytes;
        try { protectedBytes = SecureUserData.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, protectedBytes, ct); File.Move(temporary, PathName, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<DateTimeOffset?> ReadAsync(string token, CancellationToken ct)
    {
        if (!File.Exists(PathName) || new FileInfo(PathName).Length > 8192) return null;
        var plain = SecureUserData.Unprotect(await File.ReadAllBytesAsync(PathName, ct));
        try { var saved = JsonSerializer.Deserialize<Metadata>(plain); return saved?.Fingerprint == Fingerprint(token) ? saved.ExpiresAt : null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
