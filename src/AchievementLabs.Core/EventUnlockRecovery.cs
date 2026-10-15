using System.Net;

namespace AchievementLabs.Core;

public sealed class MissingEventTokenException() : InvalidOperationException("No events token is configured.");

public static class EventUnlockRecovery
{
    public static bool IsTokenFailure(Exception error) => error is MissingEventTokenException ||
        error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    public static async Task<bool> RunAsync(bool enabled, Func<Task<bool>> send,
        Func<CancellationToken, Task<bool>> refresh, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await send(); }
        catch (Exception error) when (enabled && IsTokenFailure(error))
        {
            if (!await refresh(ct)) throw new InvalidOperationException("Event token refresh was unavailable. The achievement was not retried.", error);
            ct.ThrowIfCancellationRequested();
            return await send(); // Exactly one retry; further failures go to the normal failure controls.
        }
    }
}
