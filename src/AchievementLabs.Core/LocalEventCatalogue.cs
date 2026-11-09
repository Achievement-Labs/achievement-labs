using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Author-maintained data, with an embedded copy of the same source for portable builds.</summary>
internal sealed class LocalEventCatalogue
{
    private readonly JObject data;
    private readonly bool externalCatalog;
    private readonly EventCatalogStorage storage;
    private readonly HashSet<string> supported;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Version, Task<EventTitleMapping> Result)> validated = new();
    public string[] ReservedTitleIds => data.Properties().Where(p => uint.TryParse(p.Name, out _)).Select(p => p.Name).ToArray();
    public string[] ProtectedTitleIds => externalCatalog ? ReservedTitleIds : data.Properties()
        .Where(p => p.Value is JObject block && ((bool?)block["Enabled"] == false || AchievementLabs.MultiSelect.EditionMapping.Supports(p.Name) || p.Name is "572802557" or "345903534"))
        .Select(p => p.Name).ToArray();
    public bool Matches(string id, JObject block, string template)
    {
        if (data[id] is not JObject) return false;
        try
        {
            var local = storage.ReadTitleAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            return (bool?)local["Enabled"] != false && ((bool?)local["FullySupported"] == true) == ((bool?)block["FullySupported"] == true)
                && JToken.DeepEquals(local["Achievements"], block["Achievements"])
                && storage.ReadTemplateAsync(id, CancellationToken.None).GetAwaiter().GetResult().Replace("\r\n", "\n").Trim() == template.Replace("\r\n", "\n").Trim();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Newtonsoft.Json.JsonException) { return false; }
    }
    public string[] TitleIds => supported.Where(id => id != "345903534").ToArray();
    public LocalEventCatalogue(string? path)
    {
        var external = path != null && (File.Exists(Path.Combine(path, "index.json")) || File.Exists(Path.Combine(path, "Data.json")));
        externalCatalog = external;
        storage = external ? new EventCatalogStorage(path!) : new EventCatalogStorage(HasResource("index.json"),
            (name, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Resource(name)); });
        data = storage.Index;
        if (data.Properties().LastOrDefault()?.Name != "SupportedTitleIDs") throw new InvalidDataException("SupportedTitleIDs must be last in the local catalogue.");
        supported = (data["SupportedTitleIDs"] as JArray ?? throw new InvalidDataException("Missing supported title IDs."))
            .Select(i => i.ToString()).Where(id => data[id] is JObject b && (bool?)b["Enabled"] != false).ToHashSet(StringComparer.Ordinal);
    }
    private static bool HasResource(string path) => BundledEventFiles.Contains(path);
    private static string Resource(string path) => BundledEventFiles.Read(path)
        ?? throw new InvalidDataException("Local event file is missing: " + path);
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
    private readonly Dictionary<string, (string Version, LibraryEventSupport Support)> supportCache = new();
    public Dictionary<string, LibraryEventSupport> LibrarySupport()
    {
        var result = new Dictionary<string, LibraryEventSupport>();
        foreach (var property in data.Properties().Where(p => EventCatalogStorage.ValidId(p.Name)))
        {
            var id = property.Name; var version = storage.Version(id);
            if (supportCache.TryGetValue(id, out var cached) && cached.Version == version) { result[id] = cached.Support; continue; }
            try
            {
                var block = storage.ReadTitleAsync(id, CancellationToken.None).GetAwaiter().GetResult();
                EventCatalogSchema.ValidateTitle(block);
                var item = new LibraryEventSupport(supported.Contains(id), (bool?)block["FullySupported"] == true,
                    (block["Achievements"] as JObject)?.Count ?? 0,
                    id == "345903534" ? "Uses the original event service; bundled recipes are not used for this edition." : null,
                    (bool?)block["PartiallySupported"] == true);
                supportCache[id] = (version, item); result[id] = item;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or Newtonsoft.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                var summary = (JObject)property.Value;
                result[id] = new(supported.Contains(id), (bool?)summary["FullySupported"] == true, 0,
                    "Catalog data could not be loaded. Retry the official catalog update.", (bool?)summary["PartiallySupported"] == true);
            }
        }
        return result;
    }
    public EventCatalogStatus Status()
    {
        var support = LibrarySupport();
        return new(support.Where(p => p.Value.Available && p.Value.FullySupported).Select(p => p.Key).ToArray(),
            support.Where(p => p.Value.Available && !p.Value.FullySupported).Select(p => p.Key).ToArray(), [],
            support.Where(p => p.Value.Available && p.Value.PartiallySupported && !p.Value.FullySupported).Select(p => p.Key).ToArray());
    }
}
