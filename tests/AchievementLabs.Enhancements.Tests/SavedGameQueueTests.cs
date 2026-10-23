using System.Reflection;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.Models;

public static class SavedGameQueueTests
{
    static void Assert(bool ok,string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),"labs-saved-queues-"+Guid.NewGuid());
        try
        {
            var account = new NativeAccountContext { XUIDOnly="42", XAUTH="synthetic-offline" };
            AutoUnlockerViewModel Model(NativeAccountContext owner) => new(new NativeNotices(_ => {}),owner) { StatePath=Path.Combine(root,"active.json"), SavedGameQueuesRoot=Path.Combine(root,"games") };
            var model = Model(account);
            var field = typeof(AutoUnlockerViewModel).GetField("_state",BindingFlags.Instance|BindingFlags.NonPublic)!;
            AutoUnlockState State(string title) => new() { OwnerXuid="42",TitleId=title,GameName="Game "+title,RemainingDelaySeconds=23,Queue=[new() { AchievementId="1",AchievementName="Example",DelaySeconds=120 }] };
            foreach (var title in new[]{"7","8"}) { field.SetValue(model,State(title)); model.SaveCurrentGameQueueCommand.Execute(null); }
            Assert(model.SavedGameQueues.Count==2,"Both title queues are retained independently");
            var restarted = Model(account); restarted.OnNavigatedTo();
            Assert(restarted.SavedGameQueues.Count==2 && !restarted.IsRunning,"Saved games reload without starting");
            restarted.SelectedSavedGameQueue = restarted.SavedGameQueues.Single(x => x.TitleId=="7");
            restarted.LoadSelectedGameQueueCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert(restarted.PreparedState?.TitleId=="7" && restarted.PreparedState.RemainingDelaySeconds==23 && !restarted.IsRunning,"Loading preserves delay and requires explicit start");
            AutoUnlockState? appended = null;
            restarted.AppendSavedGameAsync = state => { appended=state; return Task.CompletedTask; };
            restarted.AppendSelectedGameQueueCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert(appended?.TitleId=="7" && restarted.PreparedState?.TitleId=="7","Append hands the selected checkpoint to build queue without replacing active state");
            restarted.SelectedSavedGameQueue = restarted.SavedGameQueues.Single(x => x.TitleId=="8");
            restarted.DeleteSavedGameQueueAsync("8","43").GetAwaiter().GetResult();
            Assert(restarted.SavedGameQueues.Count==2,"A confirmation for a different account cannot delete queues");
            restarted.DeleteSavedGameQueueAsync("8","42").GetAwaiter().GetResult();
            Assert(restarted.PreparedState?.TitleId=="7" && File.Exists(restarted.StatePath),"Deleting another game preserves the loaded checkpoint");
            var afterDelete = Model(account); afterDelete.OnNavigatedTo();
            Assert(afterDelete.SavedGameQueues.Count==1 && afterDelete.SavedGameQueues[0].TitleId=="7","Deleted queue stays absent after restart");
            restarted.DeleteSavedGameQueueAsync("7","42").GetAwaiter().GetResult();
            Assert(restarted.PreparedState==null && !File.Exists(restarted.StatePath),"Deleting the loaded game clears its legacy checkpoint");
            var empty = Model(account); empty.OnNavigatedTo();
            Assert(empty.SavedGameQueues.Count==0,"Legacy migration does not resurrect deleted games");
            var settingsPath = Path.Combine(root,"options.json");
            var store = new AchievementLabs.Core.AutoUnlockPreferencesStore(settingsPath);
            Assert(store.Load()==null,"Old installations without preference files use defaults");
            var options = new AchievementLabs.Core.AutoUnlockPreferences { RefreshTokenOnFailure=true,NotifyOnFailure=false,StopOnFailure=true,
                StopOnSpoofFailure=true,NotifyOnCompletion=false,SpeedMultiplier=2.5,BuildSpeedMultiplier=3,BuildBufferSeconds=120,ReferenceGamertag="Offline reference" };
            store.Save(options);
            Assert(new AchievementLabs.Core.AutoUnlockPreferencesStore(settingsPath).Load()==options,"All Auto Unlock preferences survive restart independently of queue deletion");
            try { store.Save(options with { SpeedMultiplier=double.NaN }); throw new Exception("Invalid speed was saved"); }
            catch (InvalidDataException) { }
            Assert(store.Load()==options,"Invalid settings preserve the previous file");
            var other = Model(new NativeAccountContext { XUIDOnly="43",XAUTH="synthetic-offline" });other.OnNavigatedTo();
            Assert(other.SavedGameQueues.Count==0,"Saved queues are partitioned by Xbox account");
            Console.WriteLine("PASS: per-game queue persistence, restart, explicit resume, append handoff, remaining delay and account partitioning. No network.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
