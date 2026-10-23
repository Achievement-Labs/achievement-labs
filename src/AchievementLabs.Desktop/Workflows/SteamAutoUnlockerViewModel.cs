using System.Collections.ObjectModel;
using AchievementLabs.Models;
using AchievementLabs.Services.Steam;

namespace AchievementLabs.Desktop.Workflows;

public partial class SteamAutoUnlockerViewModel : ObservableObject, INativePageLifecycle
{
    private readonly ISteamAchievementService _steamAchievementService;
    private readonly NativeNotices _snackbarService;
    private readonly Random _random = new();
    private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(4);
    /// <summary>Achievement Mirror hook (app ID, API name, name). False stops the queue before the next unlock.</summary>
    public Func<long, string, string, CancellationToken, Task<bool>>? MirrorUnlockAsync { get; set; }
    /// <summary>Called before each unlock (app ID, API name, name) and, on failure, to withdraw it.</summary>
    public Action<long, string, string>? MirrorExpect { get; set; }
    public Action<long, string>? MirrorCancel { get; set; }
    private CancellationTokenSource? _operationCancellation;

    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private string _targetProfile = string.Empty;
    [ObservableProperty] private string _statusText = "Enter a Steam App ID, load locked achievements, then start the queue.";
    [ObservableProperty] private string _targetProfileStatus = "No target profile loaded.";
    [ObservableProperty] private int _minimumDelaySeconds = 30;
    [ObservableProperty] private int _maximumDelaySeconds = 90;
    [ObservableProperty] private int _profileTimeScale = 1;
    [ObservableProperty] private int _maximumProfileDelaySeconds = 3600;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isProfileDelayQueue;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _completedCount;

    public ObservableCollection<SteamAutoUnlockQueueItem> QueueItems { get; } = new();
    public string ProgressText => $"{CompletedCount} / {CompletedCount + QueuedCount} achievements completed";
    partial void OnCompletedCountChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnQueuedCountChanged(int value) => OnPropertyChanged(nameof(ProgressText));

    public SteamAutoUnlockerViewModel(ISteamAchievementService steamAchievementService, NativeNotices snackbarService)
    {
        _steamAchievementService = steamAchievementService;
        _snackbarService = snackbarService;
    }

    public void OnNavigatedTo()
    {
    }

    public void OnNavigatedFrom()
    {
        Stop();
    }

    [RelayCommand]
    private async Task LoadQueue()
    {
        if (!TryGetAppId(out var steamAppId))
            return;

        await RunOperation(async cancellationToken =>
        {
            StatusText = $"Loading locked achievements for Steam app {steamAppId}...";
            var achievements = await _steamAchievementService.LoadAchievementsAsync(steamAppId, cancellationToken);
            var locked = achievements
                .Where(achievement => !achievement.IsUnlocked)
                .OrderBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            QueueItems.Clear();
            foreach (var achievement in locked)
            {
                QueueItems.Add(new SteamAutoUnlockQueueItem
                {
                    Id = achievement.Id,
                    Name = achievement.Name,
                    Description = achievement.Description,
                    Status = "Queued",
                    Achievement = achievement
                });
            }

            IsProfileDelayQueue = false;
            TargetProfileStatus = "Random delay queue loaded.";
            UpdateCounts();
            StatusText = $"Queued {QueuedCount} locked achievements for Steam app {steamAppId}.";
            Show("Steam auto unlocker", StatusText, NoticeAppearance.Success, NoticeSymbol.Checkmark24);
        });
    }

    [RelayCommand]
    private async Task LoadProfileQueue()
    {
        if (!TryGetAppId(out var steamAppId))
            return;

        if (string.IsNullOrWhiteSpace(TargetProfile))
        {
            Show("Target profile", "Enter a Steam ID64, profile URL, vanity URL, or vanity name.", NoticeAppearance.Caution, NoticeSymbol.Warning24);
            return;
        }

        await RunOperation(async cancellationToken =>
        {
            NormalizeProfileDelays();
            StatusText = "Resolving target Steam profile...";
            var profile = await _steamAchievementService.ResolveSteamProfileAsync(TargetProfile, cancellationToken);

            StatusText = $"Loading target achievement timeline for {profile.DisplayName}...";
            var achievements = await _steamAchievementService.LoadAchievementsAsync(steamAppId, cancellationToken);
            var localById = achievements.ToDictionary(achievement => achievement.Id, StringComparer.OrdinalIgnoreCase);
            var targetUnlocks = await _steamAchievementService.LoadTargetAchievementUnlocksAsync(profile.SteamId, steamAppId, cancellationToken);

            QueueItems.Clear();
            DateTime? previousIncludedUnlock = null;
            foreach (var targetUnlock in targetUnlocks)
            {
                if (!localById.TryGetValue(targetUnlock.Id, out var achievement) || achievement.IsUnlocked)
                    continue;

                var delay = previousIncludedUnlock.HasValue
                    ? targetUnlock.UnlockTime - previousIncludedUnlock.Value
                    : TimeSpan.Zero;
                previousIncludedUnlock = targetUnlock.UnlockTime;

                QueueItems.Add(new SteamAutoUnlockQueueItem
                {
                    Id = achievement.Id,
                    Name = achievement.Name,
                    Description = achievement.Description,
                    Status = "Queued",
                    DelaySeconds = ScaleProfileDelay(delay),
                    TargetUnlockTime = targetUnlock.UnlockTime,
                    Achievement = achievement
                });
            }

            IsProfileDelayQueue = true;
            TargetProfileStatus = $"Target: {profile.DisplayName} ({profile.SteamId}) | {targetUnlocks.Count} public unlocks found.";
            UpdateCounts();
            StatusText = $"Queued {QueuedCount} locked achievements from target profile timeline.";
            Show("Steam auto unlocker", StatusText, NoticeAppearance.Success, NoticeSymbol.Checkmark24);
        });
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsRunning)
            return;

