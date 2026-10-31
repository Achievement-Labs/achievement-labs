using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private readonly DesktopPreferencesStore preferencesStore = new(AchievementLabsPaths.LocalFile("native-settings.json"));
    private string eventsDirectory = new DesktopPreferences().EventsDirectory;
    private string sessionPath = new DesktopPreferences().SessionPath;
    private bool rememberWindowSize, autoMaximize = true;
    public bool RememberWindowSize { get => rememberWindowSize; set { rememberWindowSize = value; Changed(); } }
    public bool AutoMaximize { get => autoMaximize; set { autoMaximize = value; Changed(); } }
    private bool regionOverride, mintAccent, fakeSignature, privacyMode, autoSpoof, autoLaunch, launchHidden;
    public bool FakeSignature { get => fakeSignature; set { fakeSignature = value; queueAccount.Settings.FakeSignatureEnabled = value; Changed(); } }
    public bool PrivacyMode { get => privacyMode; set { privacyMode = value; Changed(); Changed(nameof(ProfileName)); Changed(nameof(ProfileImageUrl)); Changed(nameof(AccountSummary)); } }
    public bool AutoSpoof { get => autoSpoof; set { autoSpoof = value; Changed(); } }
    public bool AutoLaunchXboxApp { get => autoLaunch; set { autoLaunch = value; Changed(); } }
    public bool LaunchXboxAppHidden { get => launchHidden; set { launchHidden = value; Changed(); } }
    private bool windowsNotificationsEnabled = true, notifySpooferStops = true;
    public bool WindowsNotificationsEnabled { get => windowsNotificationsEnabled; set { windowsNotificationsEnabled = value; Changed(); } }
    public bool NotifySpooferStops { get => notifySpooferStops; set { notifySpooferStops = value; Changed(); } }
    private bool stopAutoUnlockOnSpoofFailure, notifyAutoUnlockCompletion = true;
    public bool StopAutoUnlockOnSpoofFailure { get => stopAutoUnlockOnSpoofFailure; set { stopAutoUnlockOnSpoofFailure = value; Changed(); SaveAutoUnlockPreferences(); } }
    public bool NotifyAutoUnlockCompletion { get => notifyAutoUnlockCompletion; set { notifyAutoUnlockCompletion = value; Changed(); SaveAutoUnlockPreferences(); } }
    public Action<string>? AutoUnlockCompleted { get; set; }
    public Action<string, string>? WindowsNotification { get; set; }
    public Action<string>? SpooferFailure { get; set; }
    public void NotifyWindows(string title, string message) { if (WindowsNotificationsEnabled && !lifetime.IsCancellationRequested) WindowsNotification?.Invoke(title, message); }
    public void TestWindowsNotification() => NotifyWindows("Achievement Labs", $"{NotificationsLabel} test.");
    private bool autoRefreshEvents = true;
    public bool AutoRefreshEvents { get => autoRefreshEvents; set { autoRefreshEvents = value; Changed(); } }
    private bool autoRefreshDlc = true, showMythicIcon = true, highlightCompletedDlcs = true, debugExpanded;
    private string mythicColour = "#70C98A", completedDlcColour = "#183A27";
    public bool AutoRefreshDlc { get => autoRefreshDlc; set { autoRefreshDlc = value; Changed(); } }
    public bool ShowMythicIcon { get => showMythicIcon; set { showMythicIcon = value; Changed(); RefreshLibraryPresentation(); } }
    public bool HighlightCompletedDlcs { get => highlightCompletedDlcs; set { highlightCompletedDlcs = value; Changed(); RefreshLibraryPresentation(); } }
    public bool DebugExpanded { get => debugExpanded; set { debugExpanded = value; Changed(); } }
    public string MythicColour { get => mythicColour; set { mythicColour = value ?? ""; Changed(); RefreshLibraryPresentation(); } }
    public string CompletedDlcColour { get => completedDlcColour; set { completedDlcColour = value ?? ""; Changed(); RefreshLibraryPresentation(); } }
    public void ResetCompletionColours() { MythicColour = "#70C98A"; CompletedDlcColour = "#183A27"; }
    private CatalogFinding[] findings = [];
    private string catalogSummary = "Choose an Events folder in Settings, then inspect its catalog.";
    public bool IsSettings => page == "Settings";
    public bool IsDiagnostics => page == "Diagnostics";
    public bool CanDisconnect => !CompletionScanRunning && !TotalsRunning && !PresenceRunning && !QueueActive && !busy && !WorkflowBusy && session != null;
    public string EventsDirectory { get => eventsDirectory; set { eventsDirectory = value; Changed(); } }
    public string SessionPath { get => sessionPath; set { sessionPath = value; Changed(); } }
    public bool RegionOverride { get => regionOverride; set { regionOverride = value; Changed(); } }
    public bool MintAccent { get => mintAccent; set { mintAccent = value; Changed(); } }
    public CatalogFinding[] CatalogFindings { get => findings; private set { findings = value; Changed(); } }
    public string CatalogSummary { get => catalogSummary; private set { catalogSummary = value; Changed(); } }
    public async Task LoadPreferencesAsync()
    {
        try { librarySort = AchievementLabs.Core.LibrarySortPreferences.Load(librarySortPath); Changed(nameof(LibrarySort)); Changed(nameof(VisibleGames)); }
        catch { Notice = "Saved library sort could not be read."; }
        try { var value = await preferencesStore.LoadAsync(lifetime.Token); if (AchievementLabs.Core.DesktopPlatform.IsUnixDesktop) RememberBrowserSession = (OperatingSystem.IsLinux() ? value.RememberLinuxSignIn : value.RememberMacSignIn) ?? File.Exists(NativeSessionStore.DefaultPath); AutoRefreshEvents = value.AutoRefreshEvents; RememberWindowSize = value.RememberWindowSize; AutoMaximize = value.AutoMaximize; AutomaticallyCheckUpdates = value.AutomaticallyCheckUpdates; EventsDirectory = value.EventsDirectory; SessionPath = value.SessionPath; RegionOverride = value.RegionOverride; MintAccent = value.MintAccent; UnlockAllEnabled = value.UnlockAllEnabled; OAuthProfile = value.OAuthProfile; FakeSignature = value.FakeSignature; PrivacyMode = value.PrivacyMode; AutoSpoof = value.AutoSpoof; BuildQueueBufferSeconds = value.BuildQueueBufferSeconds; AutoLaunchXboxApp = value.AutoLaunchXboxApp; LaunchXboxAppHidden = value.LaunchXboxAppHidden; WindowsNotificationsEnabled = value.WindowsNotificationsEnabled; NotifySpooferStops = value.NotifySpooferStops; StopAutoUnlockOnSpoofFailure = value.StopAutoUnlockOnSpoofFailure; NotifyAutoUnlockCompletion = value.NotifyAutoUnlockCompletion; AutoRefreshDlc = value.AutoRefreshDlc; ShowMythicIcon = value.ShowMythicIcon; HighlightCompletedDlcs = value.HighlightCompletedDlcs; MythicColour = value.MythicColour; CompletedDlcColour = value.CompletedDlcColour; await LoadSavedEventTokenAsync();
            if (OperatingSystem.IsWindows() && AutoLaunchXboxApp) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"shell:appsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App") { UseShellExecute = true, WindowStyle = LaunchXboxAppHidden ? System.Diagnostics.ProcessWindowStyle.Hidden : System.Diagnostics.ProcessWindowStyle.Normal }); }
        catch (OperationCanceledException) { }
        catch { Notice = "Native settings could not be read. Defaults are in use; save only after reviewing them."; }
        LoadAutoUnlockPreferences();
    }
    public async Task SavePreferencesAsync()
    {
        try { await preferencesStore.SaveAsync(new() { RememberMacSignIn = OperatingSystem.IsMacOS() ? RememberBrowserSession : null, RememberLinuxSignIn = OperatingSystem.IsLinux() ? RememberBrowserSession : null, AutoRefreshEvents = AutoRefreshEvents, RememberWindowSize = RememberWindowSize, AutoMaximize = AutoMaximize, AutomaticallyCheckUpdates = AutomaticallyCheckUpdates, EventsDirectory = EventsDirectory, SessionPath = SessionPath, RegionOverride = RegionOverride, MintAccent = MintAccent, UnlockAllEnabled = UnlockAllEnabled, OAuthProfile = OAuthProfile, FakeSignature = FakeSignature, PrivacyMode = PrivacyMode, AutoSpoof = AutoSpoof, BuildQueueBufferSeconds = BuildQueueBufferSeconds, AutoLaunchXboxApp = AutoLaunchXboxApp, LaunchXboxAppHidden = LaunchXboxAppHidden, WindowsNotificationsEnabled = WindowsNotificationsEnabled, NotifySpooferStops = NotifySpooferStops, StopAutoUnlockOnSpoofFailure = StopAutoUnlockOnSpoofFailure, NotifyAutoUnlockCompletion = NotifyAutoUnlockCompletion, AutoRefreshDlc = AutoRefreshDlc, ShowMythicIcon = ShowMythicIcon, HighlightCompletedDlcs = HighlightCompletedDlcs, MythicColour = ValidColour(MythicColour, "#70C98A"), CompletedDlcColour = ValidColour(CompletedDlcColour, "#183A27") }, lifetime.Token); Notice = "Native settings saved. Connection changes apply on the next connection."; }
        catch (OperationCanceledException) { }
        catch { Notice = "Could not save settings. Check that both paths are absolute and the settings folder is writable."; }
    }
    public async Task InspectCatalogAsync()
    {
        if (busy) return;
        Busy(true); CatalogSummary = "Inspecting catalog and standalone templates…"; CatalogFindings = [];
        try
        {
            var result = await CatalogInspector.InspectAsync(EventsDirectory, lifetime.Token);
            CatalogFindings = result.Findings.ToArray();
            CatalogSummary = $"{result.Titles} title blocks · {result.Supported} supported IDs · {result.Findings.Count} findings · SupportedTitleIDs last: {(result.SupportedLast ? "yes" : "no")}";
        }
        catch (OperationCanceledException) { CatalogSummary = "Inspection cancelled."; }
        catch { CatalogSummary = "Could not inspect the Events catalog. Check the selected folder and JSON syntax."; }
        finally { Busy(false); }
    }
    public async Task TestEventReplacementsAsync()
    {
        if (!CanInteract) return; Busy(true); CatalogSummary = "Testing mapped replacements locally…";
        try
        {
            var result = await Task.Run(async () =>
            {
                var catalog = new EventCatalog(EventsDirectory); var data = await catalog.ReadIndexAsync(lifetime.Token);
                var errors = new List<CatalogFinding>(); int passed = 0;
                foreach (var id in data["SupportedTitleIDs"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    if ((await catalog.ReadTitleAsync(id.ToString(), lifetime.Token))["Achievements"] is not Newtonsoft.Json.Linq.JObject mappings) continue;
                    foreach (var mapping in mappings.Properties())
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        try { await catalog.BuildPayloadsAsync(id.ToString(), mapping.Name, "123456789", DateTime.UtcNow, lifetime.Token); passed++; }
                        catch (OperationCanceledException) { throw; }
                        catch { errors.Add(new(id.ToString(), "Achievement " + mapping.Name, "Payload construction failed; inspect the mapping and standalone template.")); }
                    }
                }
                return (passed, errors);
            }, lifetime.Token);
            CatalogFindings = result.errors.ToArray(); CatalogSummary = $"Replacement validation: {result.passed} passed, {result.errors.Count} failed. No requests sent.";
        }
        catch (OperationCanceledException) { CatalogSummary = "Replacement validation cancelled."; }
        catch { CatalogSummary = "Could not validate the selected catalog."; }
        finally { Busy(false); }
    }
    public async Task DisconnectAsync()
    {
        if (!CanDisconnect) return;
        Busy(true); ++selectionVersion;
        await requests.WaitAsync();
        try { apiServer?.Stop(); ServerAddress = "Stopped"; client?.Dispose(); client = null; ClearPcPresence(); session = null; ++sessionGeneration; AuthorizationStatus = "Xbox disconnected."; connectionMethod = ""; XboxPcAttached = false; ResetWorkflowAccount(); AccountSummary = "Connect an account to view its profile."; ProfileImageUrl = null; Games = []; achievements = []; ResetActions(); SelectedGame = null; Refresh(); ProfileName = "Not connected"; Changed(nameof(ConnectionLabel)); Changed(nameof(HomeXboxSummary)); Navigate("Home"); Notice = "Disconnected. Use Attach Xbox PC app to reconnect."; }
        finally { requests.Release(); Busy(false); }
    }
    public async Task ForgetSavedSessionAsync()
    {
        if (session != null || busy || QueueActive || PresenceRunning || WorkflowBusy) { Notice = "Disconnect before removing the saved native sign-in."; return; }
        var nativePath = NativeSessionStore.DefaultPath;
        try
        {
            if (File.Exists(nativePath)) File.Delete(nativePath);
            if (AchievementLabs.Core.DesktopPlatform.IsUnixDesktop) { RememberBrowserSession = false; activeOAuthSessionPath = null; }
            if (string.Equals(Path.GetFullPath(SessionPath), Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase))
            {
                SessionPath = new DesktopPreferences().SessionPath;
                await SavePreferencesAsync();
            }
            if (AchievementLabs.Core.DesktopPlatform.IsUnixDesktop) await SavePreferencesAsync();
            Notice = "Saved native sign-in removed.";
        }
        catch { Notice = "Could not remove the saved native sign-in."; }
    }
}
