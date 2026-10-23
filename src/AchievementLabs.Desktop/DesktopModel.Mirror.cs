using System.Globalization;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.Mirror;
using AchievementLabs.Models;

namespace AchievementLabs.Desktop;

/// <summary>Achievement Mirror wiring: the page's view model and the Xbox/Steam operations it needs.</summary>
public sealed partial class DesktopModel : IMirrorHost
{
    private AchievementMirrorViewModel? mirror;
    public AchievementMirrorViewModel Mirror => mirror ??= ObserveMirror(new AchievementMirrorViewModel(this));
    public bool IsMirror => page == "Mirror";
    public Action<string, string>? MirrorAlert { get; set; }
    /// <summary>Shows the Achievement Mirror pre-check results and returns true if the user chose to start anyway.</summary>
    public Func<IReadOnlyList<MirrorPreflightIssue>, Task<bool>>? MirrorPreflightReview { get; set; }

    public void OpenMirror() { _ = Mirror; Navigate("Mirror"); Changed(nameof(IsMirror)); }

    private AchievementMirrorViewModel ObserveMirror(AchievementMirrorViewModel model)
    {
        model.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AchievementMirrorViewModel.GlobalSummary)) Changed(nameof(Mirror)); };
        return model;
    }

    /// <summary>
    /// Runs the Achievement Mirror pre-check for Auto Unlock queues. Returns false if the queue must not start.
    /// </summary>
    private async Task<bool> MirrorPreflightAsync(IEnumerable<(string TitleId, string TitleName, IReadOnlyList<string> AchievementIds)> queues)
    {
        if (mirror == null) return true;
        var issues = mirror.CheckAutoUnlockQueues(queues);
        if (issues.Count == 0) return true;
        if (MirrorPreflightReview == null) { Notice = issues[0].TitleName + ": " + issues[0].Message; return false; }
        return await MirrorPreflightReview(issues) && !issues.Any(i => i.Blocking);
    }

    private Task<bool> MirrorXboxAppUnlockAsync(string titleId, string achievementId, string name, CancellationToken cancellationToken) =>
        mirror == null ? Task.FromResult(true) : mirror.ReportXboxAppUnlockAsync(titleId, achievementId, name, cancellationToken);

    private Task<bool> MirrorSteamAppUnlockAsync(long appId, string apiName, string name, CancellationToken cancellationToken) =>
        mirror == null ? Task.FromResult(true) : mirror.ReportSteamAppUnlockAsync(appId, apiName, name, cancellationToken);

    // ---- IMirrorHost ----

    string IMirrorHost.XboxXuid => session?.Xuid ?? "";
    string IMirrorHost.SteamInstallPath => steam.GetSteamInstallPath();
    string IMirrorHost.LocalSteamId => steam.GetLocalSteamId();
    string? IMirrorHost.SteamAppName(long appId) => steamGames.FirstOrDefault(g => g.AppId == appId)?.Name is { Length: > 0 } name ? name : null;
    long IMirrorHost.SteamIdleAppId { get { var info = steam.GetSpoofSessionInfo(); return info.IsActive ? info.AppId : 0; } }

    async Task<(IReadOnlyList<MirrorAchievement> Achievements, string TitleName)> IMirrorHost.ReadXboxAsync(string titleId, CancellationToken cancellationToken)
    {
        var response = await ReadMirrorXboxResponseAsync(titleId, cancellationToken);
        var list = response.achievements
            .Where(a => !string.Equals(a.achievementType, "Challenge", StringComparison.OrdinalIgnoreCase))
            .Select(a => new MirrorAchievement(a.id, a.name, a.description ?? a.lockedDescription ?? "", a.progressState == "Achieved", ParseXboxUnlockTime(a.progression?.timeUnlocked)))
            .ToArray();
        var name = response.achievements.SelectMany(a => a.titleAssociations).FirstOrDefault(t => t.id == titleId)?.name
            ?? response.achievements.SelectMany(a => a.titleAssociations).FirstOrDefault()?.name ?? "";
        return (list, name);
    }

    async Task IMirrorHost.UnlockXboxAsync(string titleId, string achievementId, CancellationToken cancellationToken)
    {
        var response = await ReadMirrorXboxResponseAsync(titleId, cancellationToken);
        var definition = response.achievements.FirstOrDefault(a => a.id == achievementId)
            ?? throw new InvalidOperationException("Xbox no longer lists this achievement.");
        if (Workflows.AutoUnlockerViewModel.UsesEvents(response.achievements))
        {
            await SendQueueEventAchievementAsync(titleId, achievementId, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(definition.serviceConfigId) || definition.serviceConfigId == Guid.Empty.ToString())
            throw new InvalidDataException("Xbox didn't return this title's service configuration.");
        var active = session ?? throw new InvalidOperationException("Attach the Xbox PC app first.");
        using var api = new XboxApiClient(active.Authorization);
        await api.UnlockTitleBasedAchievementAsync(definition.serviceConfigId, titleId, active.Xuid, achievementId, FakeSignature);
    }

    async Task<bool> IMirrorHost.IsXboxPresenceInTitleAsync(string titleId, CancellationToken cancellationToken)
    {
        await EnsureAuthorizationFreshAsync(cancellationToken);
        var active = session ?? throw new InvalidOperationException("Attach the Xbox PC app first.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, XboxPresence.Url(active.Xuid));
        request.Headers.TryAddWithoutValidation("Authorization", active.Authorization);
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "3");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return XboxPresence.IsInTitle(await response.Content.ReadAsStringAsync(cancellationToken), titleId);
    }

    async Task<IReadOnlyList<MirrorAchievement>> IMirrorHost.ReadSteamLocalAsync(long appId, CancellationToken cancellationToken)
    {
        // Steam's own stats file on this PC updates the moment a save lands; the profile page used by the
        // Steam tab can lag, which would make a successful unlock look unverified.
        var steamId = steam.GetLocalSteamId();
        if (SteamWebApi.IsSteamId64(steamId) && steam.GetSteamInstallPath() is { Length: > 0 } steamPath
            && await Task.Run(() => SteamLocalStats.TryRead(steamPath, SteamWebApi.AccountId(steamId), appId), cancellationToken) is { } local)
            return local;
        var items = await steam.LoadAchievementsAsync(appId, cancellationToken);
        return items.Select(a => new MirrorAchievement(a.Id, a.Name, a.Description, a.IsUnlocked,
            a.UnlockTime is { } time ? new DateTimeOffset(time) : null)).ToArray();
    }

    async Task IMirrorHost.UnlockSteamAsync(long appId, string apiName, CancellationToken cancellationToken)
    {
        var item = new SteamAchievementItem { Id = apiName, IsUnlocked = false, DesiredUnlocked = true };
        await steam.StoreAchievementsAsync(appId, [item], cancellationToken);
    }

    async Task IMirrorHost.StartSteamIdleAsync(long appId, CancellationToken cancellationToken) => await steam.StartSpoofSessionAsync(appId, cancellationToken);
    void IMirrorHost.StopSteamIdle() => steam.StopSpoofSession();

    private string runningPresenceTitleId = "";
    private DateTimeOffset? presenceHeartbeatAt;
    private string? mirrorPresenceTitle;

    long IMirrorHost.SteamRunningAppId
    {
        get
        {
            try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0) is int id ? id : 0; }
            catch { return 0; }
        }
    }

    string? IMirrorHost.MirrorPresenceTitle => mirrorPresenceTitle != null && PresenceRunning && runningPresenceTitleId == mirrorPresenceTitle ? mirrorPresenceTitle : null;

    string? IMirrorHost.XboxPresenceConflict(string titleId)
    {
        if (PresenceRunning && runningPresenceTitleId.Length > 0 && runningPresenceTitleId != titleId && runningPresenceTitleId != mirrorPresenceTitle)
            return $"The Title spoofer is showing {ActiveSpoofTitle} on Xbox.";
        if (queueAccount.SpoofingStatus == 1 && queueAccount.SpoofedTitleID is { Length: > 0 } queued && queued != "0" && queued != titleId)
            return $"Auto Unlock is showing title ID {queued} on Xbox.";
        return null;
    }

    bool IMirrorHost.XboxPresenceShows(string titleId) =>
        PresenceRunning && runningPresenceTitleId == titleId || queueAccount.SpoofingStatus == 1 && queueAccount.SpoofedTitleID == titleId;

    async Task<bool> IMirrorHost.StartMirrorPresenceAsync(string titleId, CancellationToken cancellationToken)
    {
        var started = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (PresenceRunning || !CanStartPresence) return false;
            mirrorPresenceTitle = titleId; PresenceTitleId = titleId;
            _ = StartPresenceAsync();
            return PresenceRunning;
        });
        if (!started) { mirrorPresenceTitle = null; return false; }
        // Wait for the first heartbeat so an unlock sent right after lands while Xbox shows the game.
        for (var i = 0; i < 40 && presenceHeartbeatAt == null && PresenceRunning; i++) await Task.Delay(250, cancellationToken);
        return presenceHeartbeatAt != null;
    }

    void IMirrorHost.StopMirrorPresence()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (mirrorPresenceTitle != null && PresenceRunning && runningPresenceTitleId == mirrorPresenceTitle) StopPresence();
            mirrorPresenceTitle = null;
        });
    }

    void IMirrorHost.Alert(string title, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            MirrorAlert?.Invoke(title, message);
            NotifyWindows(title, message);
        });
    }

    /// <summary>Reads achievements with a short-lived client so background reads never share request headers with the UI.</summary>
    private async Task<AchievementsResponse> ReadMirrorXboxResponseAsync(string titleId, CancellationToken cancellationToken)
    {
        await EnsureAuthorizationFreshAsync(cancellationToken);
        var active = session ?? throw new InvalidOperationException("Attach the Xbox PC app first.");
        using var api = new XboxApiClient(active.Authorization);
        var response = await api.GetAchievementsForTitleAsync(active.Xuid, titleId, writeDebugLog: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (session?.Xuid != active.Xuid) throw new InvalidOperationException("The connected Xbox account changed.");
        return response ?? throw new InvalidDataException("Xbox didn't return achievements for this title.");
    }

    private static DateTimeOffset? ParseXboxUnlockTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) && time.Year > 2000 ? time : null;
}
