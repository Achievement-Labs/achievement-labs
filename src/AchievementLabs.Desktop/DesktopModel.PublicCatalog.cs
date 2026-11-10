using AchievementLabs.Core;
using System.Text.Json;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private string publicCatalogQuery = "", publicCatalogStatus = "Local Xbox catalog; search names or exact title IDs. No Xbox requests are made by catalog search.";
    private CancellationTokenSource? publicCatalogSearchCancellation;
    private static readonly HttpClient publicMetadataHttp = PublicCatalogClient.CreateHttpClient();
    private PublicCatalogClient.Title[] publicCatalogTitles = XboxTitleCatalog.Search("").Items;
    public string PublicCatalogQuery { get => publicCatalogQuery; set { publicCatalogQuery=value??""; publicCatalogSearchCancellation?.Cancel(); Changed(); } }
    public string PublicCatalogStatus { get => publicCatalogStatus; private set { publicCatalogStatus=value; Changed(); } }
    public PublicCatalogClient.Title[] PublicCatalogTitles { get => publicCatalogTitles; private set { publicCatalogTitles=value; Changed(); } }
    public bool CanSearchPublicCatalog => true;
    public async Task SearchPublicCatalogAsync()
    {
        publicCatalogSearchCancellation?.Cancel();
        using var search=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        publicCatalogSearchCancellation=search;
        try
        {
            var query=PublicCatalogQuery;
            var page=await Task.Run(()=>XboxTitleCatalog.Search(query),search.Token);
            if(search.IsCancellationRequested)return;
            PublicCatalogTitles=page.Items;
            PublicCatalogStatus=$"Local SQLite-derived catalog: {XboxTitleCatalog.Count:N0} titles | {page.TotalMatched:N0} matches | showing {page.Items.Length}. No Xbox requests.";
        }
        catch(OperationCanceledException) { }
        catch(ArgumentException error) { PublicCatalogStatus=error.Message; }
        finally { if(ReferenceEquals(publicCatalogSearchCancellation,search))publicCatalogSearchCancellation=null; }
    }
    private async Task RefreshApiTotalsAsync(CancellationToken ct)
    {
        if (OfflineChecks) return;
        var path = AchievementLabsPaths.LocalFile("public-api-totals.json");
        try { if (File.Exists(path)) sharedLibraryTotals.Merge(await File.ReadAllTextAsync(path, ct)); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { }
        try
        {
            var checkedPath = AchievementLabsPaths.LocalFile("public-api-totals-checked.json");
            var checkedIds = new Dictionary<string, DateTimeOffset>();
            try { if (File.Exists(checkedPath)) checkedIds = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(await File.ReadAllTextAsync(checkedPath, ct)) ?? new(); }
            catch (Exception error) when (error is IOException or JsonException) { }
            var ids = Games.Where(g => g.Total <= 0 && !sharedLibraryTotals.TryGet(g.Id, g.Platform, out _) &&
                (!checkedIds.TryGetValue(g.Id, out var checkedAt) || checkedAt < DateTimeOffset.UtcNow.AddDays(-1)))
                .Select(g => g.Id).Distinct().Take(100).ToArray();
            if (ids.Length == 0) return;
            var entries = await new PublicCatalogClient(publicMetadataHttp).TotalsAsync(ids, ct);
            var json = JsonSerializer.Serialize(new SharedAchievementTotals.Catalogue(1, entries), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            sharedLibraryTotals.Merge(json);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp"; await File.WriteAllTextAsync(temporary, sharedLibraryTotals.Export(), ct); File.Move(temporary, path, true);
            foreach (var id in ids) checkedIds[id] = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(checkedPath)!);
            var checkedTemporary = checkedPath + ".tmp";
            await File.WriteAllTextAsync(checkedTemporary, JsonSerializer.Serialize(checkedIds.Where(p => p.Value >= DateTimeOffset.UtcNow.AddDays(-1)).ToDictionary(p => p.Key, p => p.Value)), ct);
            File.Move(checkedTemporary, checkedPath, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    }
}
