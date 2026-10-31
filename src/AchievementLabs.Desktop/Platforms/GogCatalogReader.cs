using System.Globalization;
using Microsoft.Win32;

namespace AchievementLabs.Desktop.Platforms;

internal static class GogCatalogReader
{
    public static string DefaultDatabase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

    public static PlatformCatalogResult Read(string databasePath, string account, CancellationToken token)
    {
        try { return ReadDatabase(databasePath, account, token); }
        catch (IOException) when (File.Exists(databasePath + "-wal"))
        {
            using var copy = new GalaxyCacheCopy(databasePath, token);
            var result = ReadDatabase(copy.DatabasePath, account, token);
            return result with { Status = result.Status + " Read from a temporary cache copy; launcher storage was not modified." };
        }
    }

    private static PlatformCatalogResult ReadDatabase(string databasePath, string account, CancellationToken token)
    {
        var games = ReadRegistry();
        if (!File.Exists(databasePath)) return new(games.Values.ToArray(), "Galaxy cache not found. Installed GOG games were read from the registry; refresh after Galaxy has synced.", []);
        using var db = new ReadOnlySqlite(databasePath);
        db.Query("BEGIN");
        var accounts = db.Query("SELECT DISTINCT CAST(userId AS TEXT) FROM UserAchievements ORDER BY userId").Select(r => r[0]!).ToArray();
        // A single cached account is unambiguous. Multiple accounts require an explicit selection.
        if (accounts.Length == 1 && string.IsNullOrEmpty(account)) account = accounts[0];
        if (!accounts.Contains(account)) account = "";
        var products = db.Query("SELECT CAST(p.id AS TEXT),p.name,i.installationPath FROM Products p LEFT JOIN InstalledBaseProducts i ON i.productId=p.id");
        foreach (var p in products)
        {
            token.ThrowIfCancellationRequested();
            var id = p[0]!;
            games.TryGetValue(id, out var old);
            games[id] = new("GOG", id, string.IsNullOrWhiteSpace(p[1]) ? old?.Name ?? $"GOG {id}" : p[1]!, p[2] ?? old?.InstallPath ?? "", LaunchUri: $"goggalaxy://openGameView/{Uri.EscapeDataString(id)}", Source: "Galaxy local cache");
        }
        var pieces = db.Query("""SELECT g.releaseKey,t.type,g.value FROM GamePieces g JOIN GamePieceTypes t ON t.id=g.gamePieceTypeId WHERE g.releaseKey LIKE 'gog\_%' ESCAPE '\' AND (g.userId IS NULL OR CAST(g.userId AS TEXT)=?)""", account);
        foreach (var group in pieces.GroupBy(r => r[0]!))
        {
            var id = group.Key[4..];
            games.TryGetValue(id, out var game);
            var title = group.FirstOrDefault(r => r[1] is "title" or "originalTitle")?[2];
            var art = group.FirstOrDefault(r => r[1] is "verticalCover" or "squareIcon" or "background")?[2];
            games[id] = (game ?? new("GOG", id, $"GOG {id}", LaunchUri: $"goggalaxy://openGameView/{Uri.EscapeDataString(id)}", Source: "Galaxy local cache")) with { Name = Decode(title) is { Length: > 0 } name ? name : game?.Name ?? $"GOG {id}", ImageUrl = Decode(art) };
        }
        var rows = db.Query("""
            SELECT a.gameReleaseKey,a.apikey,
              (SELECT l.name FROM LocalizedAchievements l WHERE l.gameReleaseKey=a.gameReleaseKey AND l.apikey=a.apikey ORDER BY l.languageId LIMIT 1),
              (SELECT l.description FROM LocalizedAchievements l WHERE l.gameReleaseKey=a.gameReleaseKey AND l.apikey=a.apikey ORDER BY l.languageId LIMIT 1),
              a.imageUnlockedUrl,a.imageLockedUrl,a.rarity,CAST(u.isUnlocked AS TEXT),u.unlockTime
            FROM Achievements a LEFT JOIN UserAchievements u
              ON u.gameReleaseKey=a.gameReleaseKey AND u.apikey=a.apikey AND CAST(u.userId AS TEXT)=?
            WHERE a.gameReleaseKey LIKE 'gog\_%' ESCAPE '\'
            """, account);
        foreach (var group in rows.GroupBy(r => r[0]!))
        {
            token.ThrowIfCancellationRequested();
            var id = group.Key[4..];
            games.TryGetValue(id, out var game);
            var achievements = group.Select(r => new PlatformAchievementRow(r[1]!, r[2] ?? r[1]!, r[3] ?? "", r[7] == null ? null : r[7] == "1", r[7] == "1" ? r[4] ?? "" : r[5] ?? r[4] ?? "", Percent(r[6]), r[8] ?? "", "Galaxy local cache")).ToArray();
            games[id] = (game ?? new("GOG", id, $"GOG {id}", LaunchUri: $"goggalaxy://openGameView/{Uri.EscapeDataString(id)}", Source: "Galaxy local cache")) with { Rows = achievements, CatalogComplete = true, AccountId = account };
        }
        db.Query("COMMIT");
        var status = accounts.Length > 1 && account.Length == 0 ? "Choose a cached Galaxy account to display player states. Definitions are available; progress stays unknown." : "Read-only Galaxy cache. Refresh in Galaxy first to retrieve newer library and achievement data.";
        return new(games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray(), status, accounts);
    }

    private static double? Percent(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 100 ? n : null;
    private static string Decode(string? value)
    {
        if (value == null) return "";
        try { var token = Newtonsoft.Json.Linq.JToken.Parse(value); return token.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)token! : token["title"]?.ToString() ?? token["url"]?.ToString() ?? ""; }
        catch { return value; }
    }
    private static Dictionary<string, PlatformGameRow> ReadRegistry()
    {
        var games = new Dictionary<string, PlatformGameRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var registry = root.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (registry == null) continue;
            foreach (var id in registry.GetSubKeyNames())
            {
                using var key = registry.OpenSubKey(id);
                if (key == null) continue;
                games[id] = new("GOG", id, key.GetValue("gameName") as string ?? $"GOG {id}", key.GetValue("path") as string ?? "", LaunchUri: $"goggalaxy://openGameView/{Uri.EscapeDataString(id)}", Source: "GOG installation registry");
            }
        }
        return games;
    }
}
