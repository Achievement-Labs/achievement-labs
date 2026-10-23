using AchievementLabs.Core;
using AchievementLabs.Desktop.Workflows;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    public Action<string>? AutoUnlockFailure { get; set; }
    private readonly NativeAccountContext queueAccount = new();
    private AutoUnlockerViewModel? xboxQueue;
    private SteamAutoUnlockerViewModel? steamQueue;
    private BuildQueueViewModel? xboxBuildQueue;
    public BuildQueueViewModel XboxBuildQueue => xboxBuildQueue ??= ObserveBuildQueuePreferences(ObserveWorkflow(new BuildQueueViewModel(queueAccount, new NativeNotices(message => Notice = message), eventCatalog) { ConfigureRunner = runner => {
        runner.ReadQueueAchievementsAsync = ReadQueueAchievementsAsync;
        runner.PresenceGate = requests;
        runner.FailureNotification = message => AutoUnlockFailure?.Invoke(message);
        runner.CompletionNotification = message => { if (NotifyAutoUnlockCompletion) { AutoUnlockCompleted?.Invoke(message); NotifyWindows("Auto unlock complete", message); } };
    } }));
    public AutoUnlockerViewModel XboxQueue => xboxQueue ??= ObserveAutoUnlockPreferences(new AutoUnlockerViewModel(new NativeNotices(message => Notice = message), queueAccount) { EventCatalog = eventCatalog, CanDeleteSavedQueue = () => !QueueActive && xboxBuildQueue?.IsAdding != true, AppendSavedGameAsync = state => XboxBuildQueue.AddSavedGameAsync(state), ReadQueueAchievementsAsync = ReadQueueAchievementsAsync, ExternalPresenceActive = () => PresenceRunning, PresenceGate = requests, FailureNotification = message => AutoUnlockFailure?.Invoke(message), CompletionNotification = message => { if (NotifyAutoUnlockCompletion) { AutoUnlockCompleted?.Invoke(message); NotifyWindows("Auto unlock complete", message); } } });
    public SteamAutoUnlockerViewModel SteamQueue => steamQueue ??= ObserveWorkflow(new SteamAutoUnlockerViewModel(steam, new NativeNotices(message => Notice = message)));
    public string QueueOwnerXuid => queueAccount.XUIDOnly;
    public bool IsQueues => page == "Queues";
    public bool IsSteamQueue => page == "SteamQueue";
    private bool queueActive;
    private double buildQueueBufferSeconds = 60;
    public double BuildQueueBufferSeconds
    {
        get => buildQueueBufferSeconds;
        set { if (!double.IsFinite(value) || value < 0 || value > 3600) return; buildQueueBufferSeconds = value; Changed(); SaveAutoUnlockPreferences(); }
    }
    public bool QueueActive { get => queueActive; private set { queueActive = value; Changed(); Busy(busy); } }
    public void OpenQueues()
    {
        SyncWorkflowAccount();
        queueAccount.LastOAuthResponse = session?.OAuthResponse; queueAccount.XAUTH = session?.Authorization ?? ""; queueAccount.XUIDOnly = session?.Xuid ?? ""; queueAccount.EventsToken = session?.EventsToken ?? ""; queueAccount.EventsDirectory = EventsDirectory;
        XboxQueue.OnNavigatedTo(); XboxBuildQueue.Refresh(); Navigate("Queues");
    }
    private async Task<AchievementsResponse?> ReadQueueAchievementsAsync(string titleId, CancellationToken ct)
    {
        var active = session ?? throw new InvalidOperationException("Connect an Xbox account first.");
        var api = client ?? throw new InvalidOperationException("Xbox client is unavailable.");
        ct.ThrowIfCancellationRequested();
        var response = await api.GetAchievementsForTitleAsync(active.Xuid, titleId);
        ct.ThrowIfCancellationRequested();
        if (session?.Xuid != active.Xuid) throw new InvalidOperationException("The connected Xbox account changed.");
        return response;
    }
    private async Task<bool> SendQueueEventAchievementAsync(string titleId, string achievementId, CancellationToken ct)
    {
        // Read the live session on every attempt, including after manual save or automatic refresh.
        var active = session ?? throw new InvalidOperationException("Connect an Xbox account first.");
        var api = client ?? throw new InvalidOperationException("Xbox client is unavailable.");
        if (!EventTokenValidator.TryNormalize(active.EventsToken, out var token) ||
            !EventTokenValidator.TryValidate(token, out _, out _))
            throw new AchievementLabs.Core.MissingEventTokenException();
        var payloads = await eventCatalog.GetPayloadsAsync(titleId, achievementId, active.Xuid, ct);
        if (payloads.Payloads.Length == 0) throw new InvalidOperationException("No mapped event data is available.");
        await requests.WaitAsync(ct);
        try
        {
            foreach (var payload in payloads.Payloads)
            {
                ct.ThrowIfCancellationRequested();
                if (session?.Xuid != active.Xuid) throw new InvalidOperationException("The connected Xbox account changed.");
                var result = await api.UnlockEventBasedAchievementWithDiagnostics(token, payload);
                if (result.StatusCode < 200 || result.StatusCode >= 300)
                    throw new HttpRequestException($"Event request failed: HTTP {result.StatusCode} {result.ReasonPhrase}.", null,
                        (System.Net.HttpStatusCode)result.StatusCode);
            }
        }
        finally { requests.Release(); }
        return true;
    }
    public async Task RunXboxQueueAsync()
    {
        if (QueueActive && !XboxQueue.IsRunning) return;
        if (session == null && !XboxQueue.IsRunning) return;
        if (XboxQueue.IsRunning) { await XboxQueue.StartStopAutoUnlock(); return; }
        if (busy) { Notice = "Wait for the current operation before starting a queue."; return; }
        QueueActive = true;
        try { await XboxQueue.StartStopAutoUnlock(); } finally { QueueActive = false; }
    }
    public async Task RunSteamQueueAsync()
    {
        if (QueueActive || busy) return;
        QueueActive = true;
        try { await SteamQueue.StartCommand.ExecuteAsync(null); } finally { QueueActive = false; }
    }
    public async Task RunXboxBuildQueueAsync()
    {
        if (XboxBuildQueue.IsRunning) { XboxBuildQueue.Stop(); return; }
        if (session == null || QueueActive || busy || PresenceRunning || XboxBuildQueue.IsAdding || xboxQueue?.IsRunning == true) return;
        SyncWorkflowAccount();
        QueueActive = true;
        try { await XboxBuildQueue.RunAsync(lifetime.Token, TimeSpan.FromSeconds(BuildQueueBufferSeconds)); }
        finally { QueueActive = false; }
    }
    private void StopQueues()
    {
        xboxBuildQueue?.Stop();
        steamQueue?.StopCommand.Execute(null);
        if (xboxQueue?.IsRunning == true) _ = xboxQueue.StartStopAutoUnlock();
    }
}
