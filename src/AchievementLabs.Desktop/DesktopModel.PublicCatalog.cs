using AchievementLabs.Core;
using System.Text.Json;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private string publicCatalogQuery = "", publicCatalogStatus = "Bundled catalog: first 100 titles. Search names or IDs for current hosted results.";
    private bool searchingPublicCatalog;
    private PublicCatalogClient.Title[] publicCatalogTitles = PublicCatalogClient.BundledSearch("").Items;
    public string PublicCatalogQuery { get => publicCatalogQuery; set { publicCatalogQuery = value ?? ""; Changed(); } }
    public string PublicCatalogStatus { get => publicCatalogStatus; private set { publicCatalogStatus = value; Changed(); } }
    public PublicCatalogClient.Title[] PublicCatalogTitles { get => publicCatalogTitles; private set { publicCatalogTitles = value; Changed(); } }
    public bool CanSearchPublicCatalog => !searchingPublicCatalog;
    public async Task SearchPublicCatalogAsync()
    {
        if (searchingPublicCatalog) return;
        searchingPublicCatalog = true; Changed(nameof(CanSearchPublicCatalog));
        var query = PublicCatalogQuery.Trim();
        try
        {
            PublicCatalogClient.Page page;
            try
            {
                if (OfflineChecks) throw new IOException("Offline preview");
                using var http = PublicCatalogClient.CreateHttpClient();
                page = await new PublicCatalogClient(http).SearchAsync(query, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch { page = PublicCatalogClient.BundledSearch(query); }
            PublicCatalogTitles = page.Items;
            PublicCatalogStatus = $"{(page.Offline ? "Bundled offline catalog" : "Hosted official catalog")} · {page.TotalMatched} matches · showing {page.Items.Length}."
                + (page.TotalMatched > page.Items.Length ? " Refine your search to see other matches." : "")
                + $" Revision {page.CatalogRevision[..12]}.";
        }
        finally { searchingPublicCatalog = false; Changed(nameof(CanSearchPublicCatalog)); }
    }
    private async Task RefreshApiTotalsAsync(CancellationToken ct)
    {
        if (OfflineChecks) return;
        var path = AchievementLabsPaths.LocalFile("public-api-totals.json");
        try { if (File.Exists(path)) sharedLibraryTotals.Merge(await File.ReadAllTextAsync(path, ct)); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { }
        try
        {
            var ids = Games.Where(g => g.Total <= 0).Select(g => g.Id).Distinct().Take(5000).ToArray();
            if (ids.Length == 0) return;
            using var http = PublicCatalogClient.CreateHttpClient();
            var entries = await new PublicCatalogClient(http).TotalsAsync(ids, ct);
            var json = JsonSerializer.Serialize(new SharedAchievementTotals.Catalogue(1, entries), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            sharedLibraryTotals.Merge(json);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp"; await File.WriteAllTextAsync(temporary, sharedLibraryTotals.Export(), ct); File.Move(temporary, path, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    }
}
