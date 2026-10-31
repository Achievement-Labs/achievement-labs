using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Reads an indexed title directory or the legacy flat catalog. Index reads never load recipes.</summary>
internal sealed class EventCatalogStorage
{
    private readonly Func<string, CancellationToken, Task<string>> read;
    private readonly string? directory;
    private sealed record CachedTitle(string Id, string Version, JObject Block);
    private CachedTitle? cachedTitle;
    public bool Indexed { get; }
    public JObject Index { get; }
    public static string IndexPath(string directory) => Path.Combine(directory, File.Exists(Path.Combine(directory, "index.json")) ? "index.json" : "Data.json");
    public EventCatalogStorage(string directory)
        : this(File.Exists(Path.Combine(directory, "index.json")),
            (path, ct) => File.ReadAllTextAsync(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)), ct)) { this.directory = directory; }
    internal EventCatalogStorage(bool indexed, Func<string, CancellationToken, Task<string>> reader)
    {
        read = reader; Indexed = indexed;
        Index = Parse(read(indexed ? "index.json" : "Data.json", CancellationToken.None).GetAwaiter().GetResult());
        if (Index.Properties().LastOrDefault()?.Name != "SupportedTitleIDs" || Index["SupportedTitleIDs"] is not JArray)
            throw new InvalidDataException("SupportedTitleIDs must be the final catalog property.");
        foreach (var property in Index.Properties().Where(p => p.Name != "SupportedTitleIDs"))
            if (Indexed && (!ValidId(property.Name) || property.Value is not JObject))
                throw new InvalidDataException("Invalid catalog title ID or block.");
        if (((JArray)Index["SupportedTitleIDs"]!).Any(id => !ValidId(id.ToString())))
            throw new InvalidDataException("Invalid supported title ID.");
    }
    internal static bool ValidId(string id) => id.Length is > 0 and <= 10 && id.All(c => c is >= '0' and <= '9');
    internal static JObject Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Unexpected content after catalog JSON.");
        return result;
    }
    public async Task<JObject> ReadTitleAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id) || Index[id] is not JObject summary) throw new InvalidDataException("Title is absent from the catalog index.");
        if (!Indexed) return summary;
        ct.ThrowIfCancellationRequested();
        var version = Version(id);
        if (Volatile.Read(ref cachedTitle) is { } cached && cached.Id == id && cached.Version == version) return cached.Block;
        var data = Parse(await read(id + "/Data.json", ct).ConfigureAwait(false));
        if (data.Count != 2 || data.Properties().Last().Name != "SupportedTitleIDs" || data[id] is not JObject block
            || data["SupportedTitleIDs"] is not JArray ids || ids.Count != 1 || ids[0]!.ToString() != id)
            throw new InvalidDataException("Title file must contain its own title block and SupportedTitleIDs last.");
        EventCatalogSchema.ValidateTitle(block);
        // Runtime status comes from this title file; the index must agree with it.
        var actual = new JObject(block.Properties().Where(p => p.Name != "Achievements").Select(p => new JProperty(p.Name, p.Value.DeepClone())));
        if (!JToken.DeepEquals(actual, summary)) throw new InvalidDataException("Title file and index disagree.");
        Volatile.Write(ref cachedTitle, new(id, version, block));
        return block;
    }
    public string Version(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Invalid title ID.");
        if (directory == null) return "embedded";
        string Stamp(string path) { var file = new FileInfo(path); return file.Exists ? file.LastWriteTimeUtc.Ticks + ":" + file.Length : "missing"; }
        return Stamp(Indexed ? Path.Combine(directory, id, "Data.json") : IndexPath(directory)) + ":" + Stamp(TemplatePath(directory, id));
    }
    public Task<string> ReadTemplateAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id) || Index[id] is not JObject) throw new InvalidDataException("Unknown title ID.");
        return read(Indexed ? id + "/" + id + ".json" : id + ".json", ct);
    }
    public string TemplatePath(string directory, string id) => Path.Combine(directory, Indexed ? id : "", id + ".json");
    public async Task<JObject> ReadAllAsync(CancellationToken ct)
    {
        if (!Indexed) return Index;
        var result = new JObject();
        foreach (var title in Index.Properties().Where(p => ValidId(p.Name)))
            result[title.Name] = (await ReadTitleAsync(title.Name, ct).ConfigureAwait(false)).DeepClone();
        result["SupportedTitleIDs"] = Index["SupportedTitleIDs"]!.DeepClone();
        return result;
    }
}
