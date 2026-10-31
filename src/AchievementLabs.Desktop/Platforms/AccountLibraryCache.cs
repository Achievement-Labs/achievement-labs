using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Desktop.Platforms;

internal static class AccountLibraryCache
{
    private static string PathFor(ConnectedPlatformAccount account) => AchievementLabs.Core.AchievementLabsPaths.LocalFile(Path.Combine("account-libraries", account.Platform, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.AccountId))) + ".json"));
    internal static void Save(ConnectedPlatformAccount account, PlatformCatalogResult catalog)
    {
        if (catalog.Games.Any(g => g.Platform != account.Platform || g.AccountId != account.AccountId)) throw new InvalidDataException("Library account mismatch.");
        var path = PathFor(account); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(catalog)); File.Move(path + ".tmp", path, true);
    }
    internal static PlatformCatalogResult? Load(ConnectedPlatformAccount account)
    {
        try
        {
            var file = PathFor(account); if (!File.Exists(file)) return null;
            var result = JsonSerializer.Deserialize<PlatformCatalogResult>(File.ReadAllText(file));
            if (result == null || result.Games.Any(g => g.Platform != account.Platform || g.AccountId != account.AccountId) || result.Accounts.Length != 1 || result.Accounts[0] != account.AccountId) return null;
            return result with { Status = account.DisplayName + " · offline copy of this account's library · refresh to update" };
        }
        catch { return null; }
    }
}
