using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AchievementLabs.Desktop.Presentation;

using Image = Avalonia.Controls.Image;

public sealed class AppearanceController : IDisposable
{
    static readonly ConditionalWeakTable<Window,AppearanceController> Instances=new();
    public static AppearanceController For(Window owner)=>Instances.GetValue(owner,w=>new(w,Environment.GetCommandLineArgs().Contains("--visual-preview") || (AppContext.TryGetSwitch("AchievementLabs.OfflineChecks",out var offline) && offline)));
    readonly Window owner;readonly string file,cache;readonly bool preview;
    public AppearancePreferences Preferences {get;private set;}
    public SolidColorBrush Panel {get;}=new();public SolidColorBrush Input {get;}=new();public SolidColorBrush Button {get;}=new();public SolidColorBrush Line {get;}=new();public SolidColorBrush Header {get;}=new();public SolidColorBrush Sidebar {get;}=new();
    public event Action? Changed;
    readonly List<(Border Frame,Image Blur,Border Shade,Border Outline)> glass=new();
    readonly List<(AvaloniaObject Control,AvaloniaProperty<IBrush?> Property,IBrush? Original)> surfaces=new();
    Image? target,backdrop,sidebar,gameArt;Grid? background;Bitmap? remembered,custom;bool combined,oledApplied,disposed;
    TextBlock? status;
    AppearanceController(Window owner,bool preview)
    {
        this.owner=owner;this.preview=preview;
        var dir=preview?Path.Combine(AppContext.BaseDirectory,"preview-profile"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AchievementLabs");
        file=Path.Combine(dir,"visual-appearance.json");cache=Path.Combine(dir,"last-game-background.png");
        Preferences=AppearancePreferences.Load(file);
        if(Preferences.PersistentGameArtwork && File.Exists(cache))try{remembered=new Bitmap(cache);}catch{}
        LoadCustom();Apply();owner.Closed+=(_,_)=>Dispose();
    }
    static Color ColorWithAlpha(string hex,double opacity){var c=Color.Parse(hex);return Color.FromArgb((byte)Math.Round(255*Math.Clamp(opacity,0,1)),c.R,c.G,c.B);}
    public void RegisterGlass(Border frame,Image blur,Border shade,Border outline){glass.Add((frame,blur,shade,outline));Apply();}
    public void Configure(Grid background,Image gameArt,Image target,Image backdrop,Image sidebar,IEnumerable<Control> original)
    {
        this.background=background;this.gameArt=gameArt;this.target=target;this.backdrop=backdrop;this.sidebar=sidebar;
        foreach(var c in original.Distinct()){
            if(c is Border b && b.Background!=null && !b.Classes.Contains("panel"))surfaces.Add((b,Border.BackgroundProperty,b.Background));
            else if(c is Avalonia.Controls.Panel p && p.Background!=null)surfaces.Add((p,Avalonia.Controls.Panel.BackgroundProperty,p.Background));
        }
        gameArt.PropertyChanged+=ArtworkChanged;Apply();
    }
    void ArtworkChanged(object? sender,AvaloniaPropertyChangedEventArgs e)
    {
        if(e.Property!=Image.SourceProperty || gameArt?.Source is not Bitmap bitmap)return;
        try{
            using var bytes=new MemoryStream();bitmap.Save(bytes,new PngBitmapEncoderOptions());bytes.Position=0;var next=new Bitmap(bytes);var old=remembered;remembered=next;
            if(Preferences.PersistentGameArtwork){Directory.CreateDirectory(Path.GetDirectoryName(cache)!);File.WriteAllBytes(cache,bytes.ToArray());}
            Apply();old?.Dispose();
        }catch{status?.SetCurrentValue(TextBlock.TextProperty,"Could not remember this game's artwork.");}
    }
    public void SetPage(bool isCombined){combined=isCombined;Apply();}
    void LoadCustom()
    {
        var old=custom;custom=null;
        if(File.Exists(Preferences.CustomBackgroundPath))try{custom=new Bitmap(Preferences.CustomBackgroundPath);}catch{}
        if(target!=null)target.Source=null;old?.Dispose();
    }
    public void Apply()
    {
        Preferences.Normalize();bool oled=Preferences.OledMode;double opacity=1-Preferences.GlassTransparency/100;
        Panel.Color=oled?Colors.Black:ColorWithAlpha(Preferences.GlassColour,opacity);
        Input.Color=oled?Colors.Black:ColorWithAlpha(Preferences.GlassColour,Math.Min(.95,opacity+.13));
        Button.Color=oled?Colors.Black:ColorWithAlpha(Preferences.GlassColour,Math.Min(.98,opacity+.1));
        Header.Color=oled?Colors.Black:ColorWithAlpha(Preferences.GlassColour,opacity);
        Sidebar.Color=oled?Colors.Black:ColorWithAlpha(Preferences.GlassColour,Math.Min(.95,opacity+.12));
        Line.Color=oled?Color.Parse("#282828"):Color.Parse("#456A8391");
        foreach(var g in glass){g.Frame.Background=oled?Brushes.Black:Brushes.Transparent;g.Shade.Background=Panel;g.Blur.IsVisible=!oled;g.Blur.Effect=Preferences.GlassBlur>0?new BlurEffect {Radius=Preferences.GlassBlur}:null;g.Outline.BorderBrush=Line;}
        if(sidebar!=null)sidebar.Effect=Preferences.GlassBlur>0?new BlurEffect {Radius=Preferences.GlassBlur}:null;
        if(oled!=oledApplied){foreach(var s in surfaces)s.Control.SetCurrentValue(s.Property,oled?Brushes.Black:s.Original);oledApplied=oled;}
        if(background!=null){background.Background=oled?Brushes.Black:new LinearGradientBrush {StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,1,RelativeUnit.Relative),GradientStops=new GradientStops {new GradientStop(Color.Parse("#102330"),0),new GradientStop(Color.Parse("#162C30"),.55),new GradientStop(Color.Parse("#0B111E"),1)}};}
        if(background?.Children.OfType<Border>().FirstOrDefault(b=>b.Name=="BackgroundTint") is Border tint)tint.Background=oled?Brushes.Black:new SolidColorBrush(Color.Parse("#40101A22"));
        if(target!=null){target.Source=Preferences.BackgroundMode=="Custom image"?custom:Preferences.BackgroundMode=="Game artwork"?remembered:null;
            bool visible=!oled && target.Source!=null && (Preferences.BackgroundMode!="Game artwork" || combined || Preferences.PersistentGameArtwork);
            target.IsVisible=visible;target.Opacity=combined?1:.28;
            if(backdrop!=null)backdrop.IsVisible=visible;if(sidebar!=null)sidebar.IsVisible=visible;
        }
        Changed?.Invoke();
    }
    public void Save(){try{if(Preferences.PersistentGameArtwork && remembered!=null){Directory.CreateDirectory(Path.GetDirectoryName(cache)!);var temp=cache+".tmp";remembered.Save(temp,new PngBitmapEncoderOptions());File.Move(temp,cache,true);}Preferences.Save(file);status?.SetCurrentValue(TextBlock.TextProperty,"Appearance saved.");}catch{status?.SetCurrentValue(TextBlock.TextProperty,"Could not save appearance settings. Please try again.");}}
    public Control BuildSettings()
    {
        var content=new StackPanel {Spacing=14,MaxWidth=850,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(18)};
        content.Children.Add(new TextBlock {Text="Background & glass",FontSize=24,FontWeight=FontWeight.SemiBold});
        content.Children.Add(new TextBlock {Text="Preview changes immediately. Save appearance or Save all settings to remember them.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray});
        var mode=new ComboBox {ItemsSource=new[]{"Game artwork","Custom image","No artwork"},SelectedItem=Preferences.BackgroundMode,MinWidth=260};
        content.Children.Add(new TextBlock {Text="Background"});content.Children.Add(mode);
        var persistent=new CheckBox {Content="Keep game artwork on every page and remember it between launches",IsChecked=Preferences.PersistentGameArtwork};content.Children.Add(persistent);
        var path=new TextBox {Text=Preferences.CustomBackgroundPath,PlaceholderText="Choose a custom background image",IsReadOnly=true,MinWidth=380};
        var browse=new Avalonia.Controls.Button {Content="Choose image…"};var customRow=new StackPanel {Spacing=8};customRow.Children.Add(path);customRow.Children.Add(browse);content.Children.Add(customRow);
        var percent=new TextBlock {Text=$"Glass transparency: {Preferences.GlassTransparency:0}%"};content.Children.Add(percent);
        var slider=new Slider {Minimum=0,Maximum=85,Value=Preferences.GlassTransparency,MinWidth=380,TickFrequency=5,IsSnapToTickEnabled=true};content.Children.Add(slider);
        var blurLabel=new TextBlock {Text=$"Glass blur: {Preferences.GlassBlur:0}"};content.Children.Add(blurLabel);
        var blurSlider=new Slider {Name="GlassBlurSlider",Minimum=0,Maximum=80,Value=Preferences.GlassBlur,MinWidth=380,TickFrequency=1,IsSnapToTickEnabled=true};content.Children.Add(blurSlider);
        content.Children.Add(new TextBlock {Text="Glass colour"});
        var colourRow=new WrapPanel {Orientation=Orientation.Horizontal};
        var colourInput=new TextBox {Name="GlassColourInput",Text=Preferences.GlassColour,PlaceholderText="#111E27",Width=130,Margin=new Thickness(0,0,10,0)};
        colourRow.Children.Add(colourInput);
        foreach(var hex in new[]{"#111E27","#182A24","#172843","#30223F","#3B242C","#242424"}){
            var swatch=new Avalonia.Controls.Button {Width=32,Height=32,Background=new SolidColorBrush(Color.Parse(hex)),BorderBrush=Brushes.Gray,BorderThickness=new Thickness(1),Margin=new Thickness(0,0,6,0)};ToolTip.SetTip(swatch,hex);swatch.Click+=(_,_)=>colourInput.Text=hex;colourRow.Children.Add(swatch);
        }
        content.Children.Add(colourRow);
        var colourHint=new TextBlock {Text="Choose a swatch or enter a colour as #RRGGBB.",FontSize=12};content.Children.Add(colourHint);
        var oled=new CheckBox {Content="Pure black OLED mode",IsChecked=Preferences.OledMode};content.Children.Add(oled);
        content.Children.Add(new TextBlock {Text="OLED mode hides background artwork and uses opaque black surfaces. Turn it off to restore your background and glass settings.",TextWrapping=TextWrapping.Wrap,FontSize=12});
        var actions=new WrapPanel();var save=new Avalonia.Controls.Button {Content="Save appearance",Margin=new Thickness(0,0,10,0)};save.Classes.Add("primary");var reset=new Avalonia.Controls.Button {Content="Reset appearance"};actions.Children.Add(save);actions.Children.Add(reset);content.Children.Add(actions);
        status=new TextBlock {TextWrapping=TextWrapping.Wrap};content.Children.Add(status);
        void UpdateControls(){persistent.IsEnabled=mode.SelectedItem?.ToString()=="Game artwork";customRow.IsVisible=mode.SelectedItem?.ToString()=="Custom image";slider.IsEnabled=blurSlider.IsEnabled=colourRow.IsEnabled=oled.IsChecked!=true;}
        void Preview(){Apply();UpdateControls();status.Text="Previewing changes. Save appearance to remember them.";}
        mode.SelectionChanged+=(_,_)=>{Preferences.BackgroundMode=mode.SelectedItem?.ToString()??"Game artwork";Preview();};
        persistent.IsCheckedChanged+=(_,_)=>{Preferences.PersistentGameArtwork=persistent.IsChecked==true;Preview();};
        slider.PropertyChanged+=(_,e)=>{if(e.Property==RangeBase.ValueProperty){Preferences.GlassTransparency=slider.Value;percent.Text=$"Glass transparency: {slider.Value:0}%";Preview();}};
        blurSlider.PropertyChanged+=(_,e)=>{if(e.Property==RangeBase.ValueProperty){Preferences.GlassBlur=blurSlider.Value;blurLabel.Text=$"Glass blur: {blurSlider.Value:0}";Preview();}};
        colourInput.TextChanged+=(_,_)=>{var value=colourInput.Text??"";if(System.Text.RegularExpressions.Regex.IsMatch(value,"^#[0-9a-fA-F]{6}$")){Preferences.GlassColour=value;colourHint.Text="Choose a swatch or enter a colour as #RRGGBB.";Preview();}else colourHint.Text="Enter # followed by six hexadecimal digits (for example #172843).";};
        oled.IsCheckedChanged+=(_,_)=>{Preferences.OledMode=oled.IsChecked==true;Preview();};
        browse.Click+=async (_,_)=>{
            var files=await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="Choose a background image",AllowMultiple=false,FileTypeFilter=new[]{new FilePickerFileType("Images") {Patterns=new[]{"*.png","*.jpg","*.jpeg","*.webp","*.bmp"}}}});
            var selected=files.FirstOrDefault()?.TryGetLocalPath();if(selected==null)return;
            Preferences.CustomBackgroundPath=selected;path.Text=selected;LoadCustom();Preview();if(custom==null)status.Text="This image could not be loaded. Choose another image.";
        };
        save.Click+=(_,_)=>Save();reset.Click+=(_,_)=>{Preferences=new();LoadCustom();mode.SelectedItem=Preferences.BackgroundMode;persistent.IsChecked=Preferences.PersistentGameArtwork;slider.Value=Preferences.GlassTransparency;blurSlider.Value=Preferences.GlassBlur;colourInput.Text=Preferences.GlassColour;oled.IsChecked=Preferences.OledMode;path.Text="";Preview();};
        UpdateControls();return new ScrollViewer {Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
    }
    public void SetCustomForPreview(string path){Preferences.CustomBackgroundPath=path;Preferences.BackgroundMode="Custom image";LoadCustom();Apply();}
    public void Dispose(){if(disposed)return;disposed=true;if(gameArt!=null)gameArt.PropertyChanged-=ArtworkChanged;if(target!=null)target.Source=null;remembered?.Dispose();custom?.Dispose();}
}
