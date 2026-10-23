using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AchievementLabs.Desktop.Presentation;

public sealed class PlaytimeRefreshPolicy
{
    bool wasOpen,pending;string title="";DateTimeOffset sent=DateTimeOffset.MinValue;
    public void RecordManual(DateTimeOffset now){sent=now;pending=false;}
    public bool ShouldRefresh(bool open,string id,bool ready,bool queueRoute,DateTimeOffset now)
    {
        if(!open){wasOpen=false;pending=false;return false;}
        if(!wasOpen || title!=id){pending=true;title=id;}wasOpen=true;
        if(queueRoute && now-sent>=TimeSpan.FromMinutes(5))pending=true;
        if(!uint.TryParse(id,out var number)||number==0||!pending||!ready)return false;
        pending=false;sent=now;return true;
    }
}
public static class PlaytimeAutoRefresh
{
    public static void Attach(Window owner,object model,Button refresh,bool preview)
    {
        if(preview || (AppContext.TryGetSwitch("AchievementLabs.OfflineChecks",out var offline) && offline))return;
        object? Get(string name)=>model.GetType().GetProperty(name)?.GetValue(model);
        var policy=new PlaytimeRefreshPolicy();
        void Update(){bool queue=Get("IsQueues") is true;bool shown=Get("IsSpoofer") is true||queue;
            if(policy.ShouldRefresh(shown,Get("PresenceTitleId")?.ToString()??"",refresh.IsEnabled,queue,DateTimeOffset.UtcNow))
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        refresh.Click+=(_,_)=>policy.RecordManual(DateTimeOffset.UtcNow);
        var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};timer.Tick+=(_,_)=>Update();timer.Start();
        PropertyChangedEventHandler changed=(_,e)=>{if(e.PropertyName is "IsSpoofer" or "IsQueues" or "PresenceTitleId" or "CanQuery")Update();};
        if(model is INotifyPropertyChanged npc)npc.PropertyChanged+=changed;
        owner.Closed+=(_,_)=>{timer.Stop();if(model is INotifyPropertyChanged npc)npc.PropertyChanged-=changed;};
    }
}
