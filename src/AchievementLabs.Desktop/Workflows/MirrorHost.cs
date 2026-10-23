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

    Task<(string? TitleId, string Name, string Message)> LookupStoreTitleAsync(string storeLinkOrProductId, CancellationToken cancellationToken);
    void Alert(string title, string message);
}
