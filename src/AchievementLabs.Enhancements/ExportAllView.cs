using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AchievementLabs.MultiSelect;
public static class ExportAllView
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static object? Prop(object? o,string name)=>o?.GetType().GetProperty(name,Flags)?.GetValue(o);
    static object? Field(object? o,string name)=>o?.GetType().GetField(name,Flags)?.GetValue(o);
    static readonly string Preference=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AchievementLabs","export-folder.txt");
    static void Busy(object model,bool value)=>model.GetType().GetMethod("Busy",Flags)!.Invoke(model,new object[]{value});
    public static void Attach(Window owner,object model)
    {
        bool open=false;
        foreach(var anchor in owner.GetLogicalDescendants().OfType<Button>().Where(b=>b.Content is string s && s is "Export CSV" or "Export games CSV").ToArray())
        {
            if(anchor.Parent is not Panel panel)continue;
            var button=new Button {Name="ExportAllAchievements",Content="Export all achievements…",Margin=new Thickness(8,0,0,0)};
            ToolTip.SetTip(button,"Export every game on the connected Xbox account; resume by skipping titles already saved.");
            panel.Children.Insert(panel.Children.IndexOf(anchor)+1,button);
            void Update()=>button.IsEnabled=!open && Prop(model,"CanQuery") is true;
            PropertyChangedEventHandler handler=(_,_)=>Update();
            if(model is INotifyPropertyChanged n)n.PropertyChanged+=handler;
            owner.Closed+=(_,_)=>{if(model is INotifyPropertyChanged n)n.PropertyChanged-=handler;};
            button.Click+=async (_,_)=>
            {
                if(open || Prop(model,"CanQuery") is not true)return;
                open=true;Update();
                try{await Show(owner,model);}
                catch(Exception e){model.GetType().GetProperty("ActionSummary",Flags)?.SetValue(model,"Export could not start: "+(e.InnerException?.Message??e.Message));}
                finally{open=false;Update();}
            };
            Update();
        }
    }
    public static async Task Show(Window owner,object model)
    {
        var dialog=new Window {Title="Export all Xbox achievements",Width=740,Height=510,MinWidth=550,MinHeight=460,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=new SolidColorBrush(Color.Parse("#111817")),RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark};
        var root=new StackPanel {Spacing=13,Margin=new Thickness(24)};
        root.Children.Add(new TextBlock {Text="Export all achievements",FontSize=24,FontWeight=FontWeight.SemiBold});
        root.Children.Add(new TextBlock {Text="Scan the connected Xbox account’s full game history, including locked achievements. Game and achievement filters do not limit this export.",TextWrapping=TextWrapping.Wrap});
        string initial=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"AchievementLabs Exports");
        try{if(File.Exists(Preference))initial=File.ReadAllText(Preference);}catch(IOException){}catch(UnauthorizedAccessException){}
        var folder=new TextBox {Text=initial,PlaceholderText="Choose an export folder",HorizontalAlignment=HorizontalAlignment.Stretch};
        var choose=new Button {Content="Choose folder…"};
        var grid=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto")};grid.Children.Add(folder);Grid.SetColumn(choose,1);choose.Margin=new Thickness(8,0,0,0);grid.Children.Add(choose);root.Children.Add(grid);
        var refresh=new CheckBox {Content="Refresh already saved titles too",IsChecked=false};root.Children.Add(refresh);
        root.Children.Add(new TextBlock {Text="By default, completed titles are skipped and failed titles are retried. Use the same folder to resume. Refresh saved titles to update unlock status or discover new DLC.",FontSize=12,Opacity=.75,TextWrapping=TextWrapping.Wrap});
        var bar=new ProgressBar {Name="AllAchievementsProgress",Minimum=0,Maximum=1,Height=12,Foreground=new SolidColorBrush(Color.Parse("#58DBA1"))};root.Children.Add(bar);
        var counts=new TextBlock {Text="Ready",FontWeight=FontWeight.SemiBold};root.Children.Add(counts);
        var status=new TextBlock {Text="Each row includes Xbox Title ID and achievement ID. Progress is saved after every complete title.",TextWrapping=TextWrapping.Wrap};root.Children.Add(status);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right};
        var start=new Button {Content="Start / resume export"};var pause=new Button {Content="Pause",IsEnabled=false};var close=new Button {Content="Close"};actions.Children.Add(start);actions.Children.Add(pause);actions.Children.Add(close);root.Children.Add(actions);
        dialog.Content=new ScrollViewer {Content=root};CancellationTokenSource? cancel=null;bool running=false;
        void SetRunning(bool value){running=value;start.IsEnabled=choose.IsEnabled=folder.IsEnabled=refresh.IsEnabled=!value;pause.IsEnabled=value;close.Content=value?"Pause and close":"Close";}
        bool closeAfter=false;
        void RequestPause(){pause.IsEnabled=false;status.Text="Pausing and writing the combined CSV…";cancel?.Cancel();}
        dialog.Closing+=(_,e)=>{if(running){e.Cancel=true;closeAfter=true;RequestPause();}};
        EventHandler onOwnerClosed=(_,_)=>cancel?.Cancel();owner.Closed+=onOwnerClosed;
        choose.Click+=async (_,_)=>
        {
            try{var picked=await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions{Title="Choose the export folder",AllowMultiple=false});if(picked.Count>0 && picked[0].TryGetLocalPath() is string p)folder.Text=p;}
            catch(Exception e){status.Text="Could not choose a folder: "+e.Message;}
        };
        pause.Click+=(_,_)=>RequestPause();close.Click+=(_,_)=>dialog.Close();
        start.Click+=async (_,_)=>
        {
            if(running)return;
            if(Prop(model,"CanQuery") is not true){status.Text="Connect your Xbox account and wait for the current operation to finish.";return;}
            object? session=Field(model,"session"),client=Field(model,"client");
            string xuid=Prop(session,"Xuid")?.ToString()??"",auth=Field(client,"_xauth") as string??"";
            if(xuid.Length==0 || auth.Length==0){status.Text="No connected Xbox session. Reconnect your account first.";return;}
            string destination=folder.Text?.Trim()??"";
            if(!Path.IsPathFullyQualified(destination)){status.Text="Choose a full folder path first.";return;}
            bool refreshAll=refresh.IsChecked==true;SetRunning(true);cancel=new CancellationTokenSource();bool busySet=false;
            try
            {
                Directory.CreateDirectory(destination);
                try{Directory.CreateDirectory(Path.GetDirectoryName(Preference)!);File.WriteAllText(Preference,destination);}catch(IOException){}catch(UnauthorizedAccessException){}
                Busy(model,true);busySet=true;
                using var http=new HttpClient(new HttpClientHandler {AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate}){Timeout=TimeSpan.FromSeconds(60)};
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",auth);http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language","en-GB");http.DefaultRequestHeaders.TryAddWithoutValidation("Accept","application/json");
                var engine=new AchievementExport(http,xuid,message=>Dispatcher.UIThread.Post(()=>status.Text=message));
                var result=await Task.Run(()=>engine.Run(destination,refreshAll,p=>Dispatcher.UIThread.Post(()=>
                {
                    bar.IsIndeterminate=p.Total==0;bar.Maximum=Math.Max(1,p.Total);bar.Value=p.Done;
                    counts.Text=$"{p.Done:N0} / {p.Total:N0} titles · {p.Saved:N0} saved · {p.Skipped:N0} skipped · {p.Failed:N0} failed";status.Text=p.Message;
                }),cancel.Token));
                status.Text=$"{(result.Paused?"Paused":"Finished")}. CSV saved to:\n{Path.Combine(result.Folder,"all-achievements.csv")}"+(result.Failed>0?"\nSee debug/scan-errors.csv for titles to retry.":"");
            }
            catch(Exception e){status.Text="Export stopped: "+e.Message+"\nCompleted titles remain saved. Resume using the same folder.";}
            finally
            {
                if(busySet)Busy(model,false);bar.IsIndeterminate=false;SetRunning(false);cancel.Dispose();cancel=null;if(closeAfter)dialog.Close();
            }
        };
        try{await dialog.ShowDialog(owner);}finally{owner.Closed-=onOwnerClosed;cancel?.Cancel();}
    }
}
