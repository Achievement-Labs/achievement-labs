using System.Collections.Concurrent;
using System.Text.Json;

namespace AchievementLabs.MultiSelect;

// Explicit edition-specific mappings reviewed against the user's Xbox export.
// Expected signatures omit only transport/session/account fields. No events are synthesized.
public static class EditionMapping
{
    public sealed class Entry
    {
        public string XboxId {get;set;}="";
        public string CatalogId {get;set;}="";
        public string Name {get;set;}="";
        public JsonElement Expected {get;set;}
    }
    public sealed class Game
    {
        public string TitleId {get;set;}="";
        public string Name {get;set;}="";
        public string[] CatalogIds {get;set;}=[];
        public string[] XboxIds {get;set;}=[];
        public Entry[] Entries {get;set;}=[];
    }
    static readonly Dictionary<string,Game> Games=Load();
    enum Shape {Unknown, Legacy, Corrected}
    static readonly ConcurrentDictionary<string,Shape> Shapes=new();
    static Dictionary<string,Game> Load()
    {
        using var stream=typeof(EditionMapping).Assembly.GetManifestResourceStream("AchievementLabs.MultiSelect.EditionMappings.json")
            ?? throw new InvalidOperationException("Edition mapping data is missing.");
        var games=JsonSerializer.Deserialize<Game[]>(stream)!;
        foreach(var g in games)
        {
            if(g.Entries.Select(e=>e.XboxId).Distinct().Count()!=g.Entries.Length ||
               g.Entries.Select(e=>e.CatalogId).Distinct().Count()!=g.Entries.Length ||
               g.Entries.Any(e=>!g.XboxIds.Contains(e.XboxId) || !g.CatalogIds.Contains(e.CatalogId) ||
                   e.Expected.ValueKind!=JsonValueKind.Array || e.Expected.GetArrayLength()==0))
                throw new InvalidOperationException("Invalid edition mapping data.");
        }
        return games.ToDictionary(g=>g.TitleId,StringComparer.Ordinal);
    }
    public static bool Supports(string titleId)=>Games.ContainsKey(titleId);
    static bool Same(string[] actual,string[] expected)=>actual.Length==expected.Length &&
        new HashSet<string>(actual,StringComparer.Ordinal).SetEquals(expected);
    public static async Task<T> NormalizeCatalog<T>(Task<T> source,string titleId)
    {
        Shapes[titleId]=Shape.Unknown;
        T value=await source.ConfigureAwait(false);
        var g=Games[titleId];
        var property=value?.GetType().GetProperty("AchievementIds");
        if(property?.GetValue(value) is not IEnumerable<string> supplied)
            throw new InvalidOperationException($"{g.Name}: unrecognised event catalog format.");
        var ids=supplied.ToArray();
        var shape=Same(ids,g.CatalogIds)?Shape.Legacy:Same(ids,g.XboxIds)?Shape.Corrected:Shape.Unknown;
        if(shape==Shape.Unknown)
            throw new InvalidOperationException($"{g.Name}: the event catalog has changed. Its mapping needs review before retrying.");
        // Expose only achievements for which an explicit reviewed translation exists.
        property.SetValue(value,g.Entries.Select(e=>e.XboxId).ToArray());
        Shapes[titleId]=shape;
        return value;
    }
    static Entry GetEntry(string titleId,string xboxId)
    {
        var g=Games[titleId];
        return g.Entries.SingleOrDefault(e=>e.XboxId==xboxId) ??
            throw new InvalidOperationException($"{g.Name}: no reviewed event mapping exists for this achievement.");
    }
    public static string PayloadId(string titleId,string xboxId)
    {
        if(!Shapes.TryGetValue(titleId,out var shape) || shape==Shape.Unknown)
            throw new InvalidOperationException("Load or refresh this game's achievements before retrying, so its event mapping can be checked.");
        var entry=GetEntry(titleId,xboxId);
        return shape==Shape.Legacy?entry.CatalogId:entry.XboxId;
    }
    public static async Task<T> ValidatePayloads<T>(Task<T> source,string titleId,string xboxId)
    {
        T value=await source.ConfigureAwait(false);
        if(!Supports(titleId))return value;
        var entry=GetEntry(titleId,xboxId);
        try
        {
            if(value?.GetType().GetProperty("Payloads")?.GetValue(value) is not IEnumerable<string> raw)
                throw new InvalidOperationException();
            var signatures=new List<object>();
            foreach(var text in raw)
            {
                using var doc=JsonDocument.Parse(text);
                var e=doc.RootElement;var b=e.GetProperty("data").GetProperty("baseData");
                JsonElement? Optional(string key)=>b.TryGetProperty(key,out var p)?p.Clone():null;
                signatures.Add(new {Event=e.GetProperty("name").GetString(),
                    BaseName=b.TryGetProperty("name",out var n)?n.GetString():null,
                    TitleId=b.GetProperty("titleId").ToString(), Scid=b.GetProperty("serviceConfigId").GetString(),
                    Properties=Optional("properties"), Measurements=Optional("measurements")});
            }
            if(!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(signatures),entry.Expected))
                throw new InvalidOperationException();
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Shapes[titleId]=Shape.Unknown;
            throw new InvalidOperationException($"{entry.Name}: the supplied event data differs from the reviewed mapping. This achievement's event data was not submitted; the mapping needs review.",ex);
        }
        return value;
    }
}