        if (!TryGetAppId(out var steamAppId))
            return;

        if (QueueItems.Count == 0)
        {
            Show("Steam auto unlocker", "Load the queue first.", NoticeAppearance.Caution, NoticeSymbol.Warning24);
            return;
        }

        NormalizeDelays();
        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource();
        IsRunning = true;
        IsBusy = true;

        try
        {
            foreach (var item in QueueItems.Where(item => item.Status != "Unlocked"))
            {
                _operationCancellation.Token.ThrowIfCancellationRequested();
                if (item.Achievement is null)
                    continue;

                item.DelaySeconds = IsProfileDelayQueue ? item.DelaySeconds : NextDelaySeconds();
                item.Status = $"Waiting {item.DelaySeconds}s";
                StatusText = $"Next: {item.Name}";
                await Task.Delay(TimeSpan.FromSeconds(item.DelaySeconds), _operationCancellation.Token);


                item.Status = "Unlocking";
                item.Achievement.DesiredUnlocked = true;
                MirrorExpect?.Invoke(steamAppId, item.Achievement.Id, item.Name);
                try { await _steamAchievementService.StoreAchievementsAsync(steamAppId, new[] { item.Achievement }, _operationCancellation.Token); }
                catch { MirrorCancel?.Invoke(steamAppId, item.Achievement.Id); throw; }
                item.Achievement.IsUnlocked = true;
                item.Achievement.UnlockTime = DateTime.Now;
                item.Status = "Unlocked";
                item.UnlockedAt = DateTime.Now;
                UpdateCounts();
                if (MirrorUnlockAsync != null && !await MirrorUnlockAsync(steamAppId, item.Achievement.Id, item.Name, _operationCancellation.Token))
                    throw new InvalidOperationException($"Achievement Mirror couldn't copy {item.Name} to Xbox, so the Steam auto unlocker stopped. Resolve it in Achievement Mirror first.");
            }

            StatusText = $"Steam auto unlocker finished: {CompletedCount}/{QueueItems.Count} unlocked.";
            Show("Steam auto unlocker", StatusText, NoticeAppearance.Success, NoticeSymbol.Checkmark24);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Steam auto unlocker stopped.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            Show("Steam auto unlocker", ex.Message, NoticeAppearance.Danger, NoticeSymbol.ErrorCircle24);
        }
        finally
        {
            IsRunning = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _operationCancellation?.Cancel();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (IsRunning)
            return;

        QueueItems.Clear();
        UpdateCounts();
        StatusText = "Steam auto unlock queue cleared.";
    }


    private async Task RunOperation(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;

        _operationCancellation?.Cancel();
        _operationCancellation = new CancellationTokenSource();

        try
        {
            IsBusy = true;
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Steam operation cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            Show("Steam auto unlocker", ex.Message, NoticeAppearance.Danger, NoticeSymbol.ErrorCircle24);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryGetAppId(out long steamAppId)
    {
        if (!long.TryParse(AppId, out steamAppId) || steamAppId <= 0)
        {
            Show("Steam app ID", "Enter a valid numeric Steam app ID.", NoticeAppearance.Caution, NoticeSymbol.Warning24);
            return false;
        }

        return true;
    }

    private void NormalizeDelays()
    {
        MinimumDelaySeconds = Math.Max(0, MinimumDelaySeconds);
        MaximumDelaySeconds = Math.Max(MinimumDelaySeconds, MaximumDelaySeconds);
        NormalizeProfileDelays();
    }

    private void NormalizeProfileDelays()
    {
        ProfileTimeScale = Math.Max(1, ProfileTimeScale);
        MaximumProfileDelaySeconds = Math.Max(0, MaximumProfileDelaySeconds);
    }

    private int NextDelaySeconds()
    {
        if (MinimumDelaySeconds == MaximumDelaySeconds)
            return MinimumDelaySeconds;

        return _random.Next(MinimumDelaySeconds, MaximumDelaySeconds + 1);
    }

    private int ScaleProfileDelay(TimeSpan delay)
    {
        var seconds = Math.Max(0, delay.TotalSeconds / ProfileTimeScale);
        if (MaximumProfileDelaySeconds > 0)
            seconds = Math.Min(seconds, MaximumProfileDelaySeconds);

        return (int)Math.Round(seconds);
    }

    private void UpdateCounts()
    {
        QueuedCount = QueueItems.Count(item => item.Status != "Unlocked");
        CompletedCount = QueueItems.Count(item => item.Status == "Unlocked");
    }

    private void Show(string title, string message, NoticeAppearance appearance, NoticeSymbol symbol)
    {
        _snackbarService.Show(title, message, appearance, new NoticeIcon(symbol), _snackbarDuration);
    }
}
