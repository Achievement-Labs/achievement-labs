using AchievementLabs.Core;
using AchievementLabs.Models;
using AchievementLabs.Desktop.Workflows;
using System.Reflection;
using Newtonsoft.Json;
using Avalonia.Threading;

static class EventVerificationTests
{
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static AchievementsResponse Rows(string state="InProgress",string current="5") => JsonConvert.DeserializeObject<AchievementsResponse>("{\"achievements\":[{\"id\":\"111\",\"name\":\"Gone Spelunking\",\"progressState\":\""+state+"\",\"progression\":{\"requirements\":[{\"id\":\"2751c4e6-0b62-4e26-af55-ffae9ff9acd8\",\"current\":\""+current+"\",\"target\":\"25\"}]}}]}")!;
 static void Loop(AutoUnlockerViewModel vm,CancellationToken ct=default)=>((Task)typeof(AutoUnlockerViewModel).GetMethod("RunAutoUnlockLoop",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(vm,new object[]{ct})!).GetAwaiter().GetResult();
 public static void Run()
 {
  var reads=0;var pauses=0;
  var result=AchievementUnlockVerifier.VerifyAsync(_=>Task.FromResult<AchievementProgressSnapshot?>(new(++reads<3?"InProgress":"Achieved","5/25")),default,(_,_)=>{pauses++;return Task.CompletedTask;}).GetAwaiter().GetResult();
  Check(result.Confirmed&&reads==3&&pauses==2,"Delayed Xbox confirmation is polled without resubmission");
  reads=0;
  result=AchievementUnlockVerifier.VerifyAsync(_=>{reads++;return Task.FromResult<AchievementProgressSnapshot?>(new("InProgress","69/25"));},default,(_,_)=>Task.CompletedTask).GetAwaiter().GetResult();
  Check(!result.Confirmed&&reads==5,"Counter targets alone cannot override InProgress; polling is bounded");
  result=AchievementUnlockVerifier.VerifyAsync(_=>Task.FromResult<AchievementProgressSnapshot?>(null),default,(_,_)=>Task.CompletedTask).GetAwaiter().GetResult();
  Check(!result.Confirmed,"Missing achievement cannot be marked unlocked");
  using(var stop=new CancellationTokenSource()){
   try{AchievementUnlockVerifier.VerifyAsync(_=>Task.FromResult<AchievementProgressSnapshot?>(new("InProgress","1/25")),stop.Token,(_,ct)=>{stop.Cancel();return Task.FromCanceled(ct);}).GetAwaiter().GetResult();throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
  }
  var root=Path.Combine(Path.GetTempPath(),"AchievementLabs-OfflineChecks",Environment.ProcessId.ToString(),"verification");Directory.CreateDirectory(root);
  var owner=new NativeAccountContext{XUIDOnly="0",XAUTH="synthetic-offline"};
  AutoUnlockerViewModel Model(AutoUnlockState state){
   var vm=new AutoUnlockerViewModel(new NativeNotices(_=>{}),owner){StatePath=Path.Combine(root,Guid.NewGuid().ToString("N")+".json"),SavedGameQueuesRoot=Path.Combine(root,"games"),VerificationDelayAsync=(_,_)=>Task.CompletedTask,StopOnFailure=false,ContinueAfterFailure=true,NotifyOnFailure=false};
   typeof(AutoUnlockerViewModel).GetField("_state",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(vm,state);
   typeof(AutoUnlockerViewModel).GetMethod("PopulateQueueDisplay",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(vm,null);
   return vm;
  }
  AutoUnlockState State(int count=1)=>new(){TitleId="74304278",OwnerXuid="0",GameName="Synthetic game",IsEventBased=true,Queue=Enumerable.Range(0,count).Select(i=>new AutoUnlockQueueEntry{AchievementId=(111+i).ToString(),AchievementName="Synthetic achievement",EventRequestIndex=2,EventSubmissionPendingVerification=true}).ToList()};
  var pending=State(2);var halted=Model(pending);var calls=0;halted.ReadQueueAchievementsAsync=(_,_)=>{calls++;return Task.FromResult<AchievementsResponse?>(Rows());};
  Loop(halted);Dispatcher.UIThread.RunJobs();
  Check(calls==5&&pending.CurrentIndex==0&&!pending.Queue[0].Completed&&!pending.Queue[1].Completed&&halted.HadFailure,"Unconfirmed entry pauses before the next even with continue-on-failure enabled");
  Check(pending.Queue[0].EventSubmissionPendingVerification&&pending.Queue[0].EventRequestIndex==2&&halted.QueueItems[0].Status.StartsWith("Unconfirmed"),"Accepted checkpoint is retained with an honest status");
  var saved=JsonConvert.DeserializeObject<AutoUnlockState>(File.ReadAllText(halted.StatePath))!;Check(saved.Queue[0].EventSubmissionPendingVerification&&!saved.Queue[0].Completed,"Unconfirmed status survives restart");
  var resumed=Model(saved);calls=0;resumed.ReadQueueAchievementsAsync=(_,_)=>{calls++;return Task.FromResult<AchievementsResponse?>(Rows());};Loop(resumed);
  Check(calls==5&&saved.CurrentIndex==0&&saved.Queue[0].EventRequestIndex==2,"Resume only verifies an accepted event; no event catalogue or sender is needed");
  var handoffState=State();var handoff=Model(handoffState);var presenceCalls=0;handoff.ExternalPresenceActive=()=>{presenceCalls++;return true;};handoff.ReadQueueAchievementsAsync=(_,_)=>Task.FromResult<AchievementsResponse?>(Rows());File.WriteAllText(handoff.StatePath,JsonConvert.SerializeObject(handoffState));
  var handoffResult=handoff.RunPreparedAsync(default).GetAwaiter().GetResult();Check(handoffResult==BuildQueueItemStatus.Paused&&!handoff.PreparedState!.Queue[0].Completed&&presenceCalls==0,"Prepared multi-game runner reports Paused without requiring event mappings or resending accepted events");
  var routeChanged=State();routeChanged.IsEventBased=false;var checkOnly=Model(routeChanged);calls=0;checkOnly.ReadQueueAchievementsAsync=(_,_)=>{calls++;return Task.FromResult<AchievementsResponse?>(Rows());};Loop(checkOnly);Check(calls==5&&!routeChanged.Queue[0].Completed&&routeChanged.CurrentIndex==0,"A pending accepted checkpoint stays read-only even if later metadata changes its event-routing flag");
  var success=State();var confirmed=Model(success);calls=0;confirmed.ReadQueueAchievementsAsync=(_,_)=>Task.FromResult<AchievementsResponse?>(Rows(++calls==2?"Achieved":"InProgress"));Loop(confirmed);Dispatcher.UIThread.RunJobs();
  Check(success.CurrentIndex==1&&success.Queue[0].Completed&&!success.Queue[0].EventSubmissionPendingVerification&&confirmed.QueueItems[0].Status=="Unlocked","Only Achieved advances the queue and paints green");
  var old=State();old.CurrentIndex=1;old.Queue[0].Completed=true;old.Queue[0].EventSubmissionPendingVerification=false;var legacy=Model(old);legacy.ValidateQueueMetadata(Rows());
  Check(old.CurrentIndex==0&&!old.Queue[0].Completed&&old.Queue[0].EventSubmissionPendingVerification,"Old falsely completed saved entries are reopened without automatic replay");
  var unavailable=State();var readFailure=Model(unavailable);readFailure.ReadQueueAchievementsAsync=(_,_)=>throw new HttpRequestException("synthetic read failure");Loop(readFailure);Check(!unavailable.Queue[0].Completed&&unavailable.CurrentIndex==0&&unavailable.Queue[0].EventSubmissionPendingVerification,"Read failures never inherit submission success");
  var switched=State();var mismatch=Model(switched);mismatch.ReadQueueAchievementsAsync=(_,_)=>{owner.XUIDOnly="1";return Task.FromResult<AchievementsResponse?>(Rows("Achieved"));};Loop(mismatch);Check(!switched.Queue[0].Completed&&switched.CurrentIndex==0,"Account changes cannot confirm the previous owner's achievement");owner.XUIDOnly="0";
  Console.WriteLine("PASS: delayed confirmation, bounded polling, counters versus Achieved, missing rows, cancellation, queue pause despite continue-on-failure, persisted checkpoints, no replay on resume, true completion, legacy saved-state repair and account mismatch. Synthetic data; zero Xbox requests.");
 }
}
