using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop;

public sealed record GpdCatalogRow(string TitleId, string Name, string Detail, bool InProfile)
{
    public bool CanAdd => !InProfile;
}

// Public GPD catalog: download-only, verified against the index; contributions are exported locally and attached by the user.
public sealed partial class DesktopModel
{
    private Xbox360GpdCatalogClient? gpdCatalog;
    private IReadOnlyCollection<Xbox360GpdCatalogClient.Title> gpdCatalogTitles = [];
    private string gpdCatalogSearch = "";
    private GpdCatalogRow[] gpdCatalogRows = [];
    private static readonly HttpClient gpdHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
    private Xbox360GpdCatalogClient GpdCatalog => gpdCatalog ??= new(gpdHttp, GpdDirectory);
    public string ContributionDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(GpdDirectory))!, "GPD contributions");
    public string GpdCatalogSearch { get => gpdCatalogSearch; set { gpdCatalogSearch = value ?? ""; Changed(); DrawGpdCatalog(); } }
    public GpdCatalogRow[] GpdCatalogRows { get => gpdCatalogRows; private set { gpdCatalogRows = value; Changed(); } }
    // Like mobile, only offered for the open game when the loaded catalog does not already carry it.
    public bool CanContributeXbox360Gpd => GpdContributions && !string.IsNullOrEmpty(loadedXbox360Profile) && Xbox360GpdCatalogClient.IsTitleId(Xbox360EditorTitleId) &&
        gpdCatalogTitles.Count != 0 && !gpdCatalogTitles.Any(t => t.TitleId.Equals(Xbox360EditorTitleId, StringComparison.OrdinalIgnoreCase));

    public async Task LoadGpdCatalogAsync()
    {
        if (busy) return;
        Busy(true);
        try
        {
            gpdCatalogTitles = await GpdCatalog.LoadAsync(lifetime.Token);
            DrawGpdCatalog();
            GpdAddStatus = $"{gpdCatalogTitles.Count} catalog titles available.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { GpdAddStatus = error.Message; }
        finally { Busy(false); }
    }

    private void DrawGpdCatalog()
    {
        var inProfile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!string.IsNullOrEmpty(loadedXbox360Profile))
                foreach (var entry in packages.InspectPackage(loadedXbox360Profile).Entries) inProfile.Add(entry.Path);
        }
        catch { }
        var query = GpdCatalogSearch.Trim();
        GpdCatalogRows = gpdCatalogTitles
            .Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.TitleId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Take(200)
            .Select(t => new GpdCatalogRow(t.TitleId, t.Name.Length > 0 ? t.Name : "Title " + t.TitleId,
                $"{t.TitleId} · {t.Achievements} achievements · {t.Gamerscore} G", inProfile.Contains(t.TitleId + ".gpd")))
            .ToArray();
        RefreshXbox360Files();
    }

    public async Task AddCatalogGameAsync(GpdCatalogRow row)
    {
        if (busy || row.InProfile) return;
        if (!CanModifyXbox360Profile || string.IsNullOrEmpty(loadedXbox360Profile)) { await AddXbox360GameAsync(row.TitleId, row.Name); return; }
        Busy(true);
        try
        {
            GpdAddStatus = $"Downloading and verifying {row.TitleId}.gpd…";
            await GpdCatalog.DownloadAsync(row.TitleId, lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { GpdAddStatus = $"Could not download {row.TitleId}.gpd: {error.Message}"; return; }
        finally { Busy(false); }
        await AddXbox360GameAsync(row.TitleId, row.Name);
        DrawGpdCatalog();
    }

    public void ContributeXbox360Gpd()
    {
        if (!CanContributeXbox360Gpd) { Xbox360UnlockStatus = GpdAddStatus = "Turn on GPD contributions in Settings, load the catalog and open a game it does not include."; return; }
        var key = Xbox360EditorTitleId.ToUpperInvariant();
        try
        {
            var locked = Xbox360GpdTemplate.Reset(packages.ReadEntryBytes(loadedXbox360Profile, key + ".gpd"));
            Directory.CreateDirectory(ContributionDirectory);
            var path = Path.Combine(ContributionDirectory, key + ".gpd");
            File.WriteAllBytes(path, locked);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ContributionDirectory) { UseShellExecute = true });
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Xbox360GpdCatalogClient.IssueUri + Uri.EscapeDataString("GPD contribution: " + key)) { UseShellExecute = true });
            Xbox360UnlockStatus = GpdAddStatus = $"Exported a locked copy to {path}. Attach it to the GitHub issue that opened. Nothing was uploaded automatically.";
        }
        catch (Exception error) { Xbox360UnlockStatus = GpdAddStatus = $"Could not export {key}.gpd: {error.Message}"; }
    }
}
