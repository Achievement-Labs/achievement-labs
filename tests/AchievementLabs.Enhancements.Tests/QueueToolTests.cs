using AchievementLabs.MultiSelect;
using System.Collections.Generic;
public static class QueueToolTests
{
 public sealed class Entry{public double DelaySeconds{get;set;} public bool Completed{get;set;}}
 public sealed class State{public List<Entry> Queue{get;set;}=[];public int CurrentIndex{get;set;}public double RemainingDelaySeconds{get;set;}public bool IsRunning{get;set;}public int Saves;public bool Fail;public void Save(){if(Fail)throw new Exception("synthetic save failure");Saves++;}}
 public sealed class Model{public State? _state;public bool IsRunning{get;set;}public string StatusText{get;set;}="";public string StartStopButtonText{get;set;}="";public bool HasExistingState{get;set;}=true;public int Displays;public void PopulateQueueDisplay(){Displays++;}public void UpdateProgressText(){Displays++;}}
 public sealed class Game{public string TitleId{get;set;}="572802557";}
 public sealed class Queue{public string TitleId{get;set;}="";}
 public sealed class Desktop{public Game SelectedGame{get;set;}=new();public Queue XboxQueue{get;set;}=new();public bool QueueActive{get;set;}public bool PresenceRunning{get;set;}public string PresenceTitleId{get;set;}="";public int Opens;public void OpenQueues(){Opens++;}}
 static void Assert(bool x,string message){if(!x)throw new Exception(message);}
 public static void Run(){
  var desktop=new Desktop();QueueTools.OpenForGame(desktop);Assert(desktop.Opens==1&&desktop.XboxQueue.TitleId=="572802557"&&desktop.PresenceTitleId=="572802557","Game ID transferred without starting");desktop.QueueActive=true;QueueTools.OpenForGame(desktop);Assert(desktop.Opens==1,"Running queue protected");
  var done=new Entry{Completed=true};var pending=new Entry{DelaySeconds=90};
  var cleanup=new State{Queue=[done,pending],CurrentIndex=1,RemainingDelaySeconds=37};var cleaner=new Model{_state=cleanup};
  cleaner.IsRunning=true;Assert(QueueTools.RemoveCompleted(cleaner,cleanup)==0,"Cleanup protected during run");cleaner.IsRunning=false;
  Assert(QueueTools.RemoveCompleted(cleaner,cleanup)==1 && cleanup.CurrentIndex==0 && cleanup.RemainingDelaySeconds==37 && cleanup.Queue[0]==pending,"Cleanup preserves next item and countdown");
  Assert(QueueTools.RemoveCompleted(cleaner,cleanup)==0,"Cleanup no-op with no completed entries");
  var a=new Entry{Completed=true,DelaySeconds=10};var b=new Entry{Completed=true,DelaySeconds=15};var c=new Entry{DelaySeconds=20};var e=new Entry{DelaySeconds=25};
  var state=new State{Queue=[a,b,c,e],CurrentIndex=2,RemainingDelaySeconds=73};var model=new Model{_state=state};
  Assert(QueueTools.Remove(model,state,[a])==1&&state.CurrentIndex==1&&state.RemainingDelaySeconds==73&&state.Queue[1]==c&&b.Completed,"Removing earlier entry preserves active entry, timer and completed progress");
  Assert(QueueTools.Remove(model,state,[c])==1&&state.CurrentIndex==1&&state.RemainingDelaySeconds==25&&state.Queue[1]==e,"Removing active entry switches to next entry delay");
  model.IsRunning=true;Assert(QueueTools.Remove(model,state,[e])==0&&state.Queue.Count==2,"Removal rejected during run");model.IsRunning=false;
  Assert(QueueTools.Remove(model,new State(),[e])==0,"Stale queue rejected");
  state.Fail=true;try{QueueTools.Remove(model,state,[e]);throw new Exception("Expected failure");}catch(System.Reflection.TargetInvocationException){}
  Assert(state.Queue.Count==2&&state.CurrentIndex==1&&state.RemainingDelaySeconds==25,"Failed save rolls back queue and timer");state.Fail=false;
  Assert(QueueTools.Remove(model,state,[b,e])==2&&state.Queue.Count==0&&state.CurrentIndex==0&&state.RemainingDelaySeconds==0,"Empty queue remains consistent");
  state.IsRunning=true;model.IsRunning=true;QueueTools.LoadedOnly(model).GetAwaiter().GetResult();Assert(!state.IsRunning&&!model.IsRunning&&model.StartStopButtonText=="Start"&&!model.HasExistingState,"Loaded queue stays stopped and ready for explicit start");
  Console.WriteLine("PASS: Title ID navigation, stopped queue review, stable removal indices/timers, persistence rollback, empty queue and stale/running guards. No unlock or heartbeat sent.");
 }
}
