using AchievementLabs.Core;
using AchievementLabs.MultiSelect;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private bool eventRefreshRunning;
    private string eventCatalogueStatus = "Bundled event data loaded. Automatic upstream checks add new titles only.";
    public bool CanRefreshEventCatalogue => !eventRefreshRunning;
    public string EventCatalogueStatus { get => eventCatalogueStatus; private set { eventCatalogueStatus = value; Changed(); } }
    public async Task RefreshEventCatalogueAsync(bool force = true)
    {
        if (eventRefreshRunning || !force && !AutoRefreshEvents || OfflineChecks) return;
        eventRefreshRunning = true; Changed(nameof(CanRefreshEventCatalogue));
        EventCatalogueStatus = "Checking the official repository for new event titles…";
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            await eventCatalog.RefreshUpstreamAsync(http, force, lifetime.Token);
            EventCatalogueStatus = eventCatalog.UpstreamStatus;
        } catch (OperationCanceledException) { }
        catch { EventCatalogueStatus = "Could not check upstream event data. Existing mappings retained."; }
        finally { eventRefreshRunning = false; Changed(nameof(CanRefreshEventCatalogue)); }
    }
    private bool dlcRefreshRunning;
    private string dlcCatalogueStatus = "Bundled DLC sections loaded. Automatic checks run daily while the app is open.";
    public bool CanRefreshDlcCatalogue => !dlcRefreshRunning;
    public string DlcCatalogueStatus { get => dlcCatalogueStatus; private set { dlcCatalogueStatus = value; Changed(); } }
    private void UpdateDlcCatalogueStatus()
    {
        var catalogue = SharedDlcCatalogue.Current;
        DlcCatalogueStatus = $"{catalogue.LastRefreshStatus} · {catalogue.Count:N0} title lists" +
            (catalogue.LastSuccessfulRefresh is { } refreshed ? $" · Last successful check: {refreshed.ToLocalTime():g}" : "");
    }
    public async Task RefreshDlcCatalogueAsync(bool force = true)
    {
        if (dlcRefreshRunning || !force && !AutoRefreshDlc || OfflineChecks) return;
        dlcRefreshRunning = true; Changed(nameof(CanRefreshDlcCatalogue));
        DlcCatalogueStatus = "Refreshing DLC and title-update sections…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            await SharedDlcCatalogue.Current.RefreshAsync(http, AchievementLabsPaths.LocalFile("shared-achievement-packs.json"), lifetime.Token, force);
            UpdateDlcCatalogueStatus();
            Changed(nameof(VisibleAchievements)); RefreshLibraryPresentation();
        }
        catch (OperationCanceledException) { }
        catch { DlcCatalogueStatus = "Could not refresh the catalogue. Existing sections were retained."; }
        finally { dlcRefreshRunning = false; Changed(nameof(CanRefreshDlcCatalogue)); }
    }
    public void OpenDebugReports()
    {
        var folder = ExportBaseFolder();
        if (folder == null) { Notice = "No export reports folder exists yet. Export achievements first."; return; }
        if (CompletionAccount is { } account)
        {
            var scoped = Path.Combine(folder, "AchievementLabs-export-" + account);
            if (Directory.Exists(scoped)) folder = scoped;
        }
        try
        {
            if (!Directory.Exists(folder)) { Notice = "The saved export reports folder is no longer available."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { Notice = "Could not open the export reports folder."; }
    }
    public void OpenCatalogueReview(string review)
    {
        var file = review == "bulk" ? "bulk-dlc-2026-10-08.json" : "dlc-corrections-2026-10-08.json";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://github.com/Achievement-Labs/achievement-labs/blob/main/catalog/reviews/" + file) { UseShellExecute = true }); }
        catch { Notice = "Could not open the catalogue review."; }
    }
}
