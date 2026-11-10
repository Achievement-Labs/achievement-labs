using AchievementLabs.Core;
using System.Text.Json;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private string publicCatalogQuery = "", publicCatalogStatus = "Bundled catalog: first 100 titles. Search names or IDs for current hosted results.";
    private bool searchingPublicCatalog;
    private CancellationTokenSource? publicCatalogSearchCancellation;
    private static readonly HttpClient publicMetadataHttp = PublicCatalogClient.CreateHttpClient();
    private PublicCatalogClient.Title[] publicCatalogTitles = PublicCatalogClient.BundledSearch("").Items;
    public string PublicCatalogQuery { get => publicCatalogQuery; set { publicCatalogQuery = value ?? ""; publicCatalogSearchCancellation?.Cancel(); Changed(); } }
    public string PublicCatalogStatus { get => publicCatalogStatus; private set { publicCatalogStatus = value; Changed(); } }
    public PublicCatalogClient.Title[] PublicCatalogTitles { get => publicCatalogTitles; private set { publicCatalogTitles = value; Changed(); } }
    public bool CanSearchPublicCatalog => !searchingPublicCatalog;
    public async Task SearchPublicCatalogAsync()
    {
        if (searchingPublicCatalog) return;
        searchingPublicCatalog = true; Changed(nameof(CanSearchPublicCatalog));
        using var search = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        publicCatalogSearchCancellation = search;
        var query = PublicCatalogQuery.Trim();
        PublicCatalogTitles = PublicCatalogClient.BundledSearch(query).Items;
        PublicCatalogStatus = "Showing local results; checking the hosted catalog...";
        try
        {
            PublicCatalogClient.Page page;
            try
            {
                if (OfflineChecks) throw new IOException("Offline preview");
                page = await new PublicCatalogClient(publicMetadataHttp).SearchAsync(query, search.Token);
            }
            catch (OperationCanceledException) when (search.IsCancellationRequested) { return; }
            catch { page = PublicCatalogClient.BundledSearch(query); }
            PublicCatalogTitles = page.Items;
            PublicCatalogStatus = $"{(page.Offline ? "Bundled offline catalog" : "Hosted official catalog")} · {page.TotalMatched} matches · showing {page.Items.Length}."
                + (page.TotalMatched > page.Items.Length ? " Refine your search to see other matches." : "")
                + $" Revision {page.CatalogRevision[..12]}.";
        }
        finally { publicCatalogSearchCancellation = null; searchingPublicCatalog = false; Changed(nameof(CanSearchPublicCatalog)); }
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
