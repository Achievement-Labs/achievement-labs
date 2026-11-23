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
            transfer.Games.Add(new TransferGame { TitleId=state.TitleId,Name=state.GameName,
                Windows8StartDate=state.IsWindows8 ? state.Windows8StartDate : null,
                Windows8StartTime=state.IsWindows8 ? state.Windows8StartTime : null,
                Windows8BaseTimestampUtc=state.IsWindows8 ? state.Windows8BaseTimestampUtc : null,
                Items=state.Queue.Select((item,index)=>new TransferAchievement { AchievementId=item.AchievementId,Name=item.AchievementName,DelaySeconds=item.DelaySeconds,RemainingDelaySeconds=index==state.CurrentIndex ? state.RemainingDelaySeconds : null,Windows8TimestampUtc=state.IsWindows8 ? item.Windows8TimestampUtc : null,Status=item.Completed ? "Accepted" : index<state.CurrentIndex ? "Needs review" : "Waiting" }).ToList() });
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
            var windows8=DesktopModel.IsKnownWindows8Title(saved.TitleId);
            if (!windows8 && (saved.Windows8BaseTimestampUtc != null || saved.Items.Any(i=>i.Windows8TimestampUtc != null)))
                throw new InvalidDataException("Windows 8 timestamps cannot be imported for a different title type.");
            var state=new AutoUnlockState { OwnerXuid=owner,TitleId=saved.TitleId,GameName=saved.Name,StopOnFailure=true,NotifyOnFailure=true,IsWindows8=windows8,IsEventBased=!windows8&&UsesEvents(current.achievements),ServiceConfigId=current.achievements.FirstOrDefault()?.serviceConfigId ?? "",
                Windows8StartDate=windows8 ? saved.Windows8StartDate ?? "" : "",Windows8StartTime=windows8 ? saved.Windows8StartTime ?? "" : "",Windows8BaseTimestampUtc=windows8 ? saved.Windows8BaseTimestampUtc : null };
            foreach (var item in saved.Items)
            {
                var row=current.achievements.FirstOrDefault(x=>x.id==item.AchievementId && string.Equals(x.name,item.Name,StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Imported achievement does not match this edition: "+item.Name);
                var unlocked=string.Equals(row.progressState,"Achieved",StringComparison.OrdinalIgnoreCase);
                // Unconfirmed transferred submissions are not silently replayed.
                if (!unlocked && item.Status!="Waiting") throw new InvalidDataException("Refresh or reset previously submitted items before importing into PC: "+item.Name);
                state.Queue.Add(new AutoUnlockQueueEntry { AchievementId=row.id,AchievementName=row.name,AchievementDescription=QueueDescription(row),DelaySeconds=item.DelaySeconds,Completed=unlocked,Windows8TimestampUtc=windows8 ? item.Windows8TimestampUtc : null });
            }
            state.CurrentIndex=state.Queue.FindIndex(x=>!x.Completed);if(state.CurrentIndex<0)state.CurrentIndex=state.Queue.Count;
            if(state.CurrentIndex<state.Queue.Count)state.RemainingDelaySeconds=saved.Items[state.CurrentIndex].RemainingDelaySeconds ?? state.Queue[state.CurrentIndex].DelaySeconds;
            ValidateWindows8Schedule(state);
            prepared.Add(state);
        }
        foreach(var state in prepared)SaveGameQueueSnapshot(state);
        RefreshSavedGameQueues();StatusText=$"Imported {prepared.Count} saved game queues. Load separately or add to the end of Build queue; nothing was started.";
    }
}
