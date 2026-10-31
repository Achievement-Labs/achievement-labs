using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Author-maintained data, with an embedded copy of the same source for portable builds.</summary>
internal sealed class LocalEventCatalogue
{
    private readonly JObject data;
    private readonly EventCatalogStorage storage;
    private readonly HashSet<string> supported;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Version, Task<EventTitleMapping> Result)> validated = new();
    public string[] ReservedTitleIds => data.Properties().Where(p => uint.TryParse(p.Name, out _)).Select(p => p.Name).ToArray();
    public string[] TitleIds => supported.Where(id => id != "345903534").ToArray();
    public LocalEventCatalogue(string? path)
    {
        var external = path != null && (File.Exists(Path.Combine(path, "index.json")) || File.Exists(Path.Combine(path, "Data.json")));
        storage = external ? new EventCatalogStorage(path!) : new EventCatalogStorage(HasResource("index.json"),
            (name, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Resource(name)); });
        data = storage.Index;
        if (data.Properties().LastOrDefault()?.Name != "SupportedTitleIDs") throw new InvalidDataException("SupportedTitleIDs must be last in the local catalogue.");
        supported = (data["SupportedTitleIDs"] as JArray ?? throw new InvalidDataException("Missing supported title IDs."))
            .Select(i => i.ToString()).Where(id => data[id] is JObject b && (bool?)b["Enabled"] != false).ToHashSet(StringComparer.Ordinal);
    }
    private static string ResourceName(string path) => "AchievementLabs.LocalEvents." + path.Replace('/', '.').Replace('\\', '.');
    private static string? FindResource(string path) => typeof(LocalEventCatalogue).Assembly.GetManifestResourceNames()
        .FirstOrDefault(name => name.Replace('/', '.').Replace('\\', '.') == ResourceName(path));
    private static bool HasResource(string path) => FindResource(path) != null;
    private static string Resource(string path)
    {
        using var stream = typeof(LocalEventCatalogue).Assembly.GetManifestResourceStream(FindResource(path)
            ?? throw new InvalidDataException("Local event file is missing: " + path))!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public bool Contains(string id) => id != "345903534" && supported.Contains(id) && data[id] is JObject;
    public Task<EventTitleMapping> TitleAsync(string id, CancellationToken ct)
    {
        var version = storage.Version(id);
        var item = validated.AddOrUpdate(id, _ => (version, ValidateAsync(id)),
            (_, old) => old.Version == version ? old : (version, ValidateAsync(id)));
        return item.Result.WaitAsync(ct);
    }
    private async Task<EventTitleMapping> ValidateAsync(string id)
    {
        var ids = new List<string>();
        var block = await storage.ReadTitleAsync(id, CancellationToken.None);
        foreach (var p in ((JObject)block["Achievements"]!).Properties())
        {
            try { await PayloadsAsync(id, p.Name, "0", CancellationToken.None); ids.Add(p.Name); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or Newtonsoft.Json.JsonException or IOException or ArgumentException or KeyNotFoundException or InvalidCastException or FormatException) { }
        }
        return new(ids.ToArray());
    }
    public async Task<EventPayloads> PayloadsAsync(string id, string aid, string xuid, CancellationToken ct)
    {
        var bodies = await new EventCatalog(storage).BuildPayloadsAsync(id, aid, xuid, DateTime.UtcNow, ct);
        foreach (var text in bodies)
            if (JObject.Parse(text)["data"]?["baseData"]?["titleId"]?.ToString() != id)
                throw new InvalidDataException("The local payload is missing the selected title identity.");
        return new(bodies.ToArray());
    }
    public EventCatalogStatus Status()
    {
        using var stream = typeof(LocalEventCatalogue).Assembly.GetManifestResourceStream("AchievementLabs.LocalPublicCatalog.json")!;
        using var reader = new StreamReader(stream); var rows = JArray.Parse(reader.ReadToEnd());
        string[] Ids(string status) => rows.Where(r => (string?)r["status"] == status).Select(r => r["titleId"]!.ToString()).ToArray();
        var supportedIds = Ids("supported"); var coming = Ids("coming-soon");
        // Partial titles stay in the testing list (unknown statuses fall through to it anyway) and are also reported separately.
        return new(supportedIds, Ids("testing").Concat(TitleIds.Except(supportedIds).Except(coming)).Distinct().ToArray(), coming, Ids("partial"));
    }
}
