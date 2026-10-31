using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Desktop.Platforms;

// Tokens belong to this application, encrypted for the current Windows user; never logged.
internal sealed record ConnectedPlatformAccount(string Platform, string AccountId, string DisplayName, string AccessToken,
    string RefreshToken = "", DateTimeOffset? ExpiresAt = null, string SessionId = "", string PersonaId = "", string Cookies = "", string SessionSource = "", string AuthAppId = "", string LauncherOrbitToken = "", string SessionKind = "Bearer")
{
    public override string ToString() => Platform + " account (credentials protected)";
    private static string FileName(string platform)
    {
        if (platform is not ("Epic" or "EA" or "Ubisoft" or "GOG")) throw new ArgumentException("Unknown platform.");
        return AchievementLabs.Core.AchievementLabsPaths.LocalFile(Path.Combine("platform-accounts", platform + ".bin"));
    }
    internal static ConnectedPlatformAccount? Load(string platform)
    {
        try
        {
            var path = FileName(platform);
            if (!File.Exists(path)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes("AchievementLabs.ConnectedPlatformAccount.v1"), DataProtectionScope.CurrentUser);
            try { var account = JsonSerializer.Deserialize<ConnectedPlatformAccount>(bytes); return account?.Platform == platform && account.AccountId.Length > 0 ? account : null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch { return null; }
    }
    internal void Save()
    {
        var path = FileName(Platform); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("AchievementLabs.ConnectedPlatformAccount.v1"), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path + ".tmp", encrypted); File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static void Forget(string platform) { var path = FileName(platform); if (File.Exists(path)) File.Delete(path); }
}
