using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private readonly DesktopPreferencesStore preferencesStore = new(AchievementLabsPaths.LocalFile("native-settings.json"));
    private string eventsDirectory = new DesktopPreferences().EventsDirectory;
    private string sessionPath = new DesktopPreferences().SessionPath;
    private bool regionOverride, mintAccent, fakeSignature, privacyMode, autoSpoof, autoLaunch, launchHidden;
    public bool FakeSignature { get => fakeSignature; set { fakeSignature = value; queueAccount.Settings.FakeSignatureEnabled = value; Changed(); } }
    public bool PrivacyMode { get => privacyMode; set { privacyMode = value; Changed(); Changed(nameof(ProfileName)); Changed(nameof(ProfileImageUrl)); Changed(nameof(AccountSummary)); } }
    public bool AutoSpoof { get => autoSpoof; set { autoSpoof = value; Changed(); } }
    public bool AutoLaunchXboxApp { get => autoLaunch; set { autoLaunch = value; Changed(); } }
    public bool LaunchXboxAppHidden { get => launchHidden; set { launchHidden = value; Changed(); } }
    private CatalogFinding[] findings = [];
    private string catalogSummary = "Choose an Events folder in Settings, then inspect its catalog.";
    public bool IsSettings => page == "Settings";
    public bool IsDiagnostics => page == "Diagnostics";
    public bool CanDisconnect => !PresenceRunning && !QueueActive && !busy && !WorkflowBusy && session != null;
    public string EventsDirectory { get => eventsDirectory; set { eventsDirectory = value; Changed(); } }
    public string SessionPath { get => sessionPath; set { sessionPath = value; Changed(); } }
    public bool RegionOverride { get => regionOverride; set { regionOverride = value; Changed(); } }
    public bool MintAccent { get => mintAccent; set { mintAccent = value; Changed(); } }
    public CatalogFinding[] CatalogFindings { get => findings; private set { findings = value; Changed(); } }
    public string CatalogSummary { get => catalogSummary; private set { catalogSummary = value; Changed(); } }
    public async Task LoadPreferencesAsync()
    {
        try { var value = await preferencesStore.LoadAsync(lifetime.Token); AutomaticallyCheckUpdates = value.AutomaticallyCheckUpdates; EventsDirectory = value.EventsDirectory; SessionPath = value.SessionPath; RegionOverride = value.RegionOverride; MintAccent = value.MintAccent; UnlockAllEnabled = value.UnlockAllEnabled; OAuthProfile = value.OAuthProfile; FakeSignature = value.FakeSignature; PrivacyMode = value.PrivacyMode; AutoSpoof = value.AutoSpoof; AutoLaunchXboxApp = value.AutoLaunchXboxApp; LaunchXboxAppHidden = value.LaunchXboxAppHidden; await LoadSavedEventTokenAsync();
            if (AutoLaunchXboxApp) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"shell:appsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App") { UseShellExecute = true, WindowStyle = LaunchXboxAppHidden ? System.Diagnostics.ProcessWindowStyle.Hidden : System.Diagnostics.ProcessWindowStyle.Normal }); }
        catch (OperationCanceledException) { }
        catch { Notice = "Native settings could not be read. Defaults are in use; save only after reviewing them."; }
    }
    public async Task SavePreferencesAsync()
    {
        try { await preferencesStore.SaveAsync(new() { AutomaticallyCheckUpdates = AutomaticallyCheckUpdates, EventsDirectory = EventsDirectory, SessionPath = SessionPath, RegionOverride = RegionOverride, MintAccent = MintAccent, UnlockAllEnabled = UnlockAllEnabled, OAuthProfile = OAuthProfile, FakeSignature = FakeSignature, PrivacyMode = PrivacyMode, AutoSpoof = AutoSpoof, AutoLaunchXboxApp = AutoLaunchXboxApp, LaunchXboxAppHidden = LaunchXboxAppHidden }, lifetime.Token); Notice = "Native settings saved. Connection changes apply on the next connection."; }
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
        catch { CatalogSummary = "Could not inspect Events/Data.json. Check the selected folder and JSON syntax."; }
        finally { Busy(false); }
    }
    public async Task TestEventReplacementsAsync()
    {
        if (!CanInteract) return; Busy(true); CatalogSummary = "Testing mapped replacements locally…";
        try
        {
            var result = await Task.Run(async () =>
            {
                var catalog = new EventCatalog(EventsDirectory); var data = await catalog.ReadAsync(lifetime.Token);
                var errors = new List<CatalogFinding>(); int passed = 0;
                foreach (var id in data["SupportedTitleIDs"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    if (data[id.ToString()]?["Achievements"] is not Newtonsoft.Json.Linq.JObject mappings) continue;
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
        try { apiServer?.Stop(); ServerAddress = "Stopped"; client?.Dispose(); client = null; session = null; connectionMethod = ""; XboxPcAttached = false; ResetWorkflowAccount(); AccountSummary = "Connect an account to view its profile."; ProfileImageUrl = null; Games = []; achievements = []; ResetActions(); SelectedGame = null; Refresh(); ProfileName = "Not connected"; Changed(nameof(ConnectionLabel)); Changed(nameof(HomeXboxSummary)); Navigate("Home"); Notice = "Disconnected. Use Attach Xbox PC app to reconnect."; }
        finally { requests.Release(); Busy(false); }
    }
    public async Task ForgetSavedSessionAsync()
    {
        if (session != null || busy || QueueActive || PresenceRunning || WorkflowBusy) { Notice = "Disconnect before removing the saved native sign-in."; return; }
        var nativePath = NativeSessionStore.DefaultPath;
        try
        {
            if (File.Exists(nativePath)) File.Delete(nativePath);
            if (string.Equals(Path.GetFullPath(SessionPath), Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase))
            {
                SessionPath = new DesktopPreferences().SessionPath;
                await SavePreferencesAsync();
            }
            Notice = "Saved native sign-in removed. Existing WPF auth.json was not changed.";
        }
        catch { Notice = "Could not remove the saved native sign-in."; }
    }
}
