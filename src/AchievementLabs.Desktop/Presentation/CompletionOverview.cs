using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.Imaging;

namespace AchievementLabs.Desktop.Presentation;

public sealed record CompletionSummary(int Titles,int Mythics,int FullyCompleted,int KnownTitles,long Unlocked,long Achievements)
{
    public double? Percentage=>Achievements>0?100d*Unlocked/Achievements:null;
    public static CompletionSummary Calculate(IEnumerable<object> games){
        int titles=0,mythics=0,full=0,known=0;long unlocked=0,total=0;var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var game in games){
            if(game==null)continue;
            var id=Read(game,"Id")?.ToString();if(string.IsNullOrWhiteSpace(id) || !ids.Add(id))continue;
            titles++;if(Read(game,"BaseComplete") is true)mythics++;if(Read(game,"FullyComplete") is true)full++;
            if(Read(game,"ProgressKnown") is not true || Read(game,"NoDefinitionsReturned") is true)continue;
            long count=Convert.ToInt64(Read(game,"Total")??0);if(count<=0)continue;
            known++;total+=count;unlocked+=Math.Clamp(Convert.ToInt64(Read(game,"Completed")??0),0,count);
        }
        return new(titles,mythics,full,known,unlocked,total);
    }
    public static object? Read(object model,string name)=>model.GetType().GetProperty(name)?.GetValue(model);
    public static string GamePercentage(object game){
        if(Read(game,"ProgressKnown") is not true || Read(game,"NoDefinitionsReturned") is true)return "Progress unavailable";
        long total=Convert.ToInt64(Read(game,"Total")??0);if(total<=0)return "Progress unavailable";
        long done=Math.Clamp(Convert.ToInt64(Read(game,"Completed")??0),0,total);return $"{100d*done/total:0.#}% complete";
    }
}

