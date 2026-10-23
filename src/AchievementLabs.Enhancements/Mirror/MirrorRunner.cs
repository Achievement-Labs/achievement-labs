namespace AchievementLabs.Mirror;

/// <summary>The platform that receives mirrored unlocks.</summary>
public interface IMirrorTarget
{
    /// <summary>Current achievements on the target platform, used before and after each unlock.</summary>
    Task<IReadOnlyList<MirrorAchievement>> ReadAsync(CancellationToken cancellationToken);
    Task UnlockAsync(string achievementId, CancellationToken cancellationToken);
}

/// <summary>
/// Runtime for one monitored pair. It never backfills: everything already unlocked on the source
/// when monitoring starts is ignored. Each new source unlock is sent to the target and verified by
/// reading the target back. Any failure stops the pair; unlocks detected while stopped are kept as
/// pending and only sent if the user says so.
/// </summary>
public sealed class MirrorRunner
{
    public const int MaxLogEntries = 300;
    public static readonly TimeSpan[] DefaultAttemptDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];

    private readonly IMirrorTarget target;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly object gate = new();
    private readonly HashSet<string> handled = new(StringComparer.Ordinal);
    private readonly List<MirrorPendingUnlock> pending = [];
    private readonly List<MirrorLogEntry> log = [];
    private CancellationTokenSource session = new();

    public MirrorRunner(MirrorPair pair, IMirrorTarget target, TimeSpan[]? attemptDelays = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
    {
        Pair = pair; this.target = target;
        AttemptDelays = attemptDelays is { Length: > 0 } ? attemptDelays : DefaultAttemptDelays;
        this.delay = delay ?? Task.Delay;
        this.now = now ?? (() => DateTimeOffset.Now);
    }

    public MirrorPair Pair { get; }
    public TimeSpan[] AttemptDelays { get; }
    public MirrorStatus Status { get; private set; } = MirrorStatus.Idle;
    public string StopReason { get; private set; } = "";
    public DateTimeOffset? StartedAt { get; private set; }
    public int MirroredCount { get; private set; }

    public IReadOnlyList<MirrorPendingUnlock> Pending { get { lock (gate) return pending.ToArray(); } }
    public IReadOnlyList<MirrorLogEntry> Log { get { lock (gate) return log.ToArray(); } }

    /// <summary>Raised after any state, log or pending change. May fire on a background thread.</summary>
    public event Action? Changed;
    /// <summary>Raised once when the pair stops because of a failure (title, message).</summary>
    public event Action<string, string>? Stopped;
    public event Action<MirrorLogEntry>? Logged;
    /// <summary>Raised when a source achievement is confirmed unlocked on the target (source ID, time).</summary>
    public event Action<string, DateTimeOffset>? Mirrored;

    /// <summary>Starts monitoring. <paramref name="sourceNow"/> is the source's current list, which becomes the no-backfill baseline.</summary>
    public void Start(IReadOnlyList<MirrorAchievement> sourceNow)
    {
        if (!Pair.ReadyToMonitor) throw new InvalidOperationException("Review every achievement in the match list before mirroring.");
        int already;
        lock (gate)
        {
            session.Cancel(); session = new();
            handled.Clear(); pending.Clear();
            foreach (var achievement in sourceNow.Where(a => a.Unlocked)) handled.Add(achievement.Id);
            already = handled.Count;
            Status = MirrorStatus.Monitoring; StopReason = ""; StartedAt = now(); MirroredCount = 0;
        }
        Write($"Mirroring started ({Describe()}). {already} achievement(s) already unlocked on {Pair.Source} won't be copied.");
    }

    /// <summary>Stops monitoring by user request. Nothing is sent after this returns.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (Status == MirrorStatus.Idle) return;
            session.Cancel();
            Status = MirrorStatus.Idle; StopReason = "";
            pending.Clear();
        }
        Write("Mirroring stopped.");
    }

    /// <summary>
    /// Feeds a fresh source list from any detector. New unlocks are mirrored one at a time, oldest first.
    /// Returns false if a mirror failed (the pair is then stopped).
    /// </summary>
    public async Task<bool> ObserveSourceAsync(IReadOnlyList<MirrorAchievement> snapshot, string detectedBy, CancellationToken cancellationToken = default)
    {
        MirrorAchievement[] fresh;
        lock (gate)
        {
            if (Status == MirrorStatus.Idle) return true;
            fresh = snapshot.Where(a => a.Unlocked && !handled.Contains(a.Id))
                .OrderBy(a => a.UnlockedAt ?? DateTimeOffset.MaxValue).ToArray();
            foreach (var achievement in fresh) handled.Add(achievement.Id);
        }
        var ok = true;
        foreach (var achievement in fresh)
        {
            Write($"{Pair.Source} unlock detected: {achievement.Name} ({detectedBy}).");
            ok &= await ProcessAsync(achievement.Id, achievement.Name, cancellationToken);
        }
        return ok;
    }

    /// <summary>
    /// Called when Achievement Labs itself unlocks <paramref name="sourceId"/> on the source platform.
    /// With the toggle off the unlock is remembered and skipped; with it on it is mirrored immediately.
    /// Returns false only when mirroring was attempted and failed, so callers such as Auto Unlock can stop.
    /// </summary>
    public async Task<bool> ReportAppUnlockAsync(string sourceId, string name, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (Status == MirrorStatus.Idle || !handled.Add(sourceId)) return true;
        }
        if (!Pair.MirrorAppUnlocks)
        {
            Write($"{name} was unlocked in Achievement Labs · not mirrored (setting is off).");
            return true;
        }
        Write($"{name} was unlocked in Achievement Labs · sending immediately.");
        return await ProcessAsync(sourceId, name, cancellationToken);
    }

    /// <summary>Restarts a stopped pair. Pending unlocks are either sent in order or discarded.</summary>
    public async Task<bool> ResumeAsync(bool sendPending, CancellationToken cancellationToken = default)
    {
        MirrorPendingUnlock[] queued;
        lock (gate)
        {
            if (Status != MirrorStatus.Stopped) return true;
            if (!Pair.ReadyToMonitor) throw new InvalidOperationException("Review every achievement in the match list before resuming.");
            queued = pending.ToArray(); pending.Clear();
            Status = MirrorStatus.Monitoring; StopReason = "";
        }
        if (!sendPending)
        {
            Write(queued.Length == 0 ? "Mirroring resumed." : $"Mirroring resumed · {queued.Length} pending unlock(s) discarded.");
            return true;
        }
        Write($"Mirroring resumed · sending {queued.Length} pending unlock(s) in order.");
        for (var i = 0; i < queued.Length; i++)
        {
            if (!await ProcessAsync(queued[i].SourceId, queued[i].Name, cancellationToken))
            {
                // Keep everything after the failure pending, in order, behind the one that failed.
                lock (gate) pending.AddRange(queued.Skip(i + 1));
                Notify();
                return false;
            }
        }
        return true;
    }

    private async Task<bool> ProcessAsync(string sourceId, string name, CancellationToken cancellationToken)
    {
        CancellationToken token;
        lock (gate) token = session.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        try { await sending.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { return true; } // Stopped while waiting; nothing is sent.
        try
        {
            lock (gate)
            {
                if (Status == MirrorStatus.Idle) return true;
                if (Status == MirrorStatus.Stopped)
                {
                    pending.Add(new(sourceId, name, now()));
                    Write($"{name} is pending · mirroring is stopped.", notify: false);
                    Notify();
                    return false;
                }
            }

            var match = Pair.FindBySource(sourceId);
            if (match == null || !match.IsResolved)
                return Fail(sourceId, name, $"{name} has no confirmed match. Rescan and review the match list.");
            if (match.State == MirrorMatchState.Ignored)
            {
                Write($"{name} has no {Pair.Target} equivalent · skipped as you chose.");
                return true;
            }
            var targetId = match.TargetId(Pair.Direction)!;
            var targetName = Pair.Direction == MirrorDirection.SteamToXbox ? match.XboxName : match.SteamName;

            var lastError = "";
            for (var attempt = 0; attempt < AttemptDelays.Length; attempt++)
            {
                if (AttemptDelays[attempt] > TimeSpan.Zero) await delay(AttemptDelays[attempt], linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    // Read first: a previous attempt may have landed even though its check failed.
                    if (IsUnlocked(await target.ReadAsync(linked.Token), targetId))
                    {
                        Write(attempt == 0 ? $"{targetName} was already unlocked on {Pair.Target}." : $"{targetName} confirmed on {Pair.Target}.");
                        Count(sourceId);
                        return true;
                    }
                    await target.UnlockAsync(targetId, linked.Token);
                    if (IsUnlocked(await target.ReadAsync(linked.Token), targetId))
                    {
                        Write($"{targetName} unlocked on {Pair.Target} · verified.");
                        Count(sourceId);
                        return true;
                    }
                    lastError = $"{Pair.Target} accepted the request but still shows it locked";
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception error) { lastError = error.Message; }
                if (attempt < AttemptDelays.Length - 1) Write($"Attempt {attempt + 1} for {targetName} failed: {lastError}. Retrying.", isError: true);
            }
            return Fail(sourceId, name, $"{targetName} could not be unlocked on {Pair.Target} after {AttemptDelays.Length} attempts: {lastError}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || token.IsCancellationRequested)
        {
            return true; // Stopped by the user or the caller; nothing more is sent.
        }
        finally { sending.Release(); }
    }

    /// <summary>Stops a monitoring pair for an outside reason (e.g. conflicting Xbox presence). Later unlocks become pending.</summary>
    public void Halt(string reason)
    {
        lock (gate)
        {
            if (Status != MirrorStatus.Monitoring) return;
            Status = MirrorStatus.Stopped; StopReason = reason;
        }
        Write("Stopped: " + reason, isError: true);
        Stopped?.Invoke($"Achievement Mirror stopped · {Pair.DisplayName}", reason);
    }

    private bool Fail(string sourceId, string name, string reason)
    {
        lock (gate)
        {
            Status = MirrorStatus.Stopped; StopReason = reason;
            pending.Insert(0, new(sourceId, name, now()));
        }
        Write("Stopped: " + reason, isError: true);
        Stopped?.Invoke($"Achievement Mirror stopped · {Pair.DisplayName}", reason);
        return false;
    }

    private void Count(string sourceId)
    {
        lock (gate) MirroredCount++;
        Mirrored?.Invoke(sourceId, now());
        Notify();
    }

    private static bool IsUnlocked(IReadOnlyList<MirrorAchievement> list, string id) => list.Any(a => a.Id == id && a.Unlocked);

    private string Describe() => Pair.Direction == MirrorDirection.SteamToXbox ? "Steam → Xbox" : "Xbox → Steam";

    private void Write(string message, bool isError = false, bool notify = true)
    {
        var entry = new MirrorLogEntry(now(), message, isError);
        lock (gate)
        {
            log.Add(entry);
            if (log.Count > MaxLogEntries) log.RemoveRange(0, log.Count - MaxLogEntries);
        }
        Logged?.Invoke(entry);
        if (notify) Notify();
    }

    private void Notify() => Changed?.Invoke();
}
