namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly AppUpdater updater = new();
    private bool automaticallyCheckUpdates = true, checkingUpdate, updateReady;
    private string updateStatus = "Updates are checked automatically. Current build: " + AppUpdater.CurrentVersion;
    private string? updateDownload;
    private AvailableUpdate? availableUpdate;
    public bool AutomaticallyCheckUpdates { get => automaticallyCheckUpdates; set { automaticallyCheckUpdates = value; Changed(); } }
    public string UpdateStatus { get => updateStatus; private set { updateStatus = value; Changed(); } }
    public bool UpdateReady { get => updateReady; private set { updateReady = value; Changed(); } }
    public bool CanCheckUpdates => !checkingUpdate;
    public async Task CheckForUpdatesAsync(bool manual = false)
    {
        if (checkingUpdate || UpdateReady || (!manual && !AutomaticallyCheckUpdates)) return;
        checkingUpdate = true; Changed(nameof(CanCheckUpdates));
        try
        {
            UpdateStatus = "Checking for updates…";
            availableUpdate = await updater.CheckAsync(AppUpdater.CurrentVersion, lifetime.Token);
            if (availableUpdate == null) { UpdateStatus = "You’re up to date. Build " + AppUpdater.CurrentVersion; return; }
            UpdateStatus = "Downloading update " + availableUpdate.Manifest.Version + "…";
            updateDownload = await updater.DownloadAsync(availableUpdate, lifetime.Token);
            UpdateReady = true;
            UpdateStatus = "Update " + availableUpdate.Manifest.Version + " is ready. Install and restart when your work is saved.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { UpdateStatus = "Update check/download failed: " + error.Message + " Retry in Settings. Your current app is unchanged."; }
        finally { checkingUpdate = false; Changed(nameof(CanCheckUpdates)); }
    }
    public bool InstallUpdate()
    {
        if (!UpdateReady || updateDownload == null || availableUpdate == null) return false;
        if (busy || QueueActive || WorkflowBusy || PresenceRunning || PendingXbox360EditCount > 0)
        {
            UpdateStatus = "Save pending profile edits and finish active operations before installing the update.";
            return false;
        }
        try { AppUpdater.StartInstall(updateDownload, availableUpdate.Manifest); return true; }
        catch (Exception error) { UpdateStatus = "Could not install update: " + error.Message + " Move the EXE to a writable folder or download the release manually."; return false; }
    }
}
