using System.Text.Json;
using System.Diagnostics;

namespace AchievementLabs.Core;

/// <summary>One title at a time, including titles appended while idle or running.</summary>
public sealed class BuildQueueCoordinator
{
    private readonly object sync = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly List<BuildQueueItem> items = [];
    private readonly string path;
    private CancellationTokenSource? stop;
    private bool running;
    public event Action? Changed;
    public event Action<TimeSpan>? BufferWaiting;
    public bool IsRunning { get { lock (sync) return running; } }
    public IReadOnlyList<BuildQueueItem> Items { get { lock (sync) return items.ToArray(); } }
    public string CheckpointPath(Guid id) => Path.Combine(Path.GetDirectoryName(path)!, "jobs", id.ToString("N") + ".json");

    public BuildQueueCoordinator(string path)
    {
        this.path = Path.GetFullPath(path);
        if (!File.Exists(this.path)) return;
        var saved = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(this.path)) ?? throw new InvalidDataException("Invalid build queue file.");
        if (saved.Version != 1 || saved.Items == null || saved.Items.Select(i => i.Id).Distinct().Count() != saved.Items.Count)
            throw new InvalidDataException("Unsupported or invalid build queue file.");
        items.AddRange(saved.Items.Select(i => i.Status is BuildQueueItemStatus.Running or BuildQueueItemStatus.WaitingForToken
            ? i with { Status = BuildQueueItemStatus.Paused } : i));
    }

    public bool Enqueue(BuildQueueItem item)
    {
        lock (sync)
        {
            if (items.Any(i => i.Id == item.Id || i.Xuid == item.Xuid && i.TitleId == item.TitleId
                && i.Status is BuildQueueItemStatus.Pending or BuildQueueItemStatus.Running or BuildQueueItemStatus.WaitingForToken or BuildQueueItemStatus.Paused)) return false;
            items.Add(item with { Status = BuildQueueItemStatus.Pending });
            try { SaveLocked(); } catch { items.RemoveAt(items.Count - 1); throw; }
        }
        Signal(); Changed?.Invoke(); return true;
    }

    public bool Remove(Guid id)
    {
        lock (sync)
        {
            var index = items.FindIndex(i => i.Id == id && i.Status != BuildQueueItemStatus.Running && i.Status != BuildQueueItemStatus.WaitingForToken);
            if (index < 0) return false;
            var removed = items[index]; items.RemoveAt(index);
            try { SaveLocked(); } catch { items.Insert(index, removed); throw; }
        }
        Changed?.Invoke(); return true;
    }

    public bool Retry(Guid id)
    {
        lock (sync)
        {
            var index = items.FindIndex(i => i.Id == id && i.Status is BuildQueueItemStatus.Paused or BuildQueueItemStatus.Failed);
            if (index < 0) return false;
            if (items.Any(i => i.Id != id && i.Xuid == items[index].Xuid && i.TitleId == items[index].TitleId
                && i.Status is BuildQueueItemStatus.Pending or BuildQueueItemStatus.Running or BuildQueueItemStatus.WaitingForToken or BuildQueueItemStatus.Paused)) return false;
            var old = items[index]; items[index] = old with { Status = BuildQueueItemStatus.Pending };
            try { SaveLocked(); } catch { items[index] = old; throw; }
        }
        Signal(); Changed?.Invoke(); return true;
    }

    public void Stop() { lock (sync) stop?.Cancel(); }

    public async Task RunAsync(string xuid, Func<BuildQueueItem, CancellationToken, Task<BuildQueueItemStatus>> execute,
        CancellationToken cancellationToken = default, TimeSpan? betweenTitles = null)
    {
        var buffer = betweenTitles ?? TimeSpan.Zero;
        if (buffer < TimeSpan.Zero || buffer > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(betweenTitles));
        long? previousEnd = null;
        CancellationTokenSource cancellation;
        lock (sync)
        {
            if (running) throw new InvalidOperationException("The build queue is already running.");
            running = true; stop = cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        Changed?.Invoke();
        try
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                BuildQueueItem? next;
                var remainingBuffer = previousEnd.HasValue ? buffer - Stopwatch.GetElapsedTime(previousEnd.Value) : TimeSpan.Zero;
                lock (sync)
                {
                    next = items.FirstOrDefault(i => i.Xuid == xuid && i.Status == BuildQueueItemStatus.Pending);
                    if (next != null && remainingBuffer <= TimeSpan.Zero)
                    {
                        var index = items.FindIndex(i => i.Id == next.Id);
                        items[index] = next with { Status = BuildQueueItemStatus.Running };
                        SaveLocked();
                    }
                }
                if (next == null) { await wake.WaitAsync(cancellation.Token); continue; }
                if (remainingBuffer > TimeSpan.Zero)
                {
                    BufferWaiting?.Invoke(remainingBuffer);
                    await Task.Delay(remainingBuffer > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remainingBuffer, cancellation.Token);
                    continue; // Recheck pending jobs after the buffer; none has started yet.
                }
                Changed?.Invoke();
                var result = BuildQueueItemStatus.Failed;
                try
                {
                    result = await execute(next, cancellation.Token);
                    if (result is not (BuildQueueItemStatus.Completed or BuildQueueItemStatus.Failed or BuildQueueItemStatus.Paused))
                        result = BuildQueueItemStatus.Failed;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { result = BuildQueueItemStatus.Paused; }
                catch (BuildQueueHandoffException) { SetStatus(next.Id, BuildQueueItemStatus.Failed); throw; }
                catch { result = BuildQueueItemStatus.Failed; }
                SetStatus(next.Id, result);
                previousEnd = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            lock (sync) { running = false; stop = null; }
            cancellation.Dispose(); Changed?.Invoke();
        }
    }

    public void SetWaitingForToken(Guid id, bool waiting)
        => SetStatus(id, waiting ? BuildQueueItemStatus.WaitingForToken : BuildQueueItemStatus.Running);

    private void SetStatus(Guid id, BuildQueueItemStatus status)
    {
        lock (sync)
        {
            var index = items.FindIndex(i => i.Id == id);
            if (index < 0) return;
            items[index] = items[index] with { Status = status }; SaveLocked();
        }
        Changed?.Invoke();
    }

    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Manifest(1, items.ToArray()), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private sealed record Manifest(int Version, IReadOnlyList<BuildQueueItem> Items);
}

public sealed class BuildQueueHandoffException(string message) : Exception(message);
