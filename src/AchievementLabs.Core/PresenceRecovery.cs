namespace AchievementLabs.Core;

/// <summary>Bounded recovery for presence only. Never replays achievement actions.</summary>
public static class PresenceRecovery
{
    public static bool RetryableStatus(int status) => status is 408 or 429 || status >= 500 && status <= 599;
    public static bool Retryable(Exception error) => error is HttpRequestException http
        ? http.StatusCode is null || RetryableStatus((int)http.StatusCode)
        : error is IOException or TimeoutException or TaskCanceledException;
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken,
        Action<int, TimeSpan>? retrying = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        int[] seconds = [5, 15, 30];
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await send(cancellationToken); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && attempt < seconds.Length && Retryable(error))
            {
                var wait = TimeSpan.FromSeconds(seconds[attempt]);
                retrying?.Invoke(attempt + 1, wait);
                await delay(wait, cancellationToken);
            }
        }
    }
}
