using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using AchievementLabs.Models;
using Newtonsoft.Json;

namespace AchievementLabs.Desktop.Workflows;

public partial class AutoUnlockerViewModel
{
    public sealed record SavedGameQueueInfo(string TitleId, string Name, int Count, string Path)
    {
        public string Summary => $"{Name} · {Count} achievements · {TitleId}";
    }
    public ObservableCollection<SavedGameQueueInfo> SavedGameQueues { get; } = new();
    private SavedGameQueueInfo? selectedSavedGameQueue;
    public SavedGameQueueInfo? SelectedSavedGameQueue { get => selectedSavedGameQueue; set => SetProperty(ref selectedSavedGameQueue,value); }
    public Func<AutoUnlockState, Task>? AppendSavedGameAsync { get; set; }
    public Func<bool> CanDeleteSavedQueue { get; set; } = () => true;
    public async Task DeleteSavedGameQueueAsync(string titleId, string ownerXuid)
    {
        if (IsRunning || !CanDeleteSavedQueue() || account.XUIDOnly != ownerXuid) { StatusText = "Pause Auto Unlock before deleting saved queues."; return; }
        try
        {
            if (!Regex.IsMatch(ownerXuid,@"^\d{1,20}$") || !uint.TryParse(titleId,out var title) || title == 0) return;
            var entry = SavedGameQueues.FirstOrDefault(x => x.TitleId == titleId);
            if (entry == null) return;
            var legacy = LoadState();
            // Remove the loaded checkpoint too, so legacy migration cannot recreate the save.
            if (legacy != null && OwnSavedState(legacy) && legacy.TitleId == titleId && File.Exists(StatePath)) File.Delete(StatePath);
            if (_state != null && OwnSavedState(_state) && _state.TitleId == titleId) await ClearExistingSession();
            File.Delete(Path.Combine(SavedGamesDirectory,titleId+".json"));
            RefreshSavedGameQueues();
            CheckForExistingState();
            StatusText = "Saved game queue deleted. Other saved games were retained.";
        }
        catch { StatusText = "Could not finish deleting the saved queue. Check folder access and try again."; }
    }
    public string SavedGameQueuesRoot { get; set; } = Path.Combine(Path.GetDirectoryName(AutoUnlockState.GetSavePath())!,"saved-game-queues");
    private string SavedGamesDirectory => Path.Combine(SavedGameQueuesRoot,account.XUIDOnly);
    private bool loadingGameQueue;
    public async Task<bool> LoadSavedQueueForTitleAsync(string titleId)
    {
        if (IsRunning || loadingGameQueue || !CanDeleteSavedQueue() || !uint.TryParse(titleId, out var id) || id == 0) return false;
        if (_state != null && OwnSavedState(_state) && _state.TitleId == titleId) return true;
        loadingGameQueue = true;
        try
        {
            if (_state != null && OwnSavedState(_state)) SaveState();
            RefreshSavedGameQueues();
            var saved = SavedGameQueues.FirstOrDefault(q => q.TitleId == titleId);
            if (saved != null)
            {
                SelectedSavedGameQueue = saved;
                // Validate owner/title before replacing the active checkpoint.
                _ = ReadSelectedSavedGame();
                await LoadSelectedGameQueueAsync();
                return _state != null && OwnSavedState(_state) && _state.TitleId == titleId;
            }
            ClearLoadedQueueForTitle(titleId);
            StatusText = "No saved queue for this game. Load a reference to build one.";
            return false;
        }
        catch
        {
            ClearLoadedQueueForTitle(titleId);
            StatusText = "Saved queue could not be loaded. Existing saved files were retained.";
            return false;
        }
        finally { loadingGameQueue = false; }
    }
    private void ClearLoadedQueueForTitle(string titleId)
    {
        _state = null; QueueItems.Clear(); TitleId = titleId; SelectedSavedGameQueue = null;
        HasExistingState = false; ExistingStateInfo = ""; GameName = "Game: None";
        ReferenceInfo = "Reference User: None"; ProgressText = "Progress: 0/0";
        NextUnlockText = "Next Unlock: N/A"; TimeRemainingText = "Time Until Next: N/A";
        UpdateCompletionEstimate();
    }

