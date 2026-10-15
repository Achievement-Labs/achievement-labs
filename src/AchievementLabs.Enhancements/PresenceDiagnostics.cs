using System.Runtime.CompilerServices;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace AchievementLabs.MultiSelect;
public static class PresenceDiagnostics
{
    sealed class State {public string Message="No heartbeat result yet.";public int Code;}
    static readonly ConditionalWeakTable<object,State> States=new();
    public static async Task<(int,string)> Observe(Task<(int,string)> source,object client,string title)
    {
        var result=await source.ConfigureAwait(false);
        var s=States.GetOrCreateValue(client);s.Code=result.Item1;
        s.Message=$"Title {title}: HTTP {result.Item1}. "+Explain(result.Item1);
        return result;
    }
    public static string Explain(int code)=>code switch
    {
        401=>"Xbox rejected presence authorization. Reconnect your Xbox account. An event token is separate and cannot repair this login.",
        403=>"Xbox refused this presence request. No automatic retries or authentication bypass were attempted.",
        429=>"Presence requests are being rate limited.",
        >=200 and <300=>"Heartbeat accepted. This does not guarantee that Xbox will increase recorded playtime.",
        _=>"Heartbeat was not accepted."
    };
    public static async Task<int> Check(HttpClient http,string auth,string xuid,CancellationToken ct)
    {
        if(!ulong.TryParse(xuid,out _) || string.IsNullOrWhiteSpace(auth))throw new InvalidOperationException("Connect Xbox first.");
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://profile.xboxlive.com/users/xuid({xuid})/profile/settings?settings=Gamertag");
        request.Headers.TryAddWithoutValidation("Authorization",auth);
        request.Headers.Add("x-xbl-contract-version","2");
        using var response=await http.SendAsync(request,ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static object? Field(object o,string n)=>o.GetType().GetField(n,Flags)?.GetValue(o);
    static object? Prop(object? o,string n)=>o?.GetType().GetProperty(n,Flags)?.GetValue(o);
    public static void Attach(Window owner,object model)
    {
        var start=owner.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b=>Equals(b.Content,"Start spoofing"));
        if(start?.Parent is not Panel panel)return;
        var box=new StackPanel{Name="PresenceDiagnostics",Spacing=6,Margin=new Thickness(0,8)};
        var status=new TextBlock {Text="No heartbeat result yet.",TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var check=new Button{Name="CheckPresenceLogin",Content="Check Xbox login (read-only)"};
        var detail=new TextBlock {TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=12};
        box.Children.Add(status);box.Children.Add(check);box.Children.Add(detail);panel.Children.Insert(panel.Children.IndexOf(start),box);
        var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(20)};
        var cts=new CancellationTokenSource();bool busy=false,closed=false;DateTimeOffset next=DateTimeOffset.MinValue;object? shown=null;
        void Update()
        {
            var client=Field(model,"client");
            if(!ReferenceEquals(shown,client)){shown=client;detail.Text="";}
            status.Text=client!=null && States.TryGetValue(client,out var state)?state.Message:"No heartbeat result yet.";
            check.IsEnabled=!busy && client!=null && Prop(model,"CanQuery") is true && DateTimeOffset.UtcNow>=next;
        }
        check.Click+=async (_,_)=>
        {
            var client=Field(model,"client");var session=Field(model,"session");if(client==null || session==null || busy)return;
            busy=true;next=DateTimeOffset.UtcNow.AddSeconds(15);Update();detail.Text="Checking the general Xbox login without sending presence or achievement events…";
            try
            {
                int code=await Check(http,Field(client,"_xauth") as string??"",Prop(session,"Xuid")?.ToString()??"",cts.Token);
                if(closed || !ReferenceEquals(client,Field(model,"client")))return;
                detail.Text=code is >=200 and <300
                    ? "General Xbox login accepted. If heartbeat still returns 401, this is a presence-specific authorization problem, not a missing event token."
                    : code==401?"General Xbox login also returned 401. Reconnect Xbox to obtain fresh authorization.":$"General Xbox login check returned HTTP {code}.";
            }
            catch(Exception) {if(!closed && ReferenceEquals(client,Field(model,"client")))detail.Text="Login check unavailable. No presence or achievement events were sent.";}
            finally{busy=false;if(!closed)Update();}
        };
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};timer.Tick+=(_,_)=>Update();timer.Start();Update();
        owner.Closed+=(_,_)=>{closed=true;cts.Cancel();timer.Stop();http.Dispose();cts.Dispose();};
    }
}
