using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchievementLabs.Mirror;

namespace AchievementLabs.Core;

/// <summary>
/// Saved Achievement Mirror pairs and the Steam Web API key. The key is encrypted for the current
/// Windows account (DPAPI), the same way the event token is stored.
/// </summary>
public static class MirrorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string PairsPath => AchievementLabsPaths.LocalFile("mirror/pairs.json");
    public static string KeyPath => AchievementLabsPaths.LocalFile("mirror/steam-web-api-key.bin");
    public static string LogPath => AchievementLabsPaths.LocalFile("mirror/mirror-log.txt");

    public static List<MirrorPair> LoadPairs(string? path = null)
    {
        path ??= PairsPath;
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<MirrorPair>>(File.ReadAllText(path), Json) ?? [];
    }

    public static void SavePairs(IEnumerable<MirrorPair> pairs, string? path = null)
    {
        path ??= PairsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(pairs.ToList(), Json));
        File.Move(temp, path, true);
    }

    public static void SaveKey(string key)
    {
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, protectedBytes);
    }

    public static string ReadKey()
    {
        if (!File.Exists(KeyPath)) return "";
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void DeleteKey() { if (File.Exists(KeyPath)) File.Delete(KeyPath); }

    /// <summary>Appends to a plain-text audit log of everything the Mirror did. Never throws.</summary>
    public static void AppendLog(string pairName, MirrorLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{entry.At:yyyy-MM-dd HH:mm:ss zzz}\t{pairName}\t{(entry.IsError ? "ERROR " : "")}{entry.Message}{Environment.NewLine}");
        }
        catch { /* Logging must never interrupt mirroring. */ }
    }
}
