using System.IO;
using AchievementLabs.Core;
using AchievementLabs.Models;
using Newtonsoft.Json;

namespace AchievementLabs.Desktop.Workflows;
public partial class AutoUnlockerViewModel
{
    public string ExportQueueSyncFile()
    {
        if (IsRunning) throw new InvalidOperationException("Pause Auto Unlock before exporting.");
        if (_state != null) SaveState();
        RefreshSavedGameQueues();var transfer = new QueueTransfer { OwnerXuid=account.XUIDOnly };
        foreach (var saved in SavedGameQueues)
        {
            var state=JsonConvert.DeserializeObject<AutoUnlockState>(File.ReadAllText(saved.Path));
            if (state == null || !OwnSavedState(state)) throw new InvalidDataException("Saved queue account does not match.");
            transfer.Games.Add(new TransferGame { TitleId=state.TitleId,Name=state.GameName,Items=state.Queue.Select((item,index)=>new TransferAchievement { AchievementId=item.AchievementId,Name=item.AchievementName,DelaySeconds=item.DelaySeconds,RemainingDelaySeconds=index==state.CurrentIndex ? state.RemainingDelaySeconds : null,Status=item.Completed ? "Accepted" : index<state.CurrentIndex ? "Needs review" : "Waiting" }).ToList() });
        }
        transfer.Validate(account.XUIDOnly);return JsonConvert.SerializeObject(transfer,Formatting.Indented);
    }
    public async Task ImportQueueSyncFileAsync(string json)
    {
        if (IsRunning || ReadQueueAchievementsAsync == null) throw new InvalidOperationException("Pause and connect before importing queues.");
        var owner=account.XUIDOnly;var transfer=QueueTransfer.Parse(json,owner);var prepared=new List<AutoUnlockState>();
        foreach (var saved in transfer.Games)
        {
            StatusText=$"Checking imported queue {prepared.Count+1}/{transfer.Games.Count}: {saved.Name}";
            var current=await ReadQueueAchievementsAsync(saved.TitleId,CancellationToken.None) ?? throw new InvalidDataException("Could not verify game achievements.");
            if (account.XUIDOnly!=owner) throw new InvalidOperationException("Account changed during import.");
            var state=new AutoUnlockState { OwnerXuid=owner,TitleId=saved.TitleId,GameName=saved.Name,StopOnFailure=true,NotifyOnFailure=true,IsEventBased=UsesEvents(current.achievements),ServiceConfigId=current.achievements.FirstOrDefault()?.serviceConfigId ?? "" };
            foreach (var item in saved.Items)
            {
                var row=current.achievements.FirstOrDefault(x=>x.id==item.AchievementId && string.Equals(x.name,item.Name,StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Imported achievement does not match this edition: "+item.Name);
                var unlocked=string.Equals(row.progressState,"Achieved",StringComparison.OrdinalIgnoreCase);
                // Unconfirmed transferred submissions are not silently replayed.
                if (!unlocked && item.Status!="Waiting") throw new InvalidDataException("Refresh or reset previously submitted items before importing into PC: "+item.Name);
                state.Queue.Add(new AutoUnlockQueueEntry { AchievementId=row.id,AchievementName=row.name,AchievementDescription=QueueDescription(row),DelaySeconds=item.DelaySeconds,Completed=unlocked });
            }
            state.CurrentIndex=state.Queue.FindIndex(x=>!x.Completed);if(state.CurrentIndex<0)state.CurrentIndex=state.Queue.Count;
            if(state.CurrentIndex<state.Queue.Count)state.RemainingDelaySeconds=saved.Items[state.CurrentIndex].RemainingDelaySeconds ?? state.Queue[state.CurrentIndex].DelaySeconds;
            prepared.Add(state);
        }
        foreach(var state in prepared)SaveGameQueueSnapshot(state);
        RefreshSavedGameQueues();StatusText=$"Imported {prepared.Count} saved game queues. Load separately or add to the end of Build queue; nothing was started.";
    }
}
