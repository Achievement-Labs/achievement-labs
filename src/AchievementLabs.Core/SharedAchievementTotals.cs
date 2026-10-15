using System.Text.Json;

namespace AchievementLabs.Core;

public sealed class SharedAchievementTotals
{
    public sealed record Entry(string TitleId, string Name, string[] Platforms, int Total, DateTimeOffset CheckedAt, string Endpoint);
    public sealed record Catalogue(int SchemaVersion, Entry[] Entries);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly Uri PublishedUri = new("https://raw.githubusercontent.com/thevampirebat/achievement-labs/main/catalog/achievement-totals.json");
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    public int Count => entries.Count;

    public static SharedAchievementTotals Bundled()
    {
        using var stream = typeof(SharedAchievementTotals).Assembly.GetManifestResourceStream("AchievementLabs.SharedTotals.json")
            ?? throw new InvalidDataException("Bundled totals catalogue is missing.");
        using var reader = new StreamReader(stream);
        var result = new SharedAchievementTotals();
        result.Merge(reader.ReadToEnd());
        return result;
    }
    public void Merge(string json)
    {
        var document = JsonSerializer.Deserialize<Catalogue>(json, Json)
            ?? throw new InvalidDataException("Empty totals catalogue.");
        if (document.SchemaVersion != 1 || document.Entries == null || document.Entries.Length > 100000)
            throw new InvalidDataException("Unsupported totals catalogue.");
        var seen = new HashSet<string>();
        foreach (var entry in document.Entries)
            if (entry == null || !uint.TryParse(entry.TitleId, out var id) || id == 0 || entry.TitleId != id.ToString() ||
                !seen.Add(entry.TitleId) || entry.Total <= 0 || entry.Total > 10000 ||
                entry.Platforms == null || entry.Platforms.Length == 0 || entry.Platforms.Any(string.IsNullOrWhiteSpace) ||
                entry.CheckedAt == default || entry.CheckedAt > DateTimeOffset.UtcNow.AddDays(1))
                throw new InvalidDataException("Invalid totals catalogue entry.");
        // Validate the whole document first; a malformed update cannot partly replace the cache.
        foreach (var entry in document.Entries)
            if (!entries.TryGetValue(entry.TitleId, out var old) || entry.CheckedAt > old.CheckedAt)
                entries[entry.TitleId] = entry;
    }
    public bool TryGet(string titleId, string platform, out int total)
    {
        total = 0;
        if (!entries.TryGetValue(titleId, out var entry)) return false;
        var devices = platform.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!devices.Any(d => entry.Platforms.Contains(d, StringComparer.OrdinalIgnoreCase))) return false;
        total = entry.Total;
        return true;
    }
    public async Task RefreshAsync(HttpClient http, string cachePath, CancellationToken ct, bool force = false)
    {
        if (File.Exists(cachePath))
        {
            try
            {
                Merge(await File.ReadAllTextAsync(cachePath, ct));
                if (!force && File.GetLastWriteTimeUtc(cachePath) > DateTime.UtcNow.AddHours(-24)) return;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { }
        }
        try
        {
            // This client has no Xbox authorization, cookies, XUID, or account progress.
            var json = await http.GetStringAsync(PublishedUri, ct);
            Merge(json);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp";
            await File.WriteAllTextAsync(temporary, json, ct);
            File.Move(temporary, cachePath, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException)
        { } // Offline/invalid updates leave bundled and previously cached counts usable.
    }
}
