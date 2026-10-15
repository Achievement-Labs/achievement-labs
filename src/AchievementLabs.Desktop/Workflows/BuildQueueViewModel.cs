using AchievementLabs.Core;

namespace AchievementLabs.Desktop.Workflows;

public partial class BuildQueueViewModel : ObservableObject
{
    private readonly NativeAccountContext account;
    private readonly NativeNotices notices;
    private readonly EventCatalogClient catalog;
    private readonly BuildQueueCoordinator? coordinator;
    [ObservableProperty] private string referenceGamertag = "";
    [ObservableProperty] private string titleId = "";
    [ObservableProperty] private double speedMultiplier = 1;
    [ObservableProperty] private string status = "Add games, then start. The queue waits for more games until you stop it.";
    [ObservableProperty] private bool isAdding;
    [ObservableProperty] private BuildQueueItem? selectedItem;
    [ObservableProperty] private AutoUnlockerViewModel? activeGame;
    public IReadOnlyList<BuildQueueItem> Items => coordinator?.Items ?? [];
    public bool IsRunning => coordinator?.IsRunning == true;
    public bool CanAdd => !IsAdding && coordinator != null && account.InitComplete;
    public bool CanConfigureBuffer => !IsRunning;
    public string StartStopLabel => IsRunning ? "Stop build queue" : "Start build queue";
    public Action<AutoUnlockerViewModel>? ConfigureRunner { get; set; }

    public BuildQueueViewModel(NativeAccountContext account, NativeNotices notices, EventCatalogClient catalog)
    {
        this.account = account; this.notices = notices; this.catalog = catalog;
        try
        {
            coordinator = new BuildQueueCoordinator(AchievementLabsPaths.LocalFile("build-queue/manifest.json"));
            coordinator.Changed += Refresh;
            coordinator.BufferWaiting += remaining => Status = $"Between games · previous title spoofing stopped · next title starts in {Math.Ceiling(remaining.TotalSeconds)} seconds.";
        }
        catch { Status = "The saved build queue could not be read. Preserve the manifest and repair it before continuing."; }
    }

    public void Refresh()
    {
        var selectedId = SelectedItem?.Id;
        OnPropertyChanged(nameof(Items)); OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(StartStopLabel));
        OnPropertyChanged(nameof(CanConfigureBuffer));
        SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
    }
    partial void OnIsAddingChanged(bool value) => OnPropertyChanged(nameof(CanAdd));

    [RelayCommand]
    public async Task AddGameAsync()
    {
        if (!CanAdd || coordinator == null) return;
        if (!uint.TryParse(TitleId.Trim(), out var title) || title == 0 || string.IsNullOrWhiteSpace(ReferenceGamertag)
            || !double.IsFinite(SpeedMultiplier) || SpeedMultiplier <= 0)
        { Status = "Enter a reference gamertag, decimal title ID, and positive speed."; return; }
        var owner = account.XUIDOnly;
        var id = Guid.NewGuid();
        var builder = new AutoUnlockerViewModel(notices, account)
        {
            EventCatalog = catalog, StatePath = coordinator.CheckpointPath(id),
            ReferenceGamertag = ReferenceGamertag.Trim(), TitleId = title.ToString(), SpeedMultiplier = SpeedMultiplier
        };
        IsAdding = true;
        try
        {
            await builder.BuildQueue();
            if (account.XUIDOnly != owner) throw new InvalidOperationException("The connected account changed. Add this game again.");
            var prepared = builder.PreparedState;
            if (prepared == null || !builder.QueueReady) { Status = builder.StatusText; return; }
            var added = coordinator.Enqueue(new BuildQueueItem(id, owner, prepared.TitleId, prepared.GameName,
                prepared.ReferenceXuid, prepared.SpeedMultiplier, DateTimeOffset.UtcNow));
            Status = added ? $"Added {prepared.GameName}. You can add another game while the queue runs."
                : "This game is already pending, running, or paused for this account.";
            if (!added && File.Exists(builder.StatePath)) File.Delete(builder.StatePath);
        }
        catch (OperationCanceledException) { Status = "Adding the game was cancelled."; }
        catch { Status = "Could not add this game. Your existing queue is preserved."; }
        finally { IsAdding = false; builder.ReleaseClient(); Refresh(); }
    }

    [RelayCommand]
    public void RemoveSelected()
    {
        if (SelectedItem == null || coordinator == null) return;
        try { if (!coordinator.Remove(SelectedItem.Id)) Status = "Stop the active game before removing it."; }
        catch { Status = "Could not save the queue change."; }
    }

    [RelayCommand]
    public void ResumeSelected()
    {
        if (SelectedItem == null || coordinator == null || SelectedItem.Xuid != account.XUIDOnly) return;
        try { if (coordinator.Retry(SelectedItem.Id)) Status = "Game queued to resume from its saved progress."; }
        catch { Status = "Could not save the queue change."; }
    }

    public async Task RunAsync(CancellationToken token, TimeSpan betweenTitles)
    {
        if (coordinator == null || !account.InitComplete) return;
        Status = "Build queue running. Waiting for games when the list is empty.";
        try { await coordinator.RunAsync(account.XUIDOnly, ExecuteAsync, token, betweenTitles); Status = "Build queue stopped. Saved progress is ready to resume."; }
        catch (BuildQueueHandoffException error) { Status = error.Message; }
        catch { Status = "The queue stopped because saved state could not be updated. Check the manifest and game checkpoints."; }
        finally { ActiveGame = null; Refresh(); }
    }

    private async Task<BuildQueueItemStatus> ExecuteAsync(BuildQueueItem item, CancellationToken token)
    {
        if (item.Xuid != account.XUIDOnly || coordinator == null) return BuildQueueItemStatus.Paused;
        var runner = new AutoUnlockerViewModel(notices, account) { ContinueAfterFailure = false, EventCatalog = catalog, StatePath = coordinator.CheckpointPath(item.Id) };
        ConfigureRunner?.Invoke(runner);
        ActiveGame = runner;
        Status = $"Running {item.TitleName} · spoofing this title while its achievements are processed.";
        runner.WaitingForToken += waiting => coordinator.SetWaitingForToken(item.Id, waiting);
        try { return await runner.RunPreparedAsync(token); }
        finally { runner.ReleaseClient(); ActiveGame = null; }
    }

    public void Stop() { coordinator?.Stop(); ActiveGame?.Stop(); Status = "Stopping build queue; progress will be kept."; }
}