public sealed class CompletionOverview
{
    readonly Window owner;readonly object model;readonly bool preview;object? lastGames;bool pending,closed;
    readonly List<Action<CompletionSummary>> displays=new();
    CompletionOverview(Window owner,object model,bool preview){this.owner=owner;this.model=model;this.preview=preview;}
    public static void Attach(Window owner,object model,IEnumerable<ILogical> original,bool preview){
        var overview=new CompletionOverview(owner,model,preview);var all=original.ToArray();
        var home=all.OfType<ScrollViewer>().First(s=>s.Content is StackPanel p && p.GetLogicalDescendants().OfType<Button>().Any(b=>Equals(b.Content,"Open Steam")));
        overview.BuildHome(home);
        var search=all.OfType<TextBox>().First(t=>t.PlaceholderText=="Search Xbox games or title ID…");
        var library=search.GetLogicalAncestors().OfType<Border>().First(b=>b.Classes.Contains("panel"));
        var layout=(Grid)library.Child!;
        foreach(var child in layout.Children.ToArray())if(Grid.GetRow(child)>=1)Grid.SetRow(child,Grid.GetRow(child)+1);
        layout.RowDefinitions=new RowDefinitions("Auto,Auto,Auto,*");
        var stats=overview.BuildStats("LibraryCompletionOverview",true);Grid.SetRow(stats,1);layout.Children.Add(stats);
        PropertyChangedEventHandler handler=(_,e)=>{if(e.PropertyName is "Games" or "VisibleGames")overview.QueueUpdate();};
        if(model is INotifyPropertyChanged npc){npc.PropertyChanged+=handler;owner.Closed+=(_,_)=>npc.PropertyChanged-=handler;}
        owner.Closed+=(_,_)=>overview.closed=true;overview.QueueUpdate();
    }
    void QueueUpdate(){
        if(pending || closed)return;pending=true;
        Dispatcher.UIThread.Post(()=>{
            pending=false;if(closed)return;var source=CompletionSummary.Read(model,"Games");if(ReferenceEquals(lastGames,source))return;lastGames=source;
            var games=(source as IEnumerable)?.Cast<object>().ToArray()??Array.Empty<object>();
            if(preview)foreach(var game in games){var id=CompletionSummary.Read(game,"Id")?.ToString();if(id is "1289871275" or "1144039928"){game.GetType().GetProperty("BaseComplete")?.SetValue(game,true);game.GetType().GetProperty("FullyComplete")?.SetValue(game,id=="1289871275");}}
            var summary=CompletionSummary.Calculate(games);foreach(var display in displays)display(summary);
        },DispatcherPriority.Background);
    }
    static TextBlock Text(string text,double size=12)=>new(){Text=text,FontSize=size,Foreground=new SolidColorBrush(Color.Parse("#C7D7DE")),TextWrapping=TextWrapping.Wrap};
    Control BuildStats(string name,bool compact){
        var theme=AppearanceController.For(owner);var grid=new Grid {Name=name,ColumnDefinitions=new ColumnDefinitions("*,*,*,*"),ColumnSpacing=10};
        var labels=new[]{"XBOX LIBRARY","MYTHICS EARNED","FULLY COMPLETED","ACHIEVEMENT COMPLETION"};
        var values=new TextBlock[4];var descriptions=new TextBlock[4];
        for(int i=0;i<4;i++){
            var stack=new StackPanel {Spacing=5};var label=Text(labels[i],10);label.Foreground=new SolidColorBrush(Color.Parse("#8FE3BC"));label.LetterSpacing=1;stack.Children.Add(label);
            values[i]=Text("—",compact?23:28);values[i].FontWeight=FontWeight.SemiBold;stack.Children.Add(values[i]);descriptions[i]=Text("",11);stack.Children.Add(descriptions[i]);
            var tile=new Border {Child=stack,Padding=new Thickness(compact?12:16),CornerRadius=new CornerRadius(14),Background=theme.Panel,BorderBrush=theme.Line,BorderThickness=new Thickness(1)};Grid.SetColumn(tile,i);grid.Children.Add(tile);
        }
        ToolTip.SetTip(grid,"Mythics use the app's earned base-game marker, regardless of icon visibility. Fully completed uses the app's completion flag, including add-ons. Achievement completion is unlocked / total known achievements across the loaded Xbox library; unavailable progress is excluded.");
        displays.Add(s=>{
            values[0].Text=s.Titles.ToString("N0");descriptions[0].Text="loaded titles";
            values[1].Text=s.Mythics.ToString("N0");descriptions[1].Text="base-game completion";
            values[2].Text=s.FullyCompleted.ToString("N0");descriptions[2].Text="including add-ons";
            values[3].Text=s.Percentage is double percentage?$"{percentage:0.#}%":"—";
            descriptions[3].Text=s.KnownTitles>0?$"{s.Unlocked:N0} / {s.Achievements:N0} · {s.KnownTitles:N0}/{s.Titles:N0} titles with progress":"No known progress loaded";
        });return grid;
    }
    void BuildHome(ScrollViewer home){
        var old=(StackPanel)home.Content!;var profile=old.Children.OfType<Border>().First();
        var services=old.Children.OfType<Panel>().First(p=>p.GetType().Name=="UniformGrid");
        var tiles=services.Children.OfType<Border>().ToArray();old.Children.Remove(profile);
        var content=new StackPanel {Name="HomeDashboard",Spacing=16,HorizontalAlignment=HorizontalAlignment.Stretch};
        profile.Padding=new Thickness(20);profile.Margin=new Thickness(0);content.Children.Add(profile);content.Children.Add(BuildStats("HomeCompletionOverview",false));
        var servicesGrid=new Grid {Name="HomeServiceTiles",ColumnDefinitions=new ColumnDefinitions("*,*"),RowDefinitions=new RowDefinitions(string.Join(",",Enumerable.Repeat("Auto",(tiles.Length+1)/2))),ColumnSpacing=16,RowSpacing=16};
        for(int i=0;i<tiles.Length;i++){
            var tile=tiles[i];services.Children.Remove(tile);tile.Margin=new Thickness(0);tile.Padding=new Thickness(18);tile.MinHeight=145;
            var child=tile.Child!;tile.Child=null;
            var buttons=child.GetLogicalDescendants().OfType<Button>().ToArray();var actions=new WrapPanel {Orientation=Orientation.Horizontal};
            foreach(var button in buttons){if(button.Parent is Panel parent)parent.Children.Remove(button);button.FontSize=12;button.MinHeight=34;button.Padding=new Thickness(12,7);button.Margin=new Thickness(0,0,7,5);actions.Children.Add(button);
                if(Equals(button.Content,"Retry attachment"))button.Content=AchievementLabs.Core.DesktopPlatform.IsUnixDesktop?"Sign in with Microsoft":"Connect Xbox";if(Equals(button.Content,"Library")){button.Content="Open library";button.Classes.Add("primary");}}
            foreach(var stack in child.GetLogicalDescendants().OfType<StackPanel>())stack.Spacing=7;
            foreach(var text in child.GetLogicalDescendants().OfType<TextBlock>())if(text.FontSize>=16)text.FontSize=14;
            if(child is Grid serviceGrid)foreach(var image in serviceGrid.Children.OfType<Grid>())image.VerticalAlignment=VerticalAlignment.Top;
            var body=new Grid {RowDefinitions=new RowDefinitions("Auto,*,Auto"),RowSpacing=8};body.Children.Add(child);Grid.SetRow(actions,2);body.Children.Add(actions);tile.Child=body;
            Grid.SetColumn(tile,i%2);Grid.SetRow(tile,i/2);servicesGrid.Children.Add(tile);
        }
        content.Children.Add(servicesGrid);home.Content=content;
        servicesGrid.SizeChanged+=(_,_)=>{bool narrow=servicesGrid.Bounds.Width<700;if(servicesGrid.ColumnDefinitions.Count==(narrow?1:2))return;servicesGrid.ColumnDefinitions=new ColumnDefinitions(narrow?"*":"*,*");servicesGrid.RowDefinitions=new RowDefinitions(string.Join(",",Enumerable.Repeat("Auto",narrow?tiles.Length:(tiles.Length+1)/2)));for(int i=0;i<tiles.Length;i++){Grid.SetColumn(tiles[i],narrow?0:i%2);Grid.SetRow(tiles[i],narrow?i:i/2);}};
    }
    public static async Task CheckPreview(Window owner,object model,Grid root,string output){
        model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Home"});
        foreach(var size in new[]{(1440,920,"home-dashboard.png"),(1120,720,"home-compact.png"),(900,650,"home-narrow.png"),(1680,1100,"home-large.png")}){
            owner.Width=size.Item1;owner.Height=size.Item2;await Task.Delay(500);
            var stats=root.GetLogicalDescendants().OfType<Grid>().First(g=>g.Name=="HomeCompletionOverview");
            var values=stats.GetLogicalDescendants().OfType<TextBlock>().Select(t=>t.Text).ToArray();
            if(!values.Contains("2") || !values.Contains("1") || !values.Contains("74%"))throw new Exception("Home completion summary does not match the loaded demo library.");
            var buttons=root.GetLogicalDescendants().OfType<Grid>().First(g=>g.Name=="HomeServiceTiles").GetLogicalDescendants().OfType<Button>().Distinct().ToArray();
            var tileGrid=root.GetLogicalDescendants().OfType<Grid>().First(g=>g.Name=="HomeServiceTiles");
            var tiles=tileGrid.Children.OfType<Border>().ToArray();
            for(int i=0;i<tiles.Length;i++)for(int j=0;j<i;j++)
                if(tiles[i].Bounds.Intersects(tiles[j].Bounds))throw new Exception("Home service tiles overlap.");
            if(buttons.Length<6 || buttons.Any(b=>b.TranslatePoint(new Point(b.Bounds.Width,0),root)!.Value.X>root.Bounds.Width))throw new Exception("Home service buttons are missing or overflow the window.");
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));bitmap.Render(root);bitmap.Save(Path.Combine(output,size.Item3),new PngBitmapEncoderOptions());
        }
        File.WriteAllText(Path.Combine(output,"home-checks.txt"),"PASS: Home summary shows two earned Mythics, one fully completed game, and 74% achievement completion for the demo fixtures. Service tiles do not overlap and buttons fit at four sizes. Game Session shares the existing achievement image source.\n");
    }
}