    private bool OwnSavedState(AutoUnlockState state) => Regex.IsMatch(account.XUIDOnly,@"^\d{1,20}$") && state.OwnerXuid == account.XUIDOnly && uint.TryParse(state.TitleId,out var title) && title != 0;
    private void SaveGameQueueSnapshot(AutoUnlockState state)
    {
        if (!OwnSavedState(state) || state.Queue.Count == 0) return;
        Directory.CreateDirectory(SavedGamesDirectory);
        var path = Path.Combine(SavedGamesDirectory,state.TitleId+".json");
        File.WriteAllText(path+".tmp",JsonConvert.SerializeObject(state,Formatting.Indented));
        File.Move(path+".tmp",path,true);
        var info = new SavedGameQueueInfo(state.TitleId,state.GameName,state.Queue.Count,path);
        var existing = SavedGameQueues.FirstOrDefault(x => x.TitleId == state.TitleId);
        var selected = SelectedSavedGameQueue?.TitleId;
        if (existing != null) SavedGameQueues[SavedGameQueues.IndexOf(existing)] = info; else SavedGameQueues.Add(info);
        if (selected == state.TitleId) SelectedSavedGameQueue = info;
    }
    private void RefreshSavedGameQueues()
    {
        if (!Regex.IsMatch(account.XUIDOnly,@"^\d{1,20}$")) { SavedGameQueues.Clear(); return; }
        var selected = SelectedSavedGameQueue?.TitleId;
        SavedGameQueues.Clear();
        try
        {
            if (Directory.Exists(SavedGamesDirectory))
                foreach (var path in Directory.EnumerateFiles(SavedGamesDirectory,"*.json"))
                {
                    try
                    {
                        var state = JsonConvert.DeserializeObject<AutoUnlockState>(File.ReadAllText(path));
                        if (state != null && OwnSavedState(state)) SavedGameQueues.Add(new(state.TitleId,state.GameName,state.Queue.Count,path));
                    }
                    catch { /* Keep an unreadable file for repair; do not overwrite it. */ }
                }
            var legacy = LoadState();
            if (legacy != null && OwnSavedState(legacy) && !SavedGameQueues.Any(x => x.TitleId == legacy.TitleId)) SaveGameQueueSnapshot(legacy);
            SelectedSavedGameQueue = SavedGameQueues.FirstOrDefault(x => x.TitleId == selected) ?? SavedGameQueues.FirstOrDefault();
        }
        catch { StatusText = "Some saved game queues could not be read. Existing files were retained."; }
    }
    private AutoUnlockState ReadSelectedSavedGame()
    {
        var entry = SelectedSavedGameQueue ?? throw new InvalidOperationException("Choose a saved game first.");
        var state = JsonConvert.DeserializeObject<AutoUnlockState>(File.ReadAllText(entry.Path)) ?? throw new InvalidDataException("Saved game queue is incomplete.");
        if (!OwnSavedState(state) || state.TitleId != entry.TitleId) throw new InvalidDataException("Saved game queue belongs to a different account or title.");
        state.IsRunning = false; return state;
    }
    [RelayCommand]
    private void SaveCurrentGameQueue()
    {
        if (IsRunning || _state == null) return;
        try { SaveState(); StatusText = "Game queue saved for this account."; }
        catch { StatusText = "Could not save this game queue. Existing files were retained."; }
    }
    [RelayCommand]
    private async Task LoadSelectedGameQueueAsync()
    {
        if (IsRunning) return;
        try
        {
            var state = ReadSelectedSavedGame();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StatePath))!);
            File.WriteAllText(StatePath+".tmp",JsonConvert.SerializeObject(state,Formatting.Indented));
            File.Move(StatePath+".tmp",StatePath,true);
            await ResumeExistingSession(); StatusText = "Saved game loaded. Review and press Start when ready.";
        }
        catch { StatusText = "Could not load the selected game queue. Existing saves were retained."; }
    }
    [RelayCommand]
    private async Task AppendSelectedGameQueueAsync()
    {
        if (IsRunning || AppendSavedGameAsync == null) return;
        try { await AppendSavedGameAsync(ReadSelectedSavedGame()); StatusText = "Saved game sent to Build queue. Review there before starting."; }
        catch { StatusText = "Could not append this saved game. Existing queues were retained."; }
    }
}
