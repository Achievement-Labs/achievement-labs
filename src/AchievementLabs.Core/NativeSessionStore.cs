using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XboxAuthNet.OAuth;

namespace AchievementLabs.Core;

/// <summary>Windows-user-bound storage, separate from the original app's auth.json.</summary>
public static class NativeSessionStore
{
    public static string DefaultPath => AchievementLabsPaths.LocalFile("session.bin");
    public sealed record SavedSession(string ProfileName, MicrosoftOAuthResponse OAuth);

    public static async Task SaveAsync(string path, SavedSession session, CancellationToken cancellationToken = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] protectedBytes;
        try { protectedBytes = SecureUserData.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<SavedSession> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        var plain = SecureUserData.Unprotect(encrypted);
        try
        {
            var session = JsonSerializer.Deserialize<SavedSession>(plain);
            if (session == null || string.IsNullOrWhiteSpace(session.OAuth?.RefreshToken) || string.IsNullOrWhiteSpace(session.ProfileName))
                throw new InvalidDataException("Invalid saved native session.");
            return session;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
