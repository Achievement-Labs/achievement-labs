using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace AchievementLabs.MultiSelect;
public static class PlaytimeView
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static object? Prop(object? o,string name)=>o?.GetType().GetProperty(name,Flags)?.GetValue(o);
    static object? Field(object? o,string name)=>o?.GetType().GetField(name,Flags)?.GetValue(o);
    public static void Attach(Window owner,object model)
    {
        var anchor=owner.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b=>Equals(b.Content,"Start spoofing"));
        if(anchor?.Parent is not Panel panel)return;
        var box=new StackPanel {Name="XboxRecordedPlaytime",Spacing=6,Margin=new Thickness(0,12,0,12)};
        var heading=new TextBlock {Text="XBOX-RECORDED PLAYTIME",FontSize=11,Opacity=.75};
        var value=new TextBlock {Text="—",FontSize=22,Foreground=new SolidColorBrush(Color.Parse("#58DBA1"))};
        var target=new TextBlock {FontSize=12,TextWrapping=TextWrapping.Wrap};
        var info=new TextBlock {Text="Select a title and connect your account.",FontSize=12,Opacity=.75,TextWrapping=TextWrapping.Wrap};
        var refresh=new Button {Name="RefreshXboxPlaytime",Content="Refresh playtime",HorizontalAlignment=HorizontalAlignment.Stretch};
        box.Children.Add(heading);box.Children.Add(value);box.Children.Add(target);box.Children.Add(info);box.Children.Add(refresh);
        panel.Children.Insert(panel.Children.IndexOf(anchor),box);
        // Mirror the existing reader; no extra Xbox requests for the queue tile.
        if (owner.FindControl<TextBlock>("QueueRecordedPlaytime") is { } queueValue)
            queueValue.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Text") { Source = value, StringFormat = "Xbox-recorded playtime: {0}" });
        var http=new HttpClient(new HttpClientHandler {AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate}){Timeout=TimeSpan.FromSeconds(25)};
        CancellationTokenSource? request=null;string key="",activeTitle="";bool wasRunning=false,closed=false;int generation=0;
        DateTimeOffset next=DateTimeOffset.MinValue,lastAttempt=DateTimeOffset.MinValue,cooldown=DateTimeOffset.MinValue;
        decimal? lastValue=null;DateTimeOffset? checkedAt=null;
        (string Xuid,string Title,string Auth) Context()
        {
            string xuid=Prop(Field(model,"session"),"Xuid")?.ToString()??"";
            string auth=Field(Field(model,"client"),"_xauth") as string??"";
            bool running=Prop(model,"PresenceRunning") is true;
            if(running && !wasRunning)activeTitle=Prop(model,"PresenceTitleId")?.ToString()??"";
            if(!running)activeTitle="";wasRunning=running;
            return (xuid,running?activeTitle:Prop(model,"PresenceTitleId")?.ToString()??"",auth);
        }
        async Task Update(bool manual=false)
        {
            if(closed)return;
            var c=Context();string current=c.Xuid+"/"+c.Title;
            if(current!=key)
            {
                key=current;generation++;request?.Cancel();request=null;next=DateTimeOffset.UtcNow.AddSeconds(1);
                lastValue=null;checkedAt=null;value.Text="—";target.Text="Title ID: "+c.Title;info.Text="Waiting to read Xbox playtime…";
            }
            bool valid=c.Auth.Length>0 && ulong.TryParse(c.Xuid,out _) && uint.TryParse(c.Title,out uint id) && id>0;
            bool shown=Prop(model,"IsSpoofer") is true || Prop(model,"PresenceRunning") is true;
            refresh.IsEnabled=valid && request==null && DateTimeOffset.UtcNow>=cooldown && (DateTimeOffset.UtcNow-lastAttempt).TotalSeconds>=10;
            if(!valid){info.Text="Select a valid Xbox Title ID and connect your account.";return;}
            if(request!=null || !shown && !manual || DateTimeOffset.UtcNow<cooldown || (DateTimeOffset.UtcNow-lastAttempt).TotalSeconds<10 || !manual && DateTimeOffset.UtcNow<next)return;
            int stamp=generation;var cancellation=new CancellationTokenSource();request=cancellation;refresh.IsEnabled=false;lastAttempt=DateTimeOffset.UtcNow;next=lastAttempt.AddMinutes(5);
            info.Text="Reading Xbox-recorded playtime…";
            try
            {
                // Use the app's connected account authorization only, never the Xbox app token used by presence.
                decimal? result=await XboxPlaytime.Read(http,c.Xuid,c.Title,cancellation.Token,c.Auth);
                if(closed || stamp!=generation)return;
                lastValue=result;checkedAt=DateTimeOffset.Now;value.Text=result.HasValue?XboxPlaytime.Format(result.Value):"Unavailable";
                info.Text=result.HasValue?$"Checked {checkedAt:HH:mm:ss}. Xbox may report updates with a delay. Auto-refresh: 5 minutes.":"Xbox returned no usable MinutesPlayed value for this title. Auto-refresh: 5 minutes.";
            }
            catch(OperationCanceledException)
            {
                if(!closed && stamp==generation && !cancellation.IsCancellationRequested)info.Text="Playtime request timed out. Try Refresh playtime.";
            }
            catch(Exception e)
            {
                if(closed || stamp!=generation)return;
                if(e is XboxPlaytime.RateLimited limit)cooldown=limit.RetryAt;
                value.Text=lastValue.HasValue?XboxPlaytime.Format(lastValue.Value):"Unavailable";
                info.Text=(checkedAt.HasValue?$"Last known value ({checkedAt:HH:mm:ss}). ":"")+e.Message;
            }
            finally
            {
                if(ReferenceEquals(request,cancellation))request=null;cancellation.Dispose();
                if(!closed && stamp==generation)refresh.IsEnabled=DateTimeOffset.UtcNow>=cooldown && (DateTimeOffset.UtcNow-lastAttempt).TotalSeconds>=10;
            }
        }
        var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};
        timer.Tick+=async (_,_)=>await Update();timer.Start();
        refresh.Click+=async (_,_)=>await Update(true);
        PropertyChangedEventHandler changed=async (_,e)=>{if(e.PropertyName is "PresenceTitleId" or "PresenceRunning" or "IsSpoofer" or "CanQuery" or "")await Update();};
        if(model is INotifyPropertyChanged npc)npc.PropertyChanged+=changed;
        owner.Closed+=(_,_)=>{closed=true;generation++;timer.Stop();request?.Cancel();http.Dispose();if(model is INotifyPropertyChanged npc)npc.PropertyChanged-=changed;};
        _=Update();
    }
}
