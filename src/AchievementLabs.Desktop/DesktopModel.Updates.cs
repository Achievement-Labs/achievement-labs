namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly AppUpdater updater = new();
    private bool automaticallyCheckUpdates = true, checkingUpdate, updateReady;
    private string updateStatus = "Updates are checked automatically. Current build: " + AppUpdater.CurrentDisplayVersion;
    private string? updateDownload;
    private AvailableUpdate? availableUpdate;
    public Func<string, bool, Task<bool>>? UpdatePrompt { get; set; }
    public bool AutomaticallyCheckUpdates { get => automaticallyCheckUpdates; set { automaticallyCheckUpdates = value; Changed(); } }
    public string UpdateStatus { get => updateStatus; private set { updateStatus = value; Changed(); } }
    public bool UpdateReady { get => updateReady; private set { updateReady = value; Changed(); } }
    public bool CanCheckUpdates => !checkingUpdate;
    public async Task CheckForUpdatesAsync(bool manual = false, bool startup = false)
    {
#if LABS_OPTIMIZATION_TEST
        UpdateStatus = "Local optimization test build; app updates are disabled.";
        await Task.CompletedTask;
#else
        if (OperatingSystem.IsLinux()) { UpdateStatus = "Linux preview updates: download the Linux package manually from Achievement-Labs/achievement-labs releases."; return; }
        if (OperatingSystem.IsMacOS()) { UpdateStatus = "Mac preview updates: download manually from Achievement-Labs/achievement-labs releases. Windows packages are excluded."; return; }
        if (checkingUpdate || UpdateReady || (!manual && !startup && !AutomaticallyCheckUpdates)) return;
        checkingUpdate = true; Changed(nameof(CanCheckUpdates));
        try
        {
            UpdateStatus = "Checking for updates…";
            availableUpdate = await updater.CheckAsync(AppUpdater.CurrentVersion, lifetime.Token);
            if (availableUpdate == null) { UpdateStatus = "You’re up to date. Build " + AppUpdater.CurrentDisplayVersion; return; }
            var version = AppUpdater.DisplayVersion(Version.Parse(availableUpdate.Manifest.Version));
            UpdateStatus = "Update " + version + " is available.";
            if (UpdatePrompt != null && !await UpdatePrompt(version, false))
            { UpdateStatus += " Download it from Settings → Updates when ready."; return; }
            UpdateStatus = "Downloading update " + version + "…";
            updateDownload = await updater.DownloadAsync(availableUpdate, lifetime.Token);
            UpdateReady = true;
            UpdateStatus = OperatingSystem.IsMacOS() ? "Mac update verified. Reveal the ZIP, quit the app, and replace it manually in Applications." : "Update " + version + " is ready. Install and restart when your work is saved.";
            if (UpdatePrompt != null) await UpdatePrompt(version, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { UpdateStatus = "Update check/download failed: " + error.Message + " Retry in Settings. Your current app is unchanged."; }
        finally { checkingUpdate = false; Changed(nameof(CanCheckUpdates)); }
#endif
    }
    public bool InstallUpdate()
    {
        if (!UpdateReady || updateDownload == null || availableUpdate == null) return false;
        if (busy || QueueActive || WorkflowBusy || PresenceRunning || PendingXbox360EditCount > 0)
        {
            UpdateStatus = "Save pending profile edits and finish active operations before installing the update.";
            return false;
        }
        try { AppUpdater.StartInstall(updateDownload, availableUpdate.Manifest); return !OperatingSystem.IsMacOS(); }
        catch (Exception error) { UpdateStatus = "Could not install update: " + error.Message + " Move the EXE to a writable folder or download the release manually."; return false; }
    }
}
