using System.Text.Json;

namespace AchievementLabs.MultiSelect;

// Display metadata only: this catalogue never changes unlock/event mappings.
public sealed class SharedDlcCatalogue
{
    public sealed record Definition(string Id, string Name);
    public sealed record Pack(string Name, string Kind, Definition[] Achievements);
    public sealed record Title(string TitleId, string[] Platforms, string Source, Pack[] Packs, bool IsHub = false);
    public sealed record Document(int SchemaVersion, Title[] Titles);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static SharedDlcCatalogue Current { get; } = Bundled();
    public static readonly Uri PublishedUri = new("https://raw.githubusercontent.com/thevampirebat/achievement-labs/main/catalog/achievement-packs.json");
    public static readonly Uri PublishedHubsUri = new("https://raw.githubusercontent.com/thevampirebat/achievement-labs/main/catalog/achievement-hubs.json");
    public int Count => titles.Length;
    public DateTimeOffset? LastSuccessfulRefresh { get; private set; }
    public string LastRefreshStatus { get; private set; } = "Bundled catalogue loaded";
    public bool LastRefreshSucceeded { get; private set; }
    private Title[] titles = [];
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public static SharedDlcCatalogue Bundled()
    {
        using var stream = typeof(SharedDlcCatalogue).Assembly.GetManifestResourceStream("AchievementLabs.SharedPacks.json")
            ?? throw new InvalidDataException("Bundled DLC catalogue missing.");
        using var reader = new StreamReader(stream);
        var result = new SharedDlcCatalogue(); result.Merge(reader.ReadToEnd());
        // Hubs use a separate embedded catalogue so older app versions can still
        // read the published schema that requires a nonempty base-game section.
        using var hubs = typeof(SharedDlcCatalogue).Assembly.GetManifestResourceStream("AchievementLabs.SharedHubs.json")
            ?? throw new InvalidDataException("Bundled achievement hub catalogue missing.");
        using var hubReader = new StreamReader(hubs);
        result.Merge(hubReader.ReadToEnd()); return result;
    }
    public void Merge(string json)
    {
        if (json.Length > 4_000_000) throw new InvalidDataException("DLC catalogue is too large.");
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("Empty DLC catalogue.");
        if (doc.SchemaVersion != 1 || doc.Titles == null || doc.Titles.Length > 10000)
            throw new InvalidDataException("Unsupported DLC catalogue.");
        var editions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in doc.Titles)
        {
            if (title == null || !uint.TryParse(title.TitleId, out var id) || id == 0 || title.TitleId != id.ToString() ||
                title.Platforms == null || title.Platforms.Length == 0 || title.Platforms.Any(string.IsNullOrWhiteSpace) ||
                !Uri.TryCreate(title.Source, UriKind.Absolute, out var source) || source.Scheme != "https" ||
                title.Packs == null || title.Packs.Length == 0 || title.Packs.Length > 500 || title.Packs.Count(p => p?.Kind == "base") != (title.IsHub ? 0 : 1))
                throw new InvalidDataException("Invalid DLC title.");
            foreach (var platform in title.Platforms)
                if (!editions.Add(title.TitleId + "/" + platform)) throw new InvalidDataException("Duplicate edition.");
            var ids = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in title.Packs)
            {
                if (pack == null || string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 200 ||
                    pack.Name is "All packs" or "Unclassified" || !names.Add(pack.Name) ||
                    pack.Kind is not ("base" or "dlc" or "update") || pack.Achievements == null ||
                    pack.Achievements.Length == 0 || pack.Achievements.Length > 10000)
                    throw new InvalidDataException("Invalid DLC pack.");
                foreach (var achievement in pack.Achievements)
                    if (achievement == null || string.IsNullOrWhiteSpace(achievement.Id) || achievement.Id.Length > 100 ||
                        string.IsNullOrWhiteSpace(achievement.Name) || achievement.Name.Length > 500 || !ids.Add(achievement.Id))
                        throw new InvalidDataException("Invalid or duplicate achievement mapping.");
            }
        }
        // Replace only after full validation. Keep bundled titles omitted from an update.
        var updated = titles.Where(t => !doc.Titles.Any(n => n.TitleId == t.TitleId && n.Platforms.Intersect(t.Platforms, StringComparer.OrdinalIgnoreCase).Any())).Concat(doc.Titles).ToArray();
        titles = updated;
    }
    public Title? Find(string titleId, string platform)
    {
        var devices = platform.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return titles.FirstOrDefault(t => t.TitleId == titleId && devices.Any(d => t.Platforms.Contains(d, StringComparer.OrdinalIgnoreCase)));
    }
    public static string Group(Title title, string id, string name) => title.Packs.FirstOrDefault(p => p.Achievements.Any(a =>
        a.Id == id && AchievementView.Normalize(a.Name) == AchievementView.Normalize(name)))?.Name ?? "Unclassified";

    public async Task RefreshAsync(HttpClient http, string cachePath, CancellationToken ct, bool force = false)
    {
        await refreshGate.WaitAsync(ct);
        try
        {
            LastRefreshSucceeded = true;
            bool downloaded = false;
            var hubPath = Path.Combine(Path.GetDirectoryName(cachePath)!, "shared-achievement-hubs.json");
            foreach (var (uri, path) in new[] { (PublishedUri, cachePath), (PublishedHubsUri, hubPath) })
            {
                if (File.Exists(path))
                    try
                    {
                        Merge(await File.ReadAllTextAsync(path, ct));
                        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                        if (LastSuccessfulRefresh == null || written > LastSuccessfulRefresh) LastSuccessfulRefresh = written;
                        if (!force && written > DateTimeOffset.UtcNow.AddHours(-24)) continue;
                    }
                    catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
                try
                {
                    var json = await http.GetStringAsync(uri, ct);
                    Merge(json);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path + ".tmp", json, ct);
                    File.Move(path + ".tmp", path, true);
                    LastSuccessfulRefresh = DateTimeOffset.UtcNow; downloaded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                { LastRefreshSucceeded = false; }
            }
            LastRefreshStatus = !LastRefreshSucceeded ? "Refresh incomplete; valid cached or bundled sections retained"
                : downloaded ? "Catalogue refreshed" : "Cached catalogue is up to date";
        }
        finally { refreshGate.Release(); }
    }
}
