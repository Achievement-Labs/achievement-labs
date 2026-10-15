using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Author-maintained data, with an embedded copy of the same source for portable builds.</summary>
internal sealed class LocalEventCatalogue
{
    private readonly JObject data;
    private readonly string? directory;
    private readonly HashSet<string> supported;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<EventTitleMapping>> validated = new();
    public string[] ReservedTitleIds => data.Properties().Where(p => uint.TryParse(p.Name, out _)).Select(p => p.Name).ToArray();
    public string[] TitleIds => supported.Where(id => id != "345903534").ToArray();
    public LocalEventCatalogue(string? path)
    {
        directory = path != null && File.Exists(Path.Combine(path, "Data.json")) ? path : null;
        data = JObject.Parse(directory == null ? Resource("Data") : File.ReadAllText(Path.Combine(directory, "Data.json")));
        if (data.Properties().LastOrDefault()?.Name != "SupportedTitleIDs") throw new InvalidDataException("SupportedTitleIDs must be last in the local catalogue.");
        supported = (data["SupportedTitleIDs"] as JArray ?? throw new InvalidDataException("Missing supported title IDs."))
            .Select(i => i.ToString()).Where(id => data[id] is JObject b && (bool?)b["Enabled"] != false).ToHashSet(StringComparer.Ordinal);
    }
    private static string Resource(string id)
    {
        using var stream = typeof(LocalEventCatalogue).Assembly.GetManifestResourceStream("AchievementLabs.LocalEvents." + id + ".json")
            ?? throw new InvalidDataException("Local event template is missing: " + id);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public bool Contains(string id) => id != "345903534" && supported.Contains(id) && data[id] is JObject;
    private EventCatalog Catalog() => new(data, (id, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(directory == null ? Resource(id) : File.ReadAllText(Path.Combine(directory, id + ".json"))); });
    public Task<EventTitleMapping> TitleAsync(string id, CancellationToken ct) => validated.GetOrAdd(id, ValidateAsync).WaitAsync(ct);
    private async Task<EventTitleMapping> ValidateAsync(string id)
    {
        var ids = new List<string>();
        foreach (var p in ((JObject)data[id]!["Achievements"]!).Properties())
        {
            try { await PayloadsAsync(id, p.Name, "0", CancellationToken.None); ids.Add(p.Name); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or Newtonsoft.Json.JsonException or IOException or ArgumentException or KeyNotFoundException or InvalidCastException or FormatException) { }
        }
        return new(ids.ToArray());
    }
    public async Task<EventPayloads> PayloadsAsync(string id, string aid, string xuid, CancellationToken ct)
    {
        var bodies = await Catalog().BuildPayloadsAsync(id, aid, xuid, DateTime.UtcNow, ct);
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
        return new(supportedIds, Ids("testing").Concat(TitleIds.Except(supportedIds).Except(coming)).Distinct().ToArray(), coming);
    }
}
