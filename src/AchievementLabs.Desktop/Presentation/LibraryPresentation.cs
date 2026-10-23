using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Reflection;
using Avalonia.VisualTree;

namespace AchievementLabs.Desktop.Presentation;

public static class LibraryPresentation
{
    public static void Attach(Window owner,object model,IEnumerable<ILogical> original,bool preview)
    {
        var search=original.OfType<TextBox>().First(t=>t.PlaceholderText=="Search Xbox games or title ID…");
        var panel=search.GetLogicalAncestors().OfType<Border>().First(b=>b.Classes.Contains("panel"));
        var list=panel.GetLogicalDescendants().OfType<ListBox>().First();
        var template=list.ItemTemplate!;
        list.ItemTemplate=new FuncDataTemplate<object>((game,scope)=>{
            // Clearing a virtualized container builds its template with null content.
            if(game==null || !template.Match(game))return null;
            if(preview && Get(game,"Id")?.ToString() is "1289871275" or "1144039928"){
                Set(game,"MythicVisible",true);Set(game,"MythicColour","#86D7AC");
            }
            var row=template.Build(game);if(row==null)return null;row.DataContext=game;
            var grid=row.GetLogicalDescendants().OfType<Grid>().First(g=>g.Children.Any(c=>c.Name=="LibraryCompletionCell"));
            var completion=grid.Children.OfType<Grid>().First(g=>g.Name=="LibraryCompletionCell");
            var count=completion.Children.OfType<TextBlock>().First();
            var icon=completion.Children.First(c=>c.Name=="MythicCompletionIcon");
            var id=grid.Children.OfType<TextBlock>().First(t=>Grid.GetColumn(t)==3);
            var actions=grid.Children.OfType<StackPanel>().First(p=>Grid.GetColumn(p)==4);
            completion.Children.Remove(count);completion.Children.Remove(icon);grid.Children.Remove(completion);
            var theme=AppearanceController.For(owner);
            count.Name="LibraryAchievementCount";count.TextWrapping=TextWrapping.NoWrap;count.TextTrimming=TextTrimming.CharacterEllipsis;count.FontSize=12;count.VerticalAlignment=VerticalAlignment.Center;
            ToolTip.SetTip(count,Get(game,"ProgressLabel")?.ToString());
            var countContent=new StackPanel {Spacing=3};countContent.Children.Add(count);
            countContent.Children.Add(new TextBlock {Name="LibraryGameCompletionPercentage",Text=CompletionSummary.GamePercentage(game),FontSize=11,Foreground=new SolidColorBrush(Color.Parse("#8FE3BC")),TextWrapping=TextWrapping.NoWrap});
            var countCell=new Border {Name="LibraryAchievementSection",Child=countContent,Background=theme.Panel,BorderBrush=theme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(10,7),VerticalAlignment=VerticalAlignment.Center};
            Grid.SetColumn(countCell,2);grid.Children.Add(countCell);
            icon.HorizontalAlignment=HorizontalAlignment.Center;icon.VerticalAlignment=VerticalAlignment.Center;
            var mythicCell=new Border {Name="LibraryMythicSection",Child=icon,Background=theme.Panel,BorderBrush=theme.Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Height=34,VerticalAlignment=VerticalAlignment.Center};
            mythicCell.Bind(Visual.IsVisibleProperty,new Binding("IsVisible") {Source=icon});ToolTip.SetTip(mythicCell,"Mythic completion");Grid.SetColumn(mythicCell,3);grid.Children.Add(mythicCell);
            Grid.SetColumn(id,4);id.FontSize=12;id.VerticalAlignment=VerticalAlignment.Center;
            var session=new Button {Name="LibraryOpenGameSession",Content="Open Game Session",Tag=game};
            if(!preview)session.Bind(InputElement.IsEnabledProperty,new Binding("CanQuery") {Source=model});
            session.Click+=(_,_)=>OpenSession(model,game,preview);ToolTip.SetTip(session,"Open this game's details, title spoofer and Auto Unlock.");actions.Children.Add(session);
            actions.Spacing=6;actions.VerticalAlignment=VerticalAlignment.Center;
            foreach(var button in actions.Children.OfType<Button>()){button.FontSize=12;button.Padding=new Thickness(9,7);button.Margin=new Thickness(0);}
            grid.ColumnSpacing=10;grid.RowSpacing=9;
            bool? currentCompact=null;
            void Layout(){
                bool compact=grid.Bounds.Width<1160;
                if(currentCompact==compact)return;currentCompact=compact;
                grid.ColumnDefinitions=new ColumnDefinitions(compact?"54,*,210,40,100":"54,*,210,40,100,Auto");
                grid.RowDefinitions=new RowDefinitions(compact?"Auto,Auto":"Auto");
                Grid.SetColumn(actions,compact?1:5);Grid.SetRow(actions,compact?1:0);Grid.SetColumnSpan(actions,compact?4:1);
                actions.HorizontalAlignment=HorizontalAlignment.Right;
            }
            Layout();grid.SizeChanged+=(_,_)=>Layout();
            return row;
        });
    }
    static object? Get(object model,string name)=>model.GetType().GetProperty(name)?.GetValue(model);
    static void Set(object model,string name,object? value)=>model.GetType().GetProperty(name)?.SetValue(model,value);
    public static void OpenSession(object model,object game,bool preview){
        Set(model,"SelectedLibraryGame",game);Set(model,"SelectedGame",game);
        if(preview){Set(model,"PresenceTitleId",Get(game,"Id"));Set(model,"SpoofTitleName",Get(game,"Name"));model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Spoofer"});}
        else model.GetType().GetMethod("OpenGameSpoofer",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(model,null);
    }
    public static async Task CheckPreview(Window owner,object model,Grid root,string output){
        model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Library"});
        foreach(var size in new[]{(1680,1100,"library-wide.png"),(1440,920,"library-compact.png"),(1120,720,"library-small.png")}){
            owner.Width=size.Item1;owner.Height=size.Item2;await Task.Delay(650);
            var counts=root.GetLogicalDescendants().OfType<TextBlock>().Where(t=>t.Name=="LibraryAchievementCount").Distinct().ToArray();
            var buttons=root.GetLogicalDescendants().OfType<Button>().Where(b=>b.Name=="LibraryOpenGameSession").Distinct().ToArray();
            if(counts.Length!=3 || buttons.Length!=3)throw new Exception("Library preview must contain three aligned rows and session buttons.");
            foreach(var count in counts){if(count.TextWrapping!=TextWrapping.NoWrap || count.Bounds.Height>25 || count.Bounds.Width<185)throw new Exception("Achievement count wrapped or lost its reserved space.");}
            foreach(var button in buttons){var point=button.TranslatePoint(new Point(button.Bounds.Width,button.Bounds.Height),root)!.Value;if(point.X>root.Bounds.Width || button.Bounds.Width<115)throw new Exception("Library session action is clipped.");}
            if(!counts.Any(c=>c.Text?.Contains("1234")==true))throw new Exception("Large achievement count fixture missing.");
            var symbols=root.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p=>p.Name=="MythicCompletionIcon" && p.IsEffectivelyVisible).Distinct().ToArray();
            if(symbols.Length!=2 || symbols.Any(p=>p.Bounds.Width<=0 || p.Bounds.Height<=0))throw new Exception("Mythic icon fixtures are not visible.");
            var positions=symbols.Select(p=>p.TranslatePoint(new Point(),root)!.Value.X).ToArray();if(Math.Abs(positions[0]-positions[1])>1)throw new Exception("Mythic icons are not aligned.");
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));bitmap.Render(root);bitmap.Save(Path.Combine(output,size.Item3),new PngBitmapEncoderOptions());
            File.AppendAllText(Path.Combine(output,"library-checks.txt"),$"PASS: {size.Item1}x{size.Item2}, three rows, one-line counts including 1234 / 1234, reserved mythic columns, session buttons within window.\n");
        }
        var target=root.GetLogicalDescendants().OfType<Button>().First(b=>b.Name=="LibraryOpenGameSession" && Get(b.Tag!,"Id")?.ToString()=="1289871275");
        target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(200);
        if(Get(model,"PresenceTitleId")?.ToString()!="1289871275" || Get(Get(model,"SelectedGame")!,"Id")?.ToString()!="1289871275" || Get(model,"IsSpoofer") is not true)throw new Exception($"Library session shortcut failed: title={Get(model,"PresenceTitleId")}, selected={Get(Get(model,"SelectedGame")!,"Id")}, spoofer={Get(model,"IsSpoofer")}, target={Get(target.Tag!,"Id")}, enabled={target.IsEffectivelyEnabled}");
        File.AppendAllText(Path.Combine(output,"library-checks.txt"),"PASS: clicking the second game's session button selects that game and opens Game session with its title ID. No account or execution actions were invoked.\n");
        model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Library"});owner.Width=1680;owner.Height=1100;
        if(Environment.GetCommandLineArgs().Contains("--scroll-regression"))await CheckScrolling(owner,model,root,output);
    }
    static async Task CheckScrolling(Window owner,object model,Grid root,string output){
        var originals=(Array)Get(model,"Games")!;var type=originals.GetType().GetElementType()!;var games=Array.CreateInstance(type,240);
        for(int i=0;i<games.Length;i++){
            var game=type.GetMethod("<Clone>$")!.Invoke(originals.GetValue(i%originals.Length),null)!;
            Set(game,"Id",(100000000+i).ToString());Set(game,"Name",$"Demo game {i+1:D3} · {Get(game,"Name")}");games.SetValue(game,i);
        }
        Set(model,"Games",games);owner.Width=1440;owner.Height=920;await Task.Delay(700);
        var list=root.GetLogicalDescendants().OfType<ListBox>().First(l=>l.ItemTemplate is FuncDataTemplate<object> && l.Items.Count==240);
        var scroller=list.GetVisualDescendants().OfType<ScrollViewer>().First();
        if(list.ItemTemplate!.Build(null)!=null)throw new Exception("Empty recycled item must have no row.");
        foreach(var offset in new[]{10d,30,60,120,240,500,1000,2000,4000,8000,16000,24000,8000,1000,0}){
            File.AppendAllText(Path.Combine(output,"scroll-checks.txt"),$"Scrolling to {offset}, extent={scroller.Extent.Height}, viewport={scroller.Viewport.Height}\n");
            scroller.Offset=new Vector(0,offset);await Task.Delay(150);
        }
        File.AppendAllText(Path.Combine(output,"scroll-checks.txt"),"PASS: scrolled 240-game library down and back to the top.\n");
    }
    static IEnumerable<string> Dump(Control control,int depth=0){
        yield return new string(' ',depth*2)+control.GetType().Name+" #"+control.Name+" "+(control is TextBlock t?t.Text:control is ContentControl c?c.Content as string:"")+" col="+Grid.GetColumn(control)+(control is Grid g?" cols="+g.ColumnDefinitions:"");
        foreach(var child in control.GetLogicalChildren().OfType<Control>())foreach(var line in Dump(child,depth+1))yield return line;
    }
}
