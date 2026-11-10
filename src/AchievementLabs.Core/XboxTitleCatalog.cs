using System.IO.Compression;
using System.Text.Json;

namespace AchievementLabs.Core;

/// <summary>Metadata-only snapshot exported from the author's SQLite catalog; no account data or recipes.</summary>
public static class XboxTitleCatalog
{
    public sealed record Entry(string TitleId,string Name,string[] Devices,string[] Platforms,int? SourceVersion,
        string? AchievementsType,string? StatsType,int? TotalAchievements,string? ModernTitleId)
    {
        private string? searchKey;
        public string SearchKey => searchKey ??= TitleSearchText.Key(Name);
        public bool? EventBased => AchievementsType switch { "Event Based"=>true,"Title Based"=>false,_=>null };
    }
    private sealed record Document(int SchemaVersion,string Revision,Entry[] Titles);
    private static readonly Lazy<Document> Data=new(Load);
    private static readonly Lazy<Dictionary<string,Entry>> ById=new(()=>Data.Value.Titles.ToDictionary(t=>t.TitleId,StringComparer.Ordinal));
    public static int Count=>Data.Value.Titles.Length;
    public static string Revision=>Data.Value.Revision;
    public static Entry? Find(string id)=>ById.Value.GetValueOrDefault(id);
    private static Document Load()
    {
        using var source=typeof(XboxTitleCatalog).Assembly.GetManifestResourceStream("AchievementLabs.XboxTitleCatalog.json.gz")
            ?? throw new InvalidDataException("Bundled Xbox title catalog is missing.");
        using var gzip=new GZipStream(source,CompressionMode.Decompress);
        var data=JsonSerializer.Deserialize<Document>(gzip)??throw new InvalidDataException("Xbox title catalog is empty.");
        if(data.SchemaVersion!=1 || data.Revision.Length!=64 || data.Titles.Length>100000 || data.Titles.Length==0 ||
           data.Titles.Any(t=>!uint.TryParse(t.TitleId,out var id) || id==0 || t.TitleId!=id.ToString() || string.IsNullOrWhiteSpace(t.Name) || t.Platforms is not {Length:>0} || t.Devices==null ||
               t.AchievementsType is not (null or "Event Based" or "Title Based") || t.StatsType is not (null or "Event Based" or "Title Based") || t.TotalAchievements is <=0 or >10000) ||
           data.Titles.Select(t=>t.TitleId).Distinct().Count()!=data.Titles.Length)throw new InvalidDataException("Invalid Xbox title metadata snapshot.");
        return data;
    }
    public static Entry[] SearchEntries(string query)
    {
        query=query.Trim();
        if(query.Length>200)throw new ArgumentException("Search must be at most 200 characters.");
        var key=TitleSearchText.Key(query);
        return Data.Value.Titles.Where(t=>query.Length==0 || t.TitleId==query || t.ModernTitleId==query ||
            (key.Length>0 ? t.SearchKey.Contains(key,StringComparison.Ordinal) : t.Name.Contains(query,StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t=>t.Name,StringComparer.OrdinalIgnoreCase).ThenBy(t=>t.TitleId,StringComparer.Ordinal).ToArray();
    }
    public static PublicCatalogClient.Page Search(string query)
    {
        var matches=SearchEntries(query);
        var items=matches.Take(100).Select(t=>
        {
            PublicCatalogClient.BundledTitles.TryGetValue(t.TitleId,out var recipe);
            return new PublicCatalogClient.Title(t.TitleId,t.Name,[],t.Platforms,recipe?.Status??"Unknown",recipe?.EventAvailable??false,
                recipe?.MappedAchievementCount??0,t.TotalAchievements??recipe?.DefinitionAchievementCount)
                { AchievementsType=t.AchievementsType??(t.SourceVersion==1 ? "Legacy" : null),StatsType=t.StatsType };
        }).ToArray();
        return new(Revision,"author-sqlite-snapshot",matches.Length,items,null) { Offline=true };
    }
}
