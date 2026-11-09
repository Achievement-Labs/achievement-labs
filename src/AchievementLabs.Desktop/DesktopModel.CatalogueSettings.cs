using AchievementLabs.Core;
using AchievementLabs.MultiSelect;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private bool eventRefreshRunning;
    private string eventCatalogueStatus = "Bundled event data loaded. Upstream checks refresh mappings and support status.";
    public bool CanRefreshEventCatalogue => !eventRefreshRunning;
    public async Task ImportEventPackAsync(string path)
    {
        if (eventRefreshRunning || !CanInteract) return;
        if (QueueActive || busy) { EventCatalogueStatus = "Pause Auto Unlock and wait for current actions before updating event data."; return; }
        eventRefreshRunning = true; Changed(nameof(CanRefreshEventCatalogue)); Changed(nameof(CanQuery)); Changed(nameof(CanInteract)); NotifyActions();
        try {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Event pack exceeds 64 MB.");
            await eventCatalog.ImportEventPackAsync(await File.ReadAllTextAsync(path, lifetime.Token), lifetime.Token);
            EventCatalogueStatus = eventCatalog.UpstreamStatus;
            RefreshLibrarySupport(); await RefreshSelectedEventSupportAsync();
        } catch (OperationCanceledException) { }
        catch { EventCatalogueStatus = "Could not import event pack. Choose a pack containing templates and mappings (up to 64 MB). Existing data retained."; }
        finally { eventRefreshRunning = false; Changed(nameof(CanRefreshEventCatalogue)); Changed(nameof(CanQuery)); Changed(nameof(CanInteract)); NotifyActions(); }
    }
    public string EventCatalogueStatus { get => eventCatalogueStatus; private set { eventCatalogueStatus = value; Changed(); } }
    public async Task RefreshEventCatalogueAsync(bool force = true, bool startup = false)
    {
        if (eventRefreshRunning || OfflineChecks) return;
        if (QueueActive || busy) { if (force) EventCatalogueStatus = "Pause Auto Unlock and wait for current actions before updating event data."; return; }
        eventRefreshRunning = true; Changed(nameof(CanRefreshEventCatalogue)); Changed(nameof(CanQuery)); Changed(nameof(CanInteract)); NotifyActions();
        EventCatalogueStatus = "Checking the official repository for event mapping/status updates…";
        try {
#if LABS_UPDATE_TESTING
            using var http = UpdateTestTransport.CreateClient();
#else
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
#endif
            await eventCatalog.RefreshUpstreamAsync(http, force, lifetime.Token, startup);
            EventCatalogueStatus = eventCatalog.UpstreamStatus;
#if LABS_UPDATE_TESTING
            EventCatalogueStatus += eventCatalog.GetLibrarySupport().ContainsKey("4294967294") ? " · Test marker: RECEIVED" : " · Test marker: not present";
            Directory.CreateDirectory(AchievementLabsPaths.UpdateTestRoot);
            File.WriteAllText(AchievementLabsPaths.LocalFile("catalog-test-status.txt"), EventCatalogueStatus);
#endif
            RefreshLibrarySupport(); await RefreshSelectedEventSupportAsync();
        } catch (OperationCanceledException) { }
        catch { EventCatalogueStatus = "Could not check upstream event data. Existing mappings retained."; }
        finally { eventRefreshRunning = false; Changed(nameof(CanRefreshEventCatalogue)); Changed(nameof(CanQuery)); Changed(nameof(CanInteract)); NotifyActions(); }
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
