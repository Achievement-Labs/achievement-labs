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
    [ObservableProperty] private string windows8StartDate = "";
    [ObservableProperty] private string windows8StartTime = "";
    public bool IsWindows8Title => DesktopModel.IsKnownWindows8Title(TitleId);
    partial void OnTitleIdChanged(string value) => OnPropertyChanged(nameof(IsWindows8Title));
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
    public Func<string, string, CancellationToken, Task<AchievementsResponse?>>? ReadAchievementsForAccountAsync { get; set; }
    public Func<string, string, CancellationToken, Task<AchievementsResponse?>>? ReadWindows8ReferenceAsync { get; set; }
    public Func<string, CancellationToken, Task<AchievementsResponse?>>? ReadWindows8CatalogAsync { get; set; }
    public bool Windows8ReferenceEditionConfirmed { get; set; }
    private Windows8ReferenceImport? importedWindows8Reference;

    public async Task ImportWindows8ReferenceAsync(string path)
    {
        var importer = new AutoUnlockerViewModel(notices, account)
        {
            ReferenceGamertag = ReferenceGamertag, TitleId = TitleId,
            Windows8ReferenceEditionConfirmed = Windows8ReferenceEditionConfirmed
        };
        await importer.ImportWindows8ReferenceAsync(path);
        importedWindows8Reference = importer.ImportedWindows8Reference;
        Status = importer.StatusText;
    }

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

    public async Task AcquireWindows8TokenAsync(string? receiptPath = null)
    {
#if WINDOWS
        var owner = account.XUIDOnly;
        if (!CanAdd || !uint.TryParse(TitleId, out _) || !ulong.TryParse(owner, out _) ||
            !DesktopModel.IsKnownWindows8Title(TitleId))
        { Status = "Connect an Xbox account and enter a Windows 8 title ID first."; return; }
        IsAdding = true;
        try
        {
            Status = receiptPath == null ? "Getting Windows 8 license and token..." : "Getting token from selected pop.bin...";
            var title = DesktopModel.ResolveWindows8TitleId(TitleId);
            var held = receiptPath == null
                ? await Windows8LicenseClient.AcquireAsync(title, owner, CancellationToken.None)
                : await Windows8LicenseClient.AcquireFromReceiptAsync(title, owner, receiptPath, CancellationToken.None);
            if (account.XUIDOnly != owner || DesktopModel.ResolveWindows8TitleId(TitleId) != title)
                throw new InvalidOperationException("The account or Windows 8 title changed during token acquisition.");
            Status = $"Windows 8 token ready until {held.ExpiresAt.ToLocalTime():t}." +
                (held.SettingsMetadataChanged ? " Windows updated settings metadata; private before/after copies were retained." : "");
        }
        catch (Exception error) { Status = error.Message; }
        finally { IsAdding = false; }
#else
        Status = "Windows 8 token acquisition requires Windows.";
#endif
    }

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
            ReferenceGamertag = ReferenceGamertag.Trim(), TitleId = title.ToString(), SpeedMultiplier = SpeedMultiplier,
            Windows8StartDate = Windows8StartDate, Windows8StartTime = Windows8StartTime,
            ReadAchievementsForAccountAsync = ReadAchievementsForAccountAsync,
            ReadWindows8ReferenceAsync = ReadWindows8ReferenceAsync,
            ReadWindows8CatalogAsync = ReadWindows8CatalogAsync,
            ImportedWindows8Reference = importedWindows8Reference,
            Windows8ReferenceEditionConfirmed = Windows8ReferenceEditionConfirmed
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
            Status = added ? $"Added {prepared.GameName}." + (prepared.IsWindows8
                ? " Get this game's license and token or open its signed pop.bin before starting."
                : " You can add another game while the queue runs.")
                : "This game is already pending, running, or paused for this account.";
            if (added) { Windows8StartDate = ""; Windows8StartTime = ""; }
            if (!added && File.Exists(builder.StatePath)) File.Delete(builder.StatePath);
        }
        catch (OperationCanceledException) { Status = "Adding the game was cancelled."; }
        catch { Status = "Could not add this game. Your existing queue is preserved."; }
        finally { IsAdding = false; builder.ReleaseClient(); Refresh(); }
    }

    public Task AddSavedGameAsync(AchievementLabs.Models.AutoUnlockState state)
    {
        if (coordinator == null || state.OwnerXuid != account.XUIDOnly || !CanAdd)
            throw new InvalidOperationException("Connect the saved queue's account before appending.");
        var id = Guid.NewGuid(); var path = coordinator.CheckpointPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        state.IsRunning = false;
        File.WriteAllText(path,Newtonsoft.Json.JsonConvert.SerializeObject(state));
        try
        {
            if (!coordinator.Enqueue(new BuildQueueItem(id,state.OwnerXuid,state.TitleId,state.GameName,state.ReferenceXuid,state.SpeedMultiplier,DateTimeOffset.UtcNow)))
                throw new InvalidOperationException("This game is already pending in the build queue.");
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        Status = $"Saved queue for {state.GameName} added. Review before starting."; Refresh();
        return Task.CompletedTask;
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
        Status = $"Running {item.TitleName} · processing its achievements.";
        runner.WaitingForToken += waiting => coordinator.SetWaitingForToken(item.Id, waiting);
        try { return await runner.RunPreparedAsync(token); }
        finally { runner.ReleaseClient(); ActiveGame = null; }
    }

    /// <summary>Remaining achievements of every game still to run, read from their saved checkpoints (for the Achievement Mirror check).</summary>
    public IReadOnlyList<(string TitleId, string TitleName, IReadOnlyList<string> AchievementIds)> RemainingQueues()
    {
        if (coordinator == null) return [];
        var result = new List<(string, string, IReadOnlyList<string>)>();
        foreach (var item in Items.Where(i => i.Xuid == account.XUIDOnly && i.Status is not (BuildQueueItemStatus.Completed or BuildQueueItemStatus.Cancelled)))
        {
            try
            {
                var path = coordinator.CheckpointPath(item.Id);
                var state = File.Exists(path) ? Newtonsoft.Json.JsonConvert.DeserializeObject<AchievementLabs.Models.AutoUnlockState>(File.ReadAllText(path)) : null;
                var ids = state?.Queue.Where(e => !e.Completed).Select(e => e.AchievementId).ToArray() ?? [];
                result.Add((item.TitleId, item.TitleName, ids));
            }
            catch { result.Add((item.TitleId, item.TitleName, [])); }
        }
        return result;
    }

    public void Stop() { coordinator?.Stop(); ActiveGame?.Stop(); Status = "Stopping build queue; progress will be kept."; }
}

