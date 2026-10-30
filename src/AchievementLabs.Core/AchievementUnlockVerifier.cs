namespace AchievementLabs.Core;

public sealed record AchievementProgressSnapshot(string State, string Progress);
public sealed record AchievementVerificationResult(bool Confirmed, string Progress, int Reads);

/// <summary>Read-only verification. Collector acceptance and numeric targets alone never confirm an unlock.</summary>
public static class AchievementUnlockVerifier
{
    public static async Task<AchievementVerificationResult> WatchAsync(
        Func<CancellationToken, Task<AchievementProgressSnapshot?>> read,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<AchievementVerificationResult>? progress = null)
    {
        delay ??= (duration, token) => Task.Delay(duration, token);
        while (true)
        {
            var result = await VerifyAsync(read, ct, delay);
            if (result.Confirmed) return result;
            progress?.Invoke(result);
            ct.ThrowIfCancellationRequested();
            await delay(TimeSpan.FromSeconds(30), ct);
        }
    }
    public static async Task<AchievementVerificationResult> VerifyAsync(
        Func<CancellationToken, Task<AchievementProgressSnapshot?>> read,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= (duration, token) => Task.Delay(duration, token);
        var pauses = new[] { 0, 2, 4, 8, 12 };
        var progress = "Xbox progress unavailable";
        var reads = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            foreach (var seconds in pauses)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (seconds > 0) await delay(TimeSpan.FromSeconds(seconds), deadline.Token);
                var snapshot = await read(deadline.Token).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
                reads++;
                if (snapshot != null)
                {
                    progress = snapshot.Progress;
                    if (string.Equals(snapshot.State, "Achieved", StringComparison.OrdinalIgnoreCase))
                        return new(true, progress, reads);
                }
            }
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        return new(false, progress, reads);
    }
}
