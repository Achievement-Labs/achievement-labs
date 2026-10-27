using Newtonsoft.Json.Linq;
using System.Diagnostics;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private Workflows.StatsEditorViewModel? statsEditor;
    public Workflows.StatsEditorViewModel StatsEditor => statsEditor ??= ObserveWorkflow(new Workflows.StatsEditorViewModel(new Workflows.NativeNotices(message => Notice = message), queueAccount));
    public void OpenStats() { queueAccount.XAUTH = session?.Authorization ?? ""; queueAccount.XUIDOnly = session?.Xuid ?? ""; queueAccount.EventsToken = session?.EventsToken ?? ""; queueAccount.EventsDirectory = EventsDirectory; StatsEditor.OnNavigatedTo(); Navigate("Stats"); }
    public void OpenLibraryStats(Game game)
    {
        if (StatsEditor.IsBusy) { Notice = "Wait for the current stat operation before selecting another title."; return; }
        OpenStats(); StatsTitleId = game.Id; StatsEditor.TitleId = game.Id; StatsEditor.RefreshSupport();
    }
    private string statsTitleId = "", serviceConfigId = "", statName = "", statValue = "0", statsOutput = "", presenceTitleId = "", spoofTitleName = "No title selected", spoofTitleDetails = "Enter a title ID to preview its Xbox metadata.", spoofingStatus = "Spoofing not started";
    private string? spoofTitleImageUrl;
    private Task? autoPresenceTask;
    private CancellationTokenSource? presenceCancellation;
    public bool IsStats => page == "Stats";
    public string StatsTitleId { get => statsTitleId; set { statsTitleId = value ?? ""; if (!StatsEditor.IsBusy) StatsEditor.TitleId = statsTitleId; Changed(); } }
    public string ServiceConfigId { get => serviceConfigId; set { serviceConfigId = value ?? ""; Changed(); } }
    public string StatName { get => statName; set { statName = value ?? ""; Changed(); } }
    public string StatValue { get => statValue; set { statValue = value ?? ""; Changed(); } }
    public string StatsOutput { get => statsOutput; private set { statsOutput = value; Changed(); } }
    public string PresenceTitleId { get => presenceTitleId; set { presenceTitleId = value ?? ""; Changed(); } }
    public bool PresenceRunning => presenceCancellation != null;
    public bool CanLookupSpoofTitle => !busy && session != null;
    public bool CanStartPresence => !busy && session != null && !PresenceRunning && xboxBuildQueue?.IsRunning != true;
    public string SpoofTitleName { get => spoofTitleName; private set { spoofTitleName = value; Changed(); } }
    public string SpoofTitleDetails { get => spoofTitleDetails; private set { spoofTitleDetails = value; Changed(); } }
    public string? SpoofTitleImageUrl { get => spoofTitleImageUrl; private set { spoofTitleImageUrl = value; Changed(); } }
    public string SpoofingStatus { get => spoofingStatus; private set { spoofingStatus = value; Changed(); } }
    private string activeSpoofTitle = "No active spoof", presenceHeartbeat = "No heartbeat sent", presenceElapsed = "Session: 0.00 hours";
    public string ActiveSpoofTitle { get => activeSpoofTitle; private set { activeSpoofTitle = value; Changed(); } }
    public string PresenceHeartbeat { get => presenceHeartbeat; private set { presenceHeartbeat = value; Changed(); } }
    public string PresenceElapsed { get => presenceElapsed; private set { presenceElapsed = value; Changed(); } }
    private string presenceTimer = "Elapsed: 00:00:00";
    public string PresenceTimer { get => presenceTimer; private set { presenceTimer = value; Changed(); } }
    private void UpdatePresenceElapsed(TimeSpan elapsed)
    {
        // Total hours keep sessions longer than a day from wrapping back to zero.
        PresenceTimer = $"Elapsed: {(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        PresenceElapsed = $"Session: {elapsed.TotalHours:F2} hours";
    }
    private void CompletePresence(string? failure, bool cancelled)
    {
        SpoofingStatus = failure != null && !cancelled ? "Spoofing stopped unexpectedly" : "Spoofing stopped";
        if (failure == null || cancelled || lifetime.IsCancellationRequested) return;
        if (StopAutoUnlockOnSpoofFailure && xboxQueue?.IsRunning == true)
            xboxQueue.StopForSpoofFailure(failure);
        if (!NotifySpooferStops) return;
        var message = $"{ActiveSpoofTitle}: {failure}";
        SpooferFailure?.Invoke(message);
        NotifyWindows("Spoofer stopped", message);
    }
    public void OpenSpoofer() => Navigate("Spoofer");
    public async Task LookupSpoofTitleAsync() => await WithAccountAsync(async (api, xuid) =>
    {
        if (!uint.TryParse(PresenceTitleId, out var id) || id == 0) throw new InvalidDataException();
        var response = await api.GetGameTitleAsync(xuid, id.ToString()) ?? throw new InvalidDataException();
        var title = response.Titles.FirstOrDefault() ?? throw new InvalidDataException();
        SpoofTitleName = title.Name;
        SpoofTitleImageUrl = ResolveTitleImage(title.DisplayImage, title.Images);
        await PrepareGameQueueAsync(title.TitleId);
        SpoofTitleDetails = $"Title ID: {title.TitleId}\nPFN: {title.Pfn ?? "Unknown"}\nType: {title.Type ?? "Unknown"}\nDevices: {string.Join(", ", title.Devices)}\nGamerscore: {title.Achievement?.CurrentGamerscore ?? 0}/{title.Achievement?.TotalGamerscore ?? 0}";
    }, allowQueue: true);
    public void UseSelectedTitleForStats() { StatsTitleId = SelectedGame?.Id ?? ""; ServiceConfigId = definitions.Values.FirstOrDefault()?.serviceConfigId ?? ""; OpenStats(); StatsEditor.TitleId = StatsTitleId; }
    public async Task ReadStatsAsync() => await WithAccountAsync(async (api, xuid) =>
    {
        if (!uint.TryParse(StatsTitleId, out _)) throw new InvalidDataException();
        if (string.IsNullOrWhiteSpace(ServiceConfigId)) ServiceConfigId = await api.GetTitleServiceConfigIdAsync(xuid, StatsTitleId) ?? throw new InvalidDataException();
        var response = await api.GetAllGameStatsRawAsync(xuid, ServiceConfigId);
        if (response.StatusCode < 200 || response.StatusCode >= 300) throw new HttpRequestException();
        try { StatsOutput = JToken.Parse(response.Body).ToString(); } catch { StatsOutput = response.Body; }
    });
    public async Task WriteStatAsync() => await WithAccountAsync(async (api, xuid) =>
    {
        if (!Guid.TryParse(ServiceConfigId, out _) || string.IsNullOrWhiteSpace(StatName)) throw new InvalidDataException();
        var value = JToken.Parse(StatValue);
        if (value is not JValue || value.Type == JTokenType.Null) throw new InvalidDataException();
        var response = await api.WriteTitleStatWithDiagnosticsAsync(xuid, ServiceConfigId, StatName, value);
        StatsOutput = $"Stat write HTTP {response.StatusCode}. Read stats again to verify the value.";
    });
    public async Task StartPresenceAsync()
    {
        if (!CanStartPresence || client == null || session == null || !uint.TryParse(PresenceTitleId, out var id) || id == 0) return;
        var xuid = session.Xuid; var titleId = id.ToString();
        runningPresenceTitleId = titleId; presenceHeartbeatAt = null;
        string? lastPresenceAuthorization = null;
        var heartbeatAccepted = false; var heartbeatRefused = false;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ActiveSpoofTitle = $"{Games.FirstOrDefault(g => g.Id == titleId)?.Name ?? (SpoofTitleDetails.StartsWith("Title ID: " + titleId + "\n") ? SpoofTitleName : "Xbox title")} · {titleId}";
        PresenceHeartbeat = "Waiting for first heartbeat";
        UpdatePresenceElapsed(TimeSpan.Zero);
        SpoofingStatus = "Starting spoofing…";
        string? failure = null;
        presenceCancellation = cancellation; Changed(nameof(PresenceRunning)); Changed(nameof(CanStartPresence)); Changed(nameof(CanDisconnect));
        var stopwatch = Stopwatch.StartNew();
        var lastHeartbeat = TimeSpan.Zero;
        try
        {
            while (true)
            {
                // Reacquire for every heartbeat so long sessions do not retain stale XAUTH.
                string? authorization;
                if (OperatingSystem.IsMacOS())
                {
                    var paired = PairedPresenceAuthorization(xuid);
                    if (paired == null) await EnsureAuthorizationFreshAsync(cancellation.Token);
                    if (session?.Xuid != xuid) throw new InvalidOperationException("Account changed; spoofing stopped.");
                    authorization = paired ?? session.Authorization;
                }
                else authorization = await AchievementLabs.Core.XboxPcAppAuthorizationReader.TryReadValidatedAsync(xuid, RegionOverride, cancellation.Token);
                if (string.IsNullOrWhiteSpace(authorization)) throw new InvalidOperationException("Open and sign in to the Xbox PC app.");
                using var api = new XboxApiClient(authorization, RegionOverride);
                var profile = await api.GetBasicProfileAsync();
                if (profile?.ProfileUsers.FirstOrDefault()?.Id != xuid)
                    throw new InvalidOperationException("The presence authorization belongs to another account. Reconnect before spoofing.");
                lastPresenceAuthorization = authorization;
                await requests.WaitAsync(cancellation.Token);
                try
                {
                    var result = await api.SendHeartbeatAsync(xuid, titleId);
                    StatsOutput = $"Presence heartbeat for {titleId}: HTTP {result.StatusCode}";
                    if (result.StatusCode < 200 || result.StatusCode >= 300)
                    {
                        heartbeatRefused = result.StatusCode is 401 or 403;
                        failure = OperatingSystem.IsMacOS() && pairedPcPresence != null && result.StatusCode == 403
                            ? "Xbox refused the PC-paired authorization (HTTP 403). Pair with Windows again from Settings → Sync."
                            : OperatingSystem.IsMacOS() && result.StatusCode == 403
                            ? "Xbox refused heartbeat access for this Mac sign-in (HTTP 403). The profile check passed, but presence needs separate authorization. Xbox PC-app authorization remains the verified route; an event token does not grant heartbeat access."
                            : $"Heartbeat HTTP {result.StatusCode}";
                        PresenceHeartbeat = StatsOutput = failure; break;
                    }
                    heartbeatAccepted = true;
                    PresenceHeartbeat = $"Last heartbeat: {DateTimeOffset.Now:HH:mm:ss} · HTTP {result.StatusCode}" + (pairedPcPresence != null ? " · PC-paired authorization" : "");
                    presenceHeartbeatAt = DateTimeOffset.Now;
                    lastHeartbeat = stopwatch.Elapsed;
                }
                finally { requests.Release(); }
                while (stopwatch.Elapsed - lastHeartbeat < TimeSpan.FromMinutes(5))
                {
                    if (OperatingSystem.IsMacOS() && pairedPcPresence != null) PairedPresenceAuthorization(xuid);
                    SpoofingStatus = "Spoofing active";
                    UpdatePresenceElapsed(stopwatch.Elapsed);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { failure = pairedPcPresence != null ? "PC-paired heartbeat failed or authorization expired. Pair with Windows again from Settings → Sync, or import a fresh PC sync file." : "Presence heartbeat failed or timed out."; PresenceHeartbeat = StatsOutput = failure; }
        finally
        {
            await requests.WaitAsync();
            try
            {
                if (!lifetime.IsCancellationRequested && lastPresenceAuthorization != null && heartbeatAccepted && !heartbeatRefused)
                {
                    using var stopApi = new XboxApiClient(lastPresenceAuthorization, RegionOverride);
                    var result = xboxQueue?.IsRunning == true ? await stopApi.SendHeartbeatAsync(xuid, xboxQueue.ActiveTitleId) : await stopApi.StopHeartbeatAsync(xuid);
                    if (result.StatusCode < 200 || result.StatusCode >= 300) StatsOutput = $"Presence stop rejected: HTTP {result.StatusCode}.";
                }
            }
            catch { StatsOutput = "Presence stop request failed; the service may retain presence until it expires."; }
            finally { var cancelled = cancellation.IsCancellationRequested; presenceCancellation = null; runningPresenceTitleId = ""; presenceHeartbeatAt = null; requests.Release(); cancellation.Dispose(); UpdatePresenceElapsed(stopwatch.Elapsed); CompletePresence(failure, cancelled); if (heartbeatRefused && OperatingSystem.IsMacOS()) ClearPcPresence(); Changed(nameof(PresenceRunning)); Changed(nameof(CanStartPresence)); Changed(nameof(CanDisconnect)); }
        }
    }
    public void StopPresence() => presenceCancellation?.Cancel();
    private async Task WithAccountAsync(Func<XboxApiClient, string, Task> operation, bool allowQueue = false)
    {
        if (!(allowQueue ? CanLookupSpoofTitle : CanQuery) || client == null || session == null) return;
        Busy(true);
        try
        {
            await EnsureAuthorizationFreshAsync(lifetime.Token);
            await requests.WaitAsync(lifetime.Token);
            try { if (client != null && session != null) await operation(client, session.Xuid); }
            finally { requests.Release(); }
        }
        catch (OperationCanceledException) { }
        catch { Notice = StatsOutput = "Request failed. Check the account connection and request inputs."; }
        finally { Busy(false); }
    }
}
