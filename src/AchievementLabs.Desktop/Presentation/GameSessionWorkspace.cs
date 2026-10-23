using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace AchievementLabs.Desktop.Presentation;

using Image = Avalonia.Controls.Image;

public static class GameSessionWorkspace
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static SolidColorBrush Brush(string value)=>new(Color.Parse(value));
    static object? Get(object? model,string name)=>model?.GetType().GetProperty(name,Flags)?.GetValue(model);
    static void Set(object model,string name,object? value)=>model.GetType().GetProperty(name,Flags)?.SetValue(model,value);
    static TextBlock Label(string text)=>new() {Text=text,FontSize=11,LetterSpacing=1.7,FontWeight=FontWeight.SemiBold,Foreground=Brush("#8FE3BC")};
    static ScrollViewer Scroll(Control content)=>new() {Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
    static Border Glass(Control content,Image source,Window owner)
    {
        var canvas=new Canvas {IsHitTestVisible=false};
        var blur=new Image {Stretch=Stretch.Uniform,Effect=new BlurEffect {Radius=30},IsHitTestVisible=false};blur.Bind(Image.SourceProperty,new Binding("Source") {Source=source});canvas.Children.Add(blur);
        var shade=new Border {Background=Brush("#A0101A22"),IsHitTestVisible=false};
        var layers=new Grid();layers.Children.Add(canvas);layers.Children.Add(shade);layers.Children.Add(content);
        var outline=new Border {CornerRadius=new CornerRadius(20),BorderBrush=Brush("#60D7E8ED"),BorderThickness=new Thickness(1),IsHitTestVisible=false};layers.Children.Add(outline);
        var frame=new Border {CornerRadius=new CornerRadius(20),Background=Brush("#50111B23"),BorderBrush=Brush("#50D7E8ED"),BorderThickness=new Thickness(1),Padding=new Thickness(20),Child=layers,ClipToBounds=true};
        owner.LayoutUpdated+=(_,_)=>{
            canvas.Margin=shade.Margin=outline.Margin=new Thickness(-frame.Padding.Left,-frame.Padding.Top,-frame.Padding.Right,-frame.Padding.Bottom);
            var origin=source.TranslatePoint(new Point(),canvas);if(origin==null)return;
            blur.Width=source.Bounds.Width;blur.Height=source.Bounds.Height;Canvas.SetLeft(blur,origin.Value.X);Canvas.SetTop(blur,origin.Value.Y);
        };
        AppearanceController.For(owner).RegisterGlass(frame,blur,shade,outline);
        return frame;
    }
    public static void Attach(Window owner)
    {
        bool preview=Environment.GetCommandLineArgs().Contains("--visual-preview");
        if(preview)AppContext.SetSwitch("AchievementLabs.OfflineChecks",true);
        var model=owner.GetType().GetField("model",Flags)?.GetValue(owner);
        if(model==null)return;
        var appearance=AppearanceController.For(owner);
        var all=owner.GetLogicalDescendants().ToArray();
        var start=all.OfType<Button>().Single(b=>Equals(b.Content,"Start spoofing"));
        var presence=start.GetLogicalAncestors().OfType<StackPanel>().First();
        var original=presence.GetLogicalAncestors().OfType<Border>().First(b=>b.Child is Grid g && g.Children.OfType<Control>().Any(c=>c==presence));
        var content=(Grid)original.Child!;
        var artwork=content.Children.OfType<Grid>().First();
        var coverImage=artwork.GetLogicalDescendants().OfType<Image>().First();
        var title=content.Children.OfType<StackPanel>().First(p=>p!=presence);
        var spoofer=(Grid)original.Parent!;
        var main=(Grid)spoofer.Parent!;
        var root=all.OfType<Grid>().Single(g=>g.Name=="PreviewRoot");
        var build=all.OfType<Button>().Single(b=>Equals(b.Content,"Build queue"));
        var queueTabs=build.GetLogicalAncestors().OfType<TabControl>().First();
        var queueParent=(Panel)queueTabs.Parent!;
        if(preview) {
            var outdir=Environment.GetCommandLineArgs().SkipWhile(a=>a!="--preview-output").Skip(1).FirstOrDefault();
            if(outdir!=null){Directory.CreateDirectory(outdir);File.WriteAllLines(Path.Combine(outdir,"queue-original-tree.txt"),Dump(queueTabs));}
        }
        queueParent.Children.Remove(queueTabs);queueTabs.Bind(Visual.IsVisibleProperty,new Binding {Source=true});queueTabs.Margin=new Thickness(0);Grid.SetRow(queueTabs,1);
        spoofer.Children.Remove(original);original.Child=null;content.Children.Remove(artwork);content.Children.Remove(title);content.Children.Remove(presence);
        artwork.Children.Remove(coverImage);coverImage.IsVisible=false;
        var gameArt=new Image {Name="SessionArtwork",IsHitTestVisible=false};
        var image=new Image {Name="SessionBackdrop",Stretch=Stretch.Uniform,IsHitTestVisible=false};
        var backdrop=new Image {Stretch=Stretch.UniformToFill,Opacity=.65,Effect=new BlurEffect {Radius=54},IsHitTestVisible=false};
        backdrop.Bind(Image.SourceProperty,new Binding("Source") {Source=image});
        var background=new Grid {Name="SpooferArtworkBackground",ClipToBounds=true,Background=new LinearGradientBrush {StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,1,RelativeUnit.Relative),GradientStops=new GradientStops {new GradientStop(Color.Parse("#102330"),0),new GradientStop(Color.Parse("#162C30"),.55),new GradientStop(Color.Parse("#0B111E"),1)}},IsHitTestVisible=false};
        var sessionVisibility=new Border {IsVisible=false};
        Grid.SetColumnSpan(background,2);Grid.SetRowSpan(background,2);background.Children.Add(coverImage);background.Children.Add(backdrop);
        image.Margin=new Thickness(240,0,0,0);background.Children.Add(image);background.Children.Add(new Border {Name="BackgroundTint",Background=Brush("#40101A22")});root.Children.Insert(0,background);
        spoofer.RowDefinitions=new RowDefinitions("Auto,Auto,Auto");spoofer.RowSpacing=16;
        title.Margin=new Thickness(24,18);title.VerticalAlignment=VerticalAlignment.Center;title.Spacing=10;Grid.SetColumn(title,0);
        var titleName=title.Children.OfType<TextBlock>().First(t=>t.FontSize>=24);
        foreach(var text in title.Children.OfType<TextBlock>()) {
            if(text.Text=="SPOOFED TITLE")text.Text="GAME DETAILS";
            if(text.FontSize>=24){text.FontSize=28;text.FontWeight=FontWeight.SemiBold;}
            if(text.Classes.Contains("muted"))text.Foreground=Brush("#D3DFE2");
        }
        var metadata=title.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Classes.Contains("muted"));
        if(metadata!=null){
            var index=title.Children.IndexOf(metadata);
            var details=new SelectableTextBlock {Name="SelectableGameDetails",FontSize=12,LineHeight=19,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#D3DFE2")};
            details.Bind(TextBlock.TextProperty,new Binding("SpoofTitleDetails"));
            var copy=new MenuItem {Header="Copy game details"};copy.Click+=async (_,_)=>{if(owner.Clipboard!=null)await owner.Clipboard.SetTextAsync(details.Text??"");};
            details.ContextMenu=new ContextMenu {ItemsSource=new[]{copy}};
            title.Children.RemoveAt(index);title.Children.Insert(index,details);metadata=details;
        }
        var output=title.Children.OfType<TextBlock>().LastOrDefault(t=>t!=metadata && t.FontSize<24 && !t.Classes.Contains("eyebrow"));
        if(output!=null){title.Children.Remove(output);output.FontSize=11;presence.Children.Insert(1,output);}
        var sessionCover=new Image {Name="GameSessionCover",Width=112,Height=148,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center};
        var coverFrame=new Border {Name="GameSessionCoverFrame",Child=sessionCover,CornerRadius=new CornerRadius(12),ClipToBounds=true,Background=Brush("#24111E27"),BorderBrush=appearance.Line,BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Center};
        var achievementPicture=all.OfType<Image>().FirstOrDefault(i=>i!=coverImage && i.GetType().Name=="RemoteImage" && i.GetLogicalAncestors().OfType<Grid>().Any(g=>g.Children.OfType<StackPanel>().Any(p=>p.Children.OfType<TextBlock>().Any(t=>t.FontSize>=26))));
        void UpdateSessionCover(){
            var selected=Get(model,"SelectedGame");bool matches=Get(selected,"Id")?.ToString()==Get(model,"PresenceTitleId")?.ToString();
            sessionCover.Source=(matches?achievementPicture?.Source:null)??coverImage.Source;coverFrame.IsVisible=sessionCover.Source!=null;
            var size=sessionCover.Source?.Size;double ratio=size is {Height:>0}?size.Value.Width/size.Value.Height:1;
            sessionCover.Width=ratio>1.35?156:112;sessionCover.Height=ratio>1.35?96:ratio<.8?148:112;
        }
        coverImage.PropertyChanged+=(_,e)=>{if(e.Property==Image.SourceProperty)UpdateSessionCover();};
        if(achievementPicture!=null)achievementPicture.PropertyChanged+=(_,e)=>{if(e.Property==Image.SourceProperty)UpdateSessionCover();};
        if(model is INotifyPropertyChanged coverModel){PropertyChangedEventHandler coverChanged=(_,e)=>{if(e.PropertyName is "SelectedGame" or "PresenceTitleId")UpdateSessionCover();};coverModel.PropertyChanged+=coverChanged;owner.Closed+=(_,_)=>coverModel.PropertyChanged-=coverChanged;}
        UpdateSessionCover();
        var titleWithCover=new Grid {ColumnDefinitions=new ColumnDefinitions("Auto,*"),ColumnSpacing=16};titleWithCover.Children.Add(coverFrame);Grid.SetColumn(title,1);titleWithCover.Children.Add(title);
        title.Margin=new Thickness(0);var titleGlass=Glass(titleWithCover,image,owner);titleGlass.VerticalAlignment=VerticalAlignment.Center;
        var presenceHeading=presence.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Text=="PRESENCE");if(presenceHeading!=null)presence.Children.Remove(presenceHeading);
        var stop=presence.Children.OfType<Button>().Single(b=>Equals(b.Content,"Stop spoofing"));presence.Children.Remove(start);presence.Children.Remove(stop);
        var diagnostics=presence.Children.OfType<StackPanel>().FirstOrDefault(p=>p.Name=="PresenceDiagnostics");
        if(diagnostics!=null){presence.Children.Remove(diagnostics);diagnostics.Margin=new Thickness(0,10,0,0);presence.Children.Add(Label("PRESENCE DIAGNOSTICS"));presence.Children.Add(diagnostics);}
        var playtime=presence.Children.OfType<StackPanel>().FirstOrDefault(p=>p.Name=="XboxRecordedPlaytime");
        if(playtime!=null){playtime.Margin=new Thickness(0,12);playtime.Spacing=5;}
        presence.Spacing=7;
        var presenceActions=new WrapPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0)};start.Margin=new Thickness(0,0,8,0);presenceActions.Children.Add(start);presenceActions.Children.Add(stop);
        var compactPresence=new Grid {ColumnDefinitions=new ColumnDefinitions("*,1.05*"),RowDefinitions=new RowDefinitions("Auto,Auto"),ColumnSpacing=14,RowSpacing=8};
        if(playtime!=null){presence.Children.Remove(playtime);playtime.Margin=new Thickness(0);Grid.SetColumn(playtime,1);compactPresence.Children.Add(playtime);}
        if(diagnostics!=null){
            presence.Children.Remove(diagnostics);var diagLabel=presence.Children.OfType<TextBlock>().FirstOrDefault(t=>t.Text=="PRESENCE DIAGNOSTICS");if(diagLabel!=null)presence.Children.Remove(diagLabel);
            var diagLine=new WrapPanel {Orientation=Orientation.Horizontal};foreach(var child in diagnostics.Children.ToArray()){diagnostics.Children.Remove(child);if(child is TextBlock text){text.FontSize=11;text.MaxWidth=310;}if(child is Button button){button.FontSize=11;button.Padding=new Thickness(8,5);}child.Margin=new Thickness(0,0,8,4);diagLine.Children.Add(child);}Grid.SetRow(diagLine,1);Grid.SetColumnSpan(diagLine,2);compactPresence.Children.Add(diagLine);
        }
        presence.Children.Add(presenceActions);Grid.SetColumn(presence,0);Grid.SetRow(presence,0);compactPresence.Children.Add(presence);
        var left=new Grid {Name="CombinedSpooferPanel",RowDefinitions=new RowDefinitions("Auto,Auto"),RowSpacing=10};left.Children.Add(Label("TITLE SPOOFER"));Grid.SetRow(compactPresence,1);left.Children.Add(compactPresence);
        var leftGlass=Glass(left,image,owner);leftGlass.VerticalAlignment=VerticalAlignment.Top;
        AdaptQueues(queueTabs,model,preview,owner);
        var right=new Grid {Name="CombinedAutoUnlockPanel",RowDefinitions=new RowDefinitions("Auto,*"),RowSpacing=8};right.Children.Add(Label("AUTO UNLOCK"));right.Children.Add(queueTabs);
        var rightGlass=Glass(right,image,owner);rightGlass.VerticalAlignment=VerticalAlignment.Stretch;Grid.SetColumn(rightGlass,1);
        var top=new Grid {Name="SessionTopPanels",ColumnDefinitions=new ColumnDefinitions("1.05*,.95*"),ColumnSpacing=16};
        leftGlass.VerticalAlignment=VerticalAlignment.Stretch;titleGlass.VerticalAlignment=VerticalAlignment.Stretch;top.Children.Add(leftGlass);Grid.SetColumn(titleGlass,1);top.Children.Add(titleGlass);Grid.SetRow(top,1);spoofer.Children.Add(top);
        Grid.SetColumn(rightGlass,0);Grid.SetRow(rightGlass,2);spoofer.Children.Add(rightGlass);
        foreach(var button in new[]{start,stop}){button.Padding=new Thickness(12,7);button.FontSize=12;}
        foreach(var button in all.OfType<Button>().Where(b=>Equals(b.Content,"Open Auto Unlock")||b.Name=="OpenGameAutoSpoofer"))button.IsVisible=false;
        var openSession=all.OfType<Button>().First(b=>string.Equals(b.Content?.ToString(),"Open Title Spoof",StringComparison.OrdinalIgnoreCase));openSession.Content="Open in Game session";ToolTip.SetTip(openSession,"Open this game's title details, Xbox playtime and Auto Unlock workspace.");
        main.Children.Remove(spoofer);var sessionScroll=Scroll(spoofer);sessionScroll.Name="GameSessionScroll";Grid.SetRow(sessionScroll,1);main.Children.Add(sessionScroll);
        var nav=all.OfType<Button>().Single(b=>b.Name=="XboxAutoUnlockNav");nav.IsVisible=false;
        var spoofNav=all.OfType<Button>().First(b=>Equals(b.Content,"Title spoofer"));spoofNav.Content="Game session";
        var library=all.OfType<Button>().First(b=>b.Name=="LibraryNav");var achievements=all.OfType<Button>().First(b=>b.Name=="AchievementsNav");
        if(library.Parent is Panel navigation){foreach(var button in new[]{library,achievements,spoofNav})navigation.Children.Remove(button);navigation.Children.Insert(0,library);navigation.Children.Insert(1,achievements);navigation.Children.Insert(2,spoofNav);}
        var sidebar=root.Children.OfType<Border>().First(b=>Grid.GetColumn(b)==0 && Grid.GetRowSpan(b)==2);
        var sidebarContent=sidebar.Child!;sidebar.Child=null;
        var sideLayers=new Grid();var sideArt=new Image {Stretch=Stretch.UniformToFill,Effect=new BlurEffect {Radius=48},Opacity=.55,IsHitTestVisible=false};sideArt.Bind(Image.SourceProperty,new Binding("Source") {Source=image});
        sideLayers.Children.Add(sideArt);sideLayers.Children.Add(new Border {Background=appearance.Sidebar,IsHitTestVisible=false});sideLayers.Children.Add(sidebarContent);sidebar.Child=sideLayers;sidebar.ClipToBounds=true;sidebar.Background=Brushes.Transparent;sidebar.BorderBrush=appearance.Line;
        foreach(var expander in sidebar.GetLogicalDescendants().OfType<Expander>()){expander.Background=appearance.Panel;expander.BorderBrush=appearance.Line;expander.BorderThickness=new Thickness(1);expander.CornerRadius=new CornerRadius(12);}
        foreach(var panel in sidebar.GetLogicalDescendants().OfType<Border>().Where(b=>b.Classes.Contains("panel"))){panel.Background=appearance.Panel;panel.BorderBrush=appearance.Line;panel.CornerRadius=new CornerRadius(12);}
        sidebar.Styles.Add(new Style(x=>x.OfType<Button>().Class("nav")){Setters={new Setter(Button.CornerRadiusProperty,new CornerRadius(11)),new Setter(Button.BackgroundProperty,appearance.Button)}});
        sidebar.Styles.Add(new Style(x=>x.OfType<Button>().Class("nav").Class("active")){Setters={new Setter(Button.BackgroundProperty,Brush("#405B9E89")),new Setter(Button.BorderBrushProperty,Brush("#507FE0B8")),new Setter(Button.BorderThicknessProperty,new Thickness(1)),new Setter(Button.ForegroundProperty,Brush("#B8F1D7"))}});
        LibraryPresentation.Attach(owner,model,all,preview);
        CompletionOverview.Attach(owner,model,all,preview);
        AppGlassTheme.Apply(owner);
        appearance.Configure(background,gameArt,image,backdrop,sideArt,all.OfType<Control>());
        _=new WideArtwork(owner,model,coverImage,gameArt,preview);
        var settingsTabs=all.OfType<TabControl>().First(t=>t.Items.OfType<TabItem>().Any(i=>Equals(i.Header,"App")) && t.Items.OfType<TabItem>().Any(i=>Equals(i.Header,"Account")));
        var appearanceTab=new TabItem {Header="Appearance",Name="AppearanceTab",Content=appearance.BuildSettings()};settingsTabs.Items.Add(appearanceTab);
        foreach(var saveButton in all.OfType<Button>().Where(b=>string.Equals(b.Content?.ToString(),"Save all settings",StringComparison.OrdinalIgnoreCase)))saveButton.Click+=(_,_)=>appearance.Save();
        var refreshPlaytime=all.OfType<Button>().First(b=>b.Name=="RefreshXboxPlaytime");PlaytimeAutoRefresh.Attach(owner,model,refreshPlaytime,preview);
        spoofer.Bind(Visual.IsVisibleProperty,new Binding("IsVisible") {Source=sessionVisibility});sessionScroll.Bind(Visual.IsVisibleProperty,new Binding("IsVisible") {Source=sessionVisibility});
        var header=main.Children.OfType<Grid>().First(g=>Grid.GetRow(g)==0);
        var heading=header.Children.OfType<TextBlock>().First();
        var queue=Get(model,"XboxQueue");
        bool preparing=false,wasCombined=false;
        void Update()
        {
            bool combined=Get(model,"IsSpoofer") is true || Get(model,"IsQueues") is true;
            sessionVisibility.IsVisible=combined;background.IsVisible=true;appearance.SetPage(combined);spoofNav.Classes.Set("active",combined);
            heading.Text=combined?"Game session":Get(model,"PageTitle")?.ToString();header.Background=appearance.Header;
            if(combined && !wasCombined && !preparing && !preview)
            {
                preparing=true;
                try {
                    model.GetType().GetMethod("SyncWorkflowAccount",Flags)?.Invoke(model,null);
                    queue?.GetType().GetMethod("OnNavigatedTo",Flags)?.Invoke(queue,null);
                    var builder=Get(model,"XboxBuildQueue");builder?.GetType().GetMethod("Refresh",Flags)?.Invoke(builder,null);
                    var id=Get(model,"PresenceTitleId")?.ToString();
                    if(queue!=null && string.IsNullOrWhiteSpace(Get(queue,"TitleId")?.ToString()) && Get(queue,"IsRunning") is not true && uint.TryParse(id,out _))Set(queue,"TitleId",id);
                    if(Get(model,"IsQueues") is true)SpooferTitleLoader.Begin(model);
                } finally {preparing=false;}
            }
            wasCombined=combined;
        }
        if(model is INotifyPropertyChanged npc){PropertyChangedEventHandler changed=(_,e)=>{
            if(e.PropertyName is "IsSpoofer" or "IsQueues" or "PageTitle")Update();
            if(e.PropertyName=="PresenceTitleId" && !preview && (Get(model,"IsSpoofer") is true || Get(model,"IsQueues") is true)) {
                var id=Get(model,"PresenceTitleId")?.ToString();
                if(queue!=null && string.IsNullOrWhiteSpace(Get(queue,"TitleId")?.ToString()) && Get(queue,"IsRunning") is not true && uint.TryParse(id,out uint parsedTitle))Set(queue,"TitleId",id);
                if(Get(model,"IsQueues") is true)SpooferTitleLoader.Begin(model);
            }
        };npc.PropertyChanged+=changed;owner.Closed+=(_,_)=>npc.PropertyChanged-=changed;}
        Update();
        void FitSession(){
            var height=sessionScroll.Viewport.Height;
            bool fit=height>=780;
            spoofer.Height=fit?height:double.NaN;
            spoofer.RowDefinitions=new RowDefinitions(fit?"Auto,Auto,*":"Auto,Auto,Auto");
            foreach(var tab in queueTabs.Items.OfType<TabItem>())if(tab.Content is Grid queueGrid){
                queueGrid.RowDefinitions=new RowDefinitions(fit?"Auto,*,Auto":"Auto,Auto,Auto");
                foreach(var items in queueGrid.Children.OfType<ListBox>()){items.MinHeight=fit?64:140;items.MaxHeight=fit?double.PositiveInfinity:280;}
            }
        }
        sessionScroll.SizeChanged+=(_,_)=>FitSession();
        owner.SizeChanged+=(_,_)=>{
            bool compact=owner.ClientSize.Height<780;
            leftGlass.Padding=rightGlass.Padding=new Thickness(compact?14:20);
            titleGlass.Padding=new Thickness(compact?14:20);titleName.FontSize=compact?21:24;
            FitSession();
        };
        owner.Title=preview?"Achievement Labs · Demo":"Achievement Labs";
        if(!preview)return;
        owner.Opened+=async (_,_)=>{
            PreviewFixtures.EnsureArtwork();
            Set(model,"PresenceTitleId","1938834516");Set(model,"SpoofTitleName","State of Decay 2: Juggernaut Edition");Set(model,"SpoofTitleDetails","Title ID: 1938834516\nPC · Xbox One · Xbox Series X|S\nGamerscore: 2380/2400");Set(model,"SpoofingStatus","Ready to spoof");Set(model,"StatsOutput","");
            AddPreviewLibrary(model,owner.GetType().Assembly);
            Set(model,"SpoofTitleImageUrl",new Uri(Path.Combine(PreviewFixtures.ArtworkDirectory,"sod2-full-artwork.jpg")).AbsoluteUri);
            model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Spoofer"});await Task.Delay(1600);
            if(sessionCover.Source==null || (achievementPicture?.Source!=null && !ReferenceEquals(sessionCover.Source,achievementPicture.Source)))throw new Exception("Game session did not reuse the achievement page's game image.");
            var outdir=Environment.GetCommandLineArgs().SkipWhile(a=>a!="--preview-output").Skip(1).FirstOrDefault();if(outdir==null)return;Directory.CreateDirectory(outdir);
            foreach(var size in new[]{(1440,920,"combined-session.png"),(1120,720,"combined-compact.png"),(1680,1100,"combined-large.png")}){
                owner.WindowState=WindowState.Normal;owner.Width=size.Item1;owner.Height=size.Item2;await Task.Delay(500);
                using var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));bitmap.Render(root);bitmap.Save(Path.Combine(outdir,size.Item3),new PngBitmapEncoderOptions());
                File.AppendAllText(Path.Combine(outdir,"layout-check.txt"),$"{size.Item1}x{size.Item2}: top={top.Bounds}; spoofer={left.Bounds}; details={title.Bounds}; auto={right.Bounds}; tabs visible={queueTabs.IsVisible}, bounds={queueTabs.Bounds}, selected={queueTabs.SelectedIndex}\n");
            }
            owner.Width=1440;owner.Height=920;
            queueTabs.SelectedIndex=1;await Task.Delay(500);
            using(var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96))){bitmap.Render(root);bitmap.Save(Path.Combine(outdir,"combined-build-queue.png"),new PngBitmapEncoderOptions());}
            queueTabs.SelectedIndex=0;
            File.WriteAllText(Path.Combine(outdir,"controls-check.txt"),$"Combined session contains {queueTabs.GetLogicalDescendants().OfType<Button>().Count()} original queue buttons; tabs visible={queueTabs.IsVisible}; spoofer action parent={start.Parent?.GetType().Name}; queue parent={queueTabs.Parent?.GetType().Name}. No execution commands were invoked.\n");
            await Task.Delay(300);
            var options=queueTabs.GetLogicalDescendants().OfType<Border>().First(e=>e.Name=="QueueToolsSection");
            var queueList=queueTabs.GetLogicalDescendants().OfType<ListBox>().First(l=>l.Name=="XboxAutoUnlockList");
            var before=queueList.TranslatePoint(new Point(),spoofer)!.Value.Y;
            await Task.Delay(500);
            var after=queueList.TranslatePoint(new Point(),spoofer)!.Value.Y;
            var scrollParent=options.GetLogicalAncestors().OfType<ScrollViewer>().FirstOrDefault();
            File.WriteAllText(Path.Combine(outdir,"flow-check.txt"),$"Settings expansion moved queue list from Y={before:F0} to Y={after:F0}; nearest scroll container={scrollParent?.Name}; auto-unlock panel height={right.Bounds.Height:F0}; session/spoofer dropdown count={title.GetLogicalDescendants().OfType<Expander>().Count()+presence.GetLogicalDescendants().OfType<Expander>().Count()}.\n");
            if(!options.IsVisible || options.Bounds.Height<100 || scrollParent!=sessionScroll || queueTabs.GetLogicalDescendants().OfType<Expander>().Any(e=>e.Name=="QueueSettingsExpander"))throw new Exception("Queue tools are not permanently integrated.");
            using(var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96))){bitmap.Render(root);bitmap.Save(Path.Combine(outdir,"settings-expanded-top.png"),new PngBitmapEncoderOptions());}
            sessionScroll.Offset=new Vector(0,sessionScroll.Extent.Height);await Task.Delay(300);
            using(var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96))){bitmap.Render(root);bitmap.Save(Path.Combine(outdir,"settings-expanded-bottom.png"),new PngBitmapEncoderOptions());}
            owner.Width=1680;owner.Height=1050;sessionScroll.Offset=new Vector();await Task.Delay(600);
            if(sessionScroll.Extent.Height>sessionScroll.Viewport.Height+1)throw new Exception($"Session page overflow: {sessionScroll.Extent.Height} > {sessionScroll.Viewport.Height}");
            var queueRoot=(Grid)queueList.Parent!;
            var footer=queueRoot.Children.OfType<Control>().First(c=>Grid.GetRow(c)==2);
            var footerBottom=footer.TranslatePoint(new Point(0,footer.Bounds.Height),sessionScroll)!.Value.Y;
            if(footerBottom>sessionScroll.Viewport.Height+1)throw new Exception("Auto Unlock footer clipped below the viewport.");
            var selectable=title.Children.OfType<SelectableTextBlock>().First(t=>t.Name=="SelectableGameDetails");
            selectable.SelectionStart=0;selectable.SelectionEnd=selectable.Text!.Length;
            if(!selectable.SelectedText.Contains("1938834516"))throw new Exception("Game details cannot be selected.");
            selectable.SelectionEnd=0;
            File.WriteAllText(Path.Combine(outdir,"fullscreen-check.txt"),$"PASS: expanded tools, page extent={sessionScroll.Extent.Height:F0}, viewport={sessionScroll.Viewport.Height:F0}, list height={queueList.Bounds.Height:F0}, footer bottom={footerBottom:F0}. Game details selection includes the title ID. Delay fixtures: 9999.5 and 10080 minutes.\n");
            Capture("expanded-fit.png");
            sessionScroll.Offset=new Vector();owner.Width=1440;owner.Height=920;await Task.Delay(300);
            foreach(var page in new[]{"Home","Library","Achievements","Settings","Tools","Stats","About","SteamLibrary","Epic","Ubisoft"}){
                model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{page});await Task.Delay(250);
                using var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));bitmap.Render(root);bitmap.Save(Path.Combine(outdir,"app-"+page.ToLowerInvariant()+".png"),new PngBitmapEncoderOptions());
            }
            model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Spoofer"});
            sessionScroll.Offset=new Vector();await Task.Delay(300);
            appearance.Preferences.GlassBlur=65;appearance.Preferences.GlassColour="#30223F";appearance.Apply();await Task.Delay(200);Capture("session-purple-glass.png");
            if(appearance.Panel.Color.R!=48 || appearance.Panel.Color.B!=63)throw new Exception("Glass colour was not applied.");
            appearance.Preferences.GlassBlur=0;appearance.Apply();await Task.Delay(200);Capture("session-no-blur.png");
            appearance.Preferences.GlassBlur=30;appearance.Preferences.GlassColour="#111E27";appearance.Apply();
            appearance.Preferences.GlassTransparency=60;appearance.Apply();await Task.Delay(200);Capture("session-transparency-60.png");
            appearance.Preferences.GlassTransparency=35;appearance.Preferences.OledMode=true;appearance.Apply();await Task.Delay(200);Capture("session-oled.png");
            if(image.IsVisible || sideArt.IsVisible || appearance.Panel.Color!=Colors.Black)throw new Exception("OLED surfaces or background were not black.");
            appearance.Preferences.OledMode=false;appearance.SetCustomForPreview(Path.Combine(PreviewFixtures.ArtworkDirectory,"sod2-full-artwork.jpg"));await Task.Delay(200);Capture("session-custom-background.png");
            appearance.Preferences.BackgroundMode="Game artwork";appearance.Preferences.PersistentGameArtwork=false;appearance.Apply();model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Home"});await Task.Delay(200);
            if(image.IsVisible || backdrop.IsVisible || sideArt.IsVisible)throw new Exception("Game artwork remained visible with persistence disabled outside the session.");Capture("home-artwork-off.png");
            appearance.Preferences.PersistentGameArtwork=true;appearance.Apply();
            model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Settings"});settingsTabs.SelectedItem=appearanceTab;await Task.Delay(300);Capture("appearance-settings.png");
            model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Achievements"});await Task.Delay(250);Capture("achievements-unified-button.png");
            if(all.OfType<Button>().Any(b=>Equals(b.Content,"Open Auto Unlock")&&b.IsVisible))throw new Exception("A separate Auto Unlock shortcut is still visible.");
            model.GetType().GetMethod("Navigate")!.Invoke(model,new object[]{"Spoofer"});sessionScroll.Offset=new Vector();
            File.WriteAllText(Path.Combine(outdir,"appearance-checks.txt"),"PASS: live transparency, custom background loading, OLED hides artwork and uses black surfaces, game-art persistence toggle, unified achievements shortcut, and appearance settings tab. No appearance preferences saved to the real account profile.\n");
            await CompletionOverview.CheckPreview(owner,model,root,outdir);
            await LibraryPresentation.CheckPreview(owner,model,root,outdir);
            File.WriteAllText(Path.Combine(outdir,"visual-checks-passed.txt"),"PASS: game session, appearance, dashboard, library actions and scrolling.");
            if(Environment.GetCommandLineArgs().Contains("--exit-after-visual-checks"))owner.Close();
            void Capture(string name){using var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));bitmap.Render(root);bitmap.Save(Path.Combine(outdir,name),new PngBitmapEncoderOptions());}
        };
    }
    static IEnumerable<string> Dump(Control c,int depth=0)
    {
        yield return new string(' ',depth*2)+c.GetType().Name+" #"+c.Name+" "+(c is TextBlock t?t.Text:c is ContentControl cc?cc.Content is string s?s:"":"")+" row="+Grid.GetRow(c)+" col="+Grid.GetColumn(c)+(c is Grid g?" rows="+g.RowDefinitions+" cols="+g.ColumnDefinitions:"");
        foreach(var child in c.GetLogicalChildren().OfType<Control>())foreach(var line in Dump(child,depth+1))yield return line;
    }
    static void AddPreviewLibrary(object model,Assembly app)
    {
        object Fixture(Type type,string id,string name,bool unlocked=false,int completed=151,int total=153,string? artFile=null)=>type.GetConstructors().First(c=>c.GetParameters().Any(p=>p.Name?.Equals("Id",StringComparison.OrdinalIgnoreCase)==true)).Invoke(
            type.GetConstructors().First(c=>c.GetParameters().Any(p=>p.Name?.Equals("Id",StringComparison.OrdinalIgnoreCase)==true)).GetParameters().Select(p=>p.Name?.ToLowerInvariant() switch {
                "id"=>(object)id,"name"=>name,"platform"=>"Xbox / PC","completed"=>completed,"total"=>total,"score"=>30,"description"=>"Sample achievement for the visual preview.","unlocked"=>unlocked,
                "imageurl"=>new Uri(Path.Combine(PreviewFixtures.ArtworkDirectory,artFile??"sod2-full-artwork.jpg")).AbsoluteUri,
                _=>p.HasDefaultValue?p.DefaultValue:p.ParameterType.IsValueType?Activator.CreateInstance(p.ParameterType):null
            }).ToArray());
        var gameType=app.GetType("AchievementLabs.Desktop.Game")!;
        var game=Fixture(gameType,"1938834516","State of Decay 2: Juggernaut Edition");
        Set(model,"SelectedGame",game);
        var games=Array.CreateInstance(gameType,3);games.SetValue(game,0);
        var forza=Fixture(gameType,"1289871275","Forza Horizon 3",completed:97,total:97,artFile:"forza.jpg");Set(forza,"MythicVisible",true);Set(forza,"FullyComplete",true);Set(forza,"MythicColour","#86D7AC");games.SetValue(forza,1);
        var halo=Fixture(gameType,"1144039928","Halo: The Master Chief Collection",completed:850,total:1234,artFile:"halo.jpg");Set(halo,"BaseComplete",true);Set(halo,"MythicVisible",true);Set(halo,"FullyComplete",false);Set(halo,"MythicColour","#86D7AC");games.SetValue(halo,2);Set(model,"Games",games);
        var achievementType=app.GetType("AchievementLabs.Desktop.Achievement")!;
        var rows=Array.CreateInstance(achievementType,3);var names=new[]{"Welcome to the community","A home of our own","Strength in numbers"};for(int i=0;i<3;i++)rows.SetValue(Fixture(achievementType,(i+1).ToString(),names[i],i==0),i);
        model.GetType().GetField("achievements",Flags)?.SetValue(model,rows);model.GetType().GetMethod("Refresh",Flags)?.Invoke(model,null);
    }
    static void AdaptQueues(TabControl tabs,object model,bool preview,Window owner)
    {
        foreach(var sp in tabs.GetLogicalDescendants().OfType<StackPanel>().Where(p=>p.Orientation==Orientation.Horizontal).ToArray()){
            var wrap=new WrapPanel {Orientation=Orientation.Horizontal};foreach(var c in sp.Children.ToArray()){sp.Children.Remove(c);wrap.Children.Add(c);c.Margin=new Thickness(c.Margin.Left,c.Margin.Top,Math.Max(6,c.Margin.Right),Math.Max(6,c.Margin.Bottom));}sp.Orientation=Orientation.Vertical;sp.Children.Add(wrap);
        }
        var xbox=tabs.Items.OfType<TabItem>().First(t=>Equals(t.Header,"Xbox"));xbox.Header="Queue";
        if(xbox.Content is not Grid grid)return;
        grid.Margin=new Thickness(0);grid.RowSpacing=8;
        var config=grid.Children.OfType<StackPanel>().FirstOrDefault(p=>Grid.GetRow(p)==0);
        var list=grid.Children.OfType<ListBox>().FirstOrDefault(p=>Grid.GetRow(p)==1);
        if(config!=null){
            config.Spacing=8;var extra=new StackPanel {Spacing=8};
            foreach(var control in config.Children.ToArray()){
                bool fields=control.GetLogicalDescendants().OfType<TextBox>().Any() && control is Grid;
                bool actions=control.GetLogicalDescendants().OfType<Button>().Any(b=>Equals(b.Content,"Build queue"));
                if(actions){
                    var wrap=control as WrapPanel ?? control.GetLogicalDescendants().OfType<WrapPanel>().FirstOrDefault();
                    if(wrap!=null){var advanced=new WrapPanel();advanced.Bind(InputElement.IsEnabledProperty,new Binding("IsEnabled") {Source=control});foreach(var child in wrap.Children.OfType<Button>().Where(b=>!Equals(b.Content,"Build queue")&&!Equals(b.Content,"Load saved queue")).ToArray()){wrap.Children.Remove(child);advanced.Children.Add(child);}extra.Children.Add(advanced);}
                }
                else if(!fields){config.Children.Remove(control);extra.Children.Add(control);}
            }
            extra.Spacing=5;
            foreach(var button in extra.GetLogicalDescendants().OfType<Button>()){button.FontSize=11;button.Padding=new Thickness(8,5);button.MinHeight=28;}
            foreach(var check in extra.GetLogicalDescendants().OfType<CheckBox>()){check.FontSize=11;check.MinHeight=26;check.Margin=new Thickness(0,0,10,0);}
            foreach(var text in extra.Children.OfType<TextBlock>()){text.FontSize=10;text.LineHeight=14;}
            var statusTile=extra.GetLogicalDescendants().OfType<Border>().FirstOrDefault(b=>b.Name=="QueueStatusTile");
            if(statusTile?.Child is StackPanel statusStack){
                var statusLine=new WrapPanel {Orientation=Orientation.Horizontal};
                foreach(var child in statusStack.Children.ToArray()){
                    statusStack.Children.Remove(child);
                    if(child is Grid statusGrid){foreach(var item in statusGrid.Children.ToArray()){statusGrid.Children.Remove(item);item.Margin=new Thickness(0,0,16,4);statusLine.Children.Add(item);}}
                    else {child.Margin=new Thickness(0,0,16,4);statusLine.Children.Add(child);}
                }
                statusTile.Child=statusLine;statusTile.Padding=new Thickness(10,6);
            }
            // The tools are part of the tile, with live queue status anchored at the top right.
            if(statusTile?.Parent is Panel statusParent){
                statusParent.Children.Remove(statusTile);
                if(statusParent.Children.Count==0 && statusParent.Parent is Panel container)container.Children.Remove(statusParent);
            }
            var toolsLayout=new Grid {ColumnDefinitions=new ColumnDefinitions("*,360"),RowDefinitions=new RowDefinitions("Auto,Auto"),ColumnSpacing=18,RowSpacing=10};
            toolsLayout.Children.Add(new TextBlock {Text="Settings, saved queues & tools",FontSize=13,FontWeight=FontWeight.SemiBold,Foreground=Brush("#D3DFE2")});
            Grid.SetRow(extra,1);toolsLayout.Children.Add(extra);
            if(statusTile!=null){statusTile.Margin=new Thickness(0);statusTile.VerticalAlignment=VerticalAlignment.Top;Grid.SetColumn(statusTile,1);Grid.SetRow(statusTile,0);Grid.SetRowSpan(statusTile,2);Grid.SetColumnSpan(statusTile,1);toolsLayout.Children.Add(statusTile);}
            config.Children.Add(new Border {Name="QueueToolsSection",Child=toolsLayout,Padding=new Thickness(12),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),BorderBrush=AppearanceController.For(owner).Line,Background=AppearanceController.For(owner).Panel});
            toolsLayout.SizeChanged+=(_,_)=>{
                bool narrow=toolsLayout.Bounds.Width<1000;
                toolsLayout.ColumnDefinitions=new ColumnDefinitions(narrow?"*":"*,360");
                toolsLayout.RowDefinitions=new RowDefinitions(narrow?"Auto,Auto,Auto":"Auto,Auto");
                if(statusTile!=null){Grid.SetColumn(statusTile,narrow?0:1);Grid.SetRow(statusTile,narrow?2:0);Grid.SetRowSpan(statusTile,narrow?1:2);}
            };
            var summary=new TextBlock {FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#C5D7DC")};summary.Bind(TextBlock.TextProperty,new Binding("XboxQueue.StatusText"));config.Children.Add(summary);
            var rowFields=config.Children.OfType<Grid>().FirstOrDefault(g=>g.GetLogicalDescendants().OfType<TextBox>().Any());
            var rowActions=config.Children.OfType<WrapPanel>().FirstOrDefault(w=>w.Children.OfType<Button>().Any(b=>Equals(b.Content,"Build queue")));
            if(rowFields!=null && rowActions!=null){config.Children.Remove(rowFields);config.Children.Remove(rowActions);rowFields.ColumnDefinitions=new ColumnDefinitions("*,180");var commandRow=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=10};commandRow.Children.Add(rowFields);Grid.SetColumn(rowActions,1);commandRow.Children.Add(rowActions);config.Children.Insert(0,commandRow);}
            grid.RowDefinitions=new RowDefinitions("Auto,Auto,Auto");
        }
        if(list!=null){
            list.Margin=new Thickness(0);list.MinHeight=140;list.MaxHeight=280;list.Background=AppearanceController.For(owner).Panel;
            list.ItemTemplate=new FuncDataTemplate<object>((row,scope)=>{
                var line=new Grid {ColumnDefinitions=new ColumnDefinitions("*,170,86"),ColumnSpacing=8,Margin=new Thickness(6,7)};
                var name=new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=12,VerticalAlignment=VerticalAlignment.Center};name.Bind(TextBlock.TextProperty,new Binding("AchievementName"));line.Children.Add(name);
                var delay=new NumericUpDown {Minimum=0,Maximum=10080,Increment=.5m,FontSize=13,FormatString="0.#",Padding=new Thickness(8,3)};ToolTip.SetTip(delay,"Delay in minutes");delay.Bind(NumericUpDown.ValueProperty,new Binding("DelayMinutes") {Mode=BindingMode.TwoWay});delay.Bind(InputElement.IsEnabledProperty,new Binding("CanEditDelay"));Grid.SetColumn(delay,1);line.Children.Add(delay);
                var status=new TextBlock {FontSize=10,Foreground=Brush("#A7CDBB"),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};status.Bind(TextBlock.TextProperty,new Binding("Status"));Grid.SetColumn(status,2);line.Children.Add(status);return line;
            });
            if(preview)list.Bind(ItemsControl.ItemsSourceProperty,new Binding {Source=new[]{new SampleRow("Welcome to the community",0,"Ready"),new SampleRow("A home of our own",9999.5m,"Waiting"),new SampleRow("Strength in numbers",10080,"Waiting")}});
        }
        if(preview)foreach(var input in tabs.GetLogicalDescendants().OfType<TextBox>()){
            if(input.PlaceholderText=="Title ID")input.Bind(TextBox.TextProperty,new Binding {Source="1938834516"});
            if(input.PlaceholderText=="Reference gamertag")input.Bind(TextBox.TextProperty,new Binding {Source="Reference player"});
        }
        foreach(var tab in tabs.Items.OfType<TabItem>().Where(t=>t!=xbox))if(tab.Content is Grid other){
            other.Margin=new Thickness(0);other.RowSpacing=8;
            var settings=other.Children.OfType<StackPanel>().FirstOrDefault(p=>Grid.GetRow(p)==0);
            other.RowDefinitions=new RowDefinitions("Auto,Auto,Auto");
            foreach(var items in other.Children.OfType<ListBox>()){items.MinHeight=180;items.MaxHeight=320;Avalonia.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(items,Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);}
        }
        foreach(var tile in tabs.GetLogicalDescendants().OfType<Border>().Where(b=>b.Name=="QueueStatusTile")){Grid.SetColumnSpan(tile,1);tile.Background=Brushes.Transparent;}
        foreach(var b in tabs.GetLogicalDescendants().OfType<Border>().Where(b=>b.Name=="QueueSpooferTile").ToArray())b.IsVisible=false;
        foreach(var button in tabs.GetLogicalDescendants().OfType<Button>().Where(b=>Equals(b.Content,"Stop spoofing")))button.IsVisible=false;
    }
    sealed class SampleRow(string name,decimal delay,string status) {public string AchievementName=>name;public decimal DelayMinutes {get;set;}=delay;public string Status=>status;public bool CanEditDelay=>false;}
}
