namespace AchievementLabs.Core;

public enum BuildQueueItemStatus
{
    Pending, Running, WaitingForToken, Paused, Completed, Failed, Cancelled
}

// A title-level job wraps the existing achievement queue. Reconcile restored
// status and verify the account before resuming any submission.
public sealed record BuildQueueItem(
    Guid Id, string Xuid, string TitleId, string TitleName, string ReferenceXuid,
    double SpeedMultiplier, DateTimeOffset AddedAt,
    BuildQueueItemStatus Status = BuildQueueItemStatus.Pending);

public static class XboxTokenMaintenancePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AuthorizationRefreshInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(5);

    public static bool ShouldRefresh(DateTimeOffset now, DateTimeOffset lastRefresh, DateTimeOffset? expiresAt)
        => now - lastRefresh >= AuthorizationRefreshInterval
            || expiresAt.HasValue && expiresAt.Value - now <= RefreshBeforeExpiry;
}
