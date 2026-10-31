using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Desktop.Platforms;

// Normalized, explicitly imported snapshots are separate from launcher databases and live Events.
public sealed record PlatformSnapshot(int SchemaVersion, string Platform, string GameId, string Title,
    string AccountId, bool CatalogComplete, PlatformAchievementRow[] Achievements, string ImageUrl = "")
{
    public static string DefaultFolder => AchievementLabs.Core.AchievementLabsPaths.LocalFile("platform-snapshots");
    public static PlatformSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        var snapshot = JsonSerializer.Deserialize<PlatformSnapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("The snapshot is empty.");
        if (snapshot.SchemaVersion != 1 || snapshot.Platform is not ("EA" or "GOG" or "Epic" or "Ubisoft") || string.IsNullOrWhiteSpace(snapshot.GameId) || string.IsNullOrWhiteSpace(snapshot.Title) || snapshot.Achievements == null)
            throw new InvalidDataException("Use a version 1 snapshot with platform, gameId, title and achievements.");
        if (snapshot.GameId.Length > 500 || snapshot.Title.Length > 1000 || snapshot.Achievements.Length > 20000) throw new InvalidDataException("Snapshot exceeds the supported limits.");
        if (snapshot.Achievements.Any(a => a.Unlocked.HasValue) && string.IsNullOrWhiteSpace(snapshot.AccountId)) throw new InvalidDataException("Snapshots containing player states must identify their accountId.");
        if (snapshot.Achievements.Any(a => string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Name) || a.Description == null || a.GlobalPercent is < 0 or > 100 || a.GlobalPercent is { } p && !double.IsFinite(p)) || snapshot.Achievements.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Achievements.Length)
            throw new InvalidDataException("Achievement IDs must be unique and rarity must be between 0 and 100.");
        return snapshot with { Achievements = snapshot.Achievements.Select(a => a with { Source = "Imported snapshot" }).ToArray() };
    }
    public static string Import(string path, string platform, string folder)
    {
        if (new FileInfo(path).Length > 16_777_216) throw new InvalidDataException("Snapshot must be smaller than 16 MB.");
        var snapshot = Parse(File.ReadAllText(path));
        if (snapshot.Platform != platform) throw new InvalidDataException($"This snapshot is for {snapshot.Platform}. Open that platform before importing.");
        Directory.CreateDirectory(folder);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Platform + ":" + snapshot.GameId)));
        var destination = Path.Combine(folder, hash + ".json");
        if (File.Exists(destination)) File.Copy(destination, destination + "." + Guid.NewGuid().ToString("N") + ".bak", false);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, destination, true);
        return snapshot.GameId;
    }
    public static (PlatformGameRow[] Games, int Invalid) Apply(string platform, IEnumerable<PlatformGameRow> source, string folder)
    {
        var games = source.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var invalid = 0;
        if (!Directory.Exists(folder)) return (games.Values.ToArray(), 0);
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 16_777_216) throw new InvalidDataException();
                var s = Parse(File.ReadAllText(path));
                if (s.Platform != platform) continue;
                games.TryGetValue(s.GameId, out var old);
                // Imported states keep their account and source visible and never mix with cache states.
                games[s.GameId] = (old ?? new(platform, s.GameId, s.Title)) with { Name = s.Title, ImageUrl = string.IsNullOrWhiteSpace(s.ImageUrl) ? old?.ImageUrl ?? "" : s.ImageUrl, Rows = s.Achievements, CatalogComplete = s.CatalogComplete, AccountId = s.AccountId ?? "", Source = "Imported snapshot" };
            }
            catch { invalid++; }
        }
        return (games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray(), invalid);
    }
}
