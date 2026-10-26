using AchievementLabs.Mirror;

namespace AchievementLabs.Desktop.Workflows;

/// <summary>Everything Achievement Mirror needs from the rest of the app. Implemented by <see cref="DesktopModel"/>.</summary>
public interface IMirrorHost
{
    /// <summary>The connected Xbox account, or empty when none is attached.</summary>
    string XboxXuid { get; }
    string SteamInstallPath { get; }
    string LocalSteamId { get; }
    string? SteamAppName(long appId);

    /// <summary>Reads the connected account's achievements for a title. Challenges are excluded.</summary>
    Task<(IReadOnlyList<MirrorAchievement> Achievements, string TitleName)> ReadXboxAsync(string titleId, CancellationToken cancellationToken);
    Task UnlockXboxAsync(string titleId, string achievementId, CancellationToken cancellationToken);
    Task<bool> IsXboxPresenceInTitleAsync(string titleId, CancellationToken cancellationToken);

    /// <summary>Reads Steam achievements through the local Steam client (used to verify Steam unlocks).</summary>
    Task<IReadOnlyList<MirrorAchievement>> ReadSteamLocalAsync(long appId, CancellationToken cancellationToken);
    Task UnlockSteamAsync(long appId, string apiName, CancellationToken cancellationToken);
    Task StartSteamIdleAsync(long appId, CancellationToken cancellationToken);
    void StopSteamIdle();
    /// <summary>App ID the Steam idle helper is currently running for, or 0.</summary>
    long SteamIdleAppId { get; }

    /// <summary>App ID Steam on this PC says is running (Steam's own RunningAppID), or 0.</summary>
    long SteamRunningAppId { get; }
    /// <summary>
    /// Describes Xbox presence another Achievement Labs feature is sending for a different title (title spoofer,
    /// Auto Unlock), or null when presence is free, already shows <paramref name="titleId"/>, or belongs to the Mirror.
    /// </summary>
    string? XboxPresenceConflict(string titleId);
    /// <summary>True when any Achievement Labs presence (the Mirror's or another feature's) currently shows this title.</summary>
    bool XboxPresenceShows(string titleId);
    /// <summary>Title the Mirror is currently showing on Xbox through the title spoofer, or null.</summary>
    string? MirrorPresenceTitle { get; }
    /// <summary>Starts title-spoofer presence for the Mirror and waits for the first heartbeat. False if it couldn't start.</summary>
    Task<bool> StartMirrorPresenceAsync(string titleId, CancellationToken cancellationToken);
    /// <summary>Stops presence only if the Mirror started it.</summary>
    void StopMirrorPresence();

    Task<(string? TitleId, string Name, string Message)> LookupStoreTitleAsync(string storeLinkOrProductId, CancellationToken cancellationToken);
    void Alert(string title, string message);
}
