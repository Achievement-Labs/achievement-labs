using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace AchievementLabs.MultiSelect;
public static class QueueTools
{
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static object? P(object? o,string n)=>o?.GetType().GetProperty(n,Flags)?.GetValue(o);
 static object? F(object? o,string n)=>o?.GetType().GetField(n,Flags)?.GetValue(o);
 static void Set(object o,string n,object value)=>o.GetType().GetProperty(n,Flags)?.SetValue(o,value);
 static object? Call(object o,string n)=>o.GetType().GetMethod(n,Flags,Type.EmptyTypes)?.Invoke(o,null);
 public static Task LoadedOnly(object model)
 {
  var state=F(model,"_state");if(state!=null)Set(state,"IsRunning",false);
  Set(model,"IsRunning",false);Set(model,"StartStopButtonText","Start");
  Set(model,"StatusText","Saved queue loaded. Review or remove achievements, then click Start to resume.");
  Set(model,"HasExistingState",false);
  return Task.CompletedTask;
 }
 public static void OpenForGame(object model)
 {
  if(P(model,"QueueActive") is true)return;
  var game=P(model,"SelectedGame");string id=P(game,"TitleId")?.ToString()??P(game,"Id")?.ToString()??"";
  if(!ulong.TryParse(id,out _))return;
  var queue=P(model,"XboxQueue");if(P(queue,"IsRunning") is true)return;
  Call(model,"OpenQueues");
  if(queue!=null)
  {
   var old=F(queue,"_state");
   if(old!=null && P(old,"TitleId")?.ToString()!=id)
   {
    queue.GetType().GetField("_state",Flags)?.SetValue(queue,null);
    var items=queue.GetType().GetProperty("QueueItems",Flags);
    if(items!=null)items.SetValue(queue,Activator.CreateInstance(items.PropertyType));
    Call(queue,"UpdateProgressText");
    Set(queue,"StatusText","Game selected. Build a queue, or load a saved queue for review before starting.");
   }
   Set(queue,"TitleId",id);
  }
  Set(model,"PresenceTitleId",id);
 }
 public static int Remove(object model,object state,IEnumerable<object> selected)
 {
  if(P(model,"IsRunning") is true || !ReferenceEquals(state,F(model,"_state")))return 0;
  if(P(state,"Queue") is not IList list)return 0;
  var remove=selected.ToHashSet(ReferenceEqualityComparer.Instance);
  var original=list.Cast<object>().ToArray();int index=(int)(P(state,"CurrentIndex")??0);
  double delay=Convert.ToDouble(P(state,"RemainingDelaySeconds")??0d);
  int next=original.Take(index).Count(x=>!remove.Contains(x));
  var kept=original.Where(x=>!remove.Contains(x)).ToArray();int count=original.Length-kept.Length;if(count==0)return 0;
  bool keepCurrent=index<original.Length && !remove.Contains(original[index]);
  list.Clear();foreach(var item in kept)list.Add(item);
  Set(state,"CurrentIndex",Math.Min(next,kept.Length));
  Set(state,"RemainingDelaySeconds",keepCurrent?delay:next<kept.Length?Convert.ToDouble(P(kept[next],"DelaySeconds")??0d):0d);
  Set(state,"IsRunning",false);
  try{if(model.GetType().GetMethod("SaveState",Flags,Type.EmptyTypes)!=null)Call(model,"SaveState");else Call(state,"Save");}
  catch{list.Clear();foreach(var item in original)list.Add(item);Set(state,"CurrentIndex",index);Set(state,"RemainingDelaySeconds",delay);throw;}
  Call(model,"PopulateQueueDisplay");Call(model,"UpdateProgressText");
  Set(model,"StatusText",$"Removed {count} achievement(s). Queue saved. Click Start when ready.");
  return count;
 }
 public static int RemoveCompleted(object model,object state)
 {
  if(P(state,"Queue") is not IList list)return 0;
  // Reuse removal's active-run guard, index adjustment, delay preservation and rollback.
  return Remove(model,state,list.Cast<object>().Where(e=>P(e,"Completed") is true).ToArray());
 }
 public static void Attach(Window owner,object model)
 {
  var buttons=owner.GetLogicalDescendants().OfType<Button>().ToArray();
  var open=buttons.FirstOrDefault(b=>b.Name=="OpenGameAutoSpoofer");
  if(open!=null)
  {
   ToolTip.SetTip(open,"Open the Xbox Auto Unlock page with this game's Title ID filled in.");
   open.Click+=(_,_)=>OpenForGame(model);
   var spoof=buttons.FirstOrDefault(b=>b.Name=="OpenGameTitleSpoofer");
   void Update(){if(spoof!=null)spoof.IsEnabled=P(model,"CanOpenGameSpoofer") is true;open.IsEnabled=P(model,"SelectedGame")!=null && P(model,"QueueActive") is not true && P(model,"CanQuery") is true;}
   var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};timer.Tick+=(_,_)=>Update();timer.Start();Update();owner.Closed+=(_,_)=>timer.Stop();
  }
  var build=buttons.FirstOrDefault(b=>Equals(b.Content,"Build queue"));
  if(build?.Parent is not Panel panel)return;
  var remove=new Button{Name="RemoveQueueAchievements",Content="Remove achievements…",Margin=new Thickness(8,0,0,0)};
  var completed=new Button{Name="RemoveCompletedQueueAchievements",Content="Remove completed",Margin=new Thickness(8,0,0,0)};
  ToolTip.SetTip(completed,"Remove completed entries from the saved queue. Pause the queue first.");
  panel.Children.Add(remove);panel.Children.Add(completed);bool editing=false;
  object? Queue()=>P(model,"XboxQueue");
  void Refresh(){var q=Queue();completed.IsEnabled=!editing && P(q,"IsRunning") is not true && F(q,"_state") is object state && P(state,"Queue") is IList entries && entries.Cast<object>().Any(e=>P(e,"Completed") is true);remove.IsEnabled=!editing && P(q,"IsRunning") is not true && F(q,"_state") is object s && P(s,"Queue") is IList l && l.Count>0;}
  var tick=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};tick.Tick+=(_,_)=>Refresh();tick.Start();Refresh();owner.Closed+=(_,_)=>tick.Stop();
  completed.Click+=(_,_)=>
  {
   var q=Queue();var state=F(q,"_state");if(q==null || state==null)return;
   try{int count=RemoveCompleted(q,state);Set(q,"StatusText",$"Removed {count} completed achievement(s). Remaining delays preserved.");}
   catch{Set(q,"StatusText","The queue could not be saved. Original entries and delays were restored.");}
   Refresh();
  };
  remove.Click+=async (_,_)=>
  {
   var q=Queue();var state=F(q,"_state");if(q==null || state==null || P(q,"IsRunning") is true || P(state,"Queue") is not IList list)return;
   editing=true;Refresh();var snapshot=list.Cast<object>().ToArray();
   var dialog=new Window{Title="Remove queue achievements",Width=680,Height=560,WindowStartupLocation=WindowStartupLocation.CenterOwner};
   var root=new DockPanel{Margin=new Thickness(18)};var bottom=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Margin=new Thickness(0,12,0,0)};
   var apply=new Button{Content="Remove selected"};var cancel=new Button{Content="Cancel"};var message=new TextBlock{Text="Select achievements to remove. Saved progress and remaining entries are kept.",TextWrapping=Avalonia.Media.TextWrapping.Wrap};
   var checks=snapshot.Select(x=>new CheckBox{Content=$"{P(x,"AchievementName")} ({P(x,"AchievementId")})",Tag=x,Margin=new Thickness(0,5,0,5)}).ToArray();
   var choices=new StackPanel();foreach(var c in checks)choices.Children.Add(c);
   bottom.Children.Add(apply);bottom.Children.Add(cancel);DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);DockPanel.SetDock(message,Dock.Top);root.Children.Add(message);root.Children.Add(new ScrollViewer{Content=choices});dialog.Content=root;
   cancel.Click+=(_,_)=>dialog.Close();apply.Click+=(_,_)=>
   {
    try{if(P(q,"IsRunning") is true || !ReferenceEquals(state,F(q,"_state")) || !list.Cast<object>().SequenceEqual(snapshot,ReferenceEqualityComparer.Instance)){message.Text="The queue changed. Close this window and reopen it before removing entries.";return;}
     Remove(q,state,checks.Where(c=>c.IsChecked==true).Select(c=>c.Tag!));dialog.Close();}
    catch{message.Text="The edited queue could not be saved. Your original queue was restored.";}
   };
   try{await dialog.ShowDialog(owner);}finally{editing=false;Refresh();}
  };
 }
}
