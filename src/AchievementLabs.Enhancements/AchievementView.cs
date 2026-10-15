using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace AchievementLabs.MultiSelect;
public static class AchievementView
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static object? Prop(object? o,string key)=>o?.GetType().GetProperty(key,Flags)?.GetValue(o);
    public static string Normalize(string text)=>new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static readonly string[] Packs={"All packs", "Base game", "Onslaught", "Devastation", "Invasion", "Nemesis", "Unclassified"};
    public static readonly string[] Sorts={"Original order", "Name A–Z", "Name Z–A", "Global % low to high", "Global % high to low"};
    public static readonly Dictionary<string,string> Ghosts=new(StringComparer.Ordinal);
    static AchievementView()
    {
        Add("Base game", "Ghost Stories|Spatial Awareness|Brave New World|Liberty Wall|No Man's Land|Blimey O'Riley|Struck Down|Waste Not|Homecoming|Go Ugly Early|Legends Never Die|It Came from Below!|Federation Day|Sleeping Beauty|Carbon Faceprint|Birds of Prey|Burn Baby Burn|The Hunted|Jungle Ghosts|Clockwork|Deep Freeze|Atlas Falls|Grindin'|Piece of cake|Into the Deep|David & Goliath|End of the Line|Cog in the machine|Sin City|Jack-pot|All or nothing|End of your rope|Severed Ties|Fly-by-wire|Loki|They look like ants|The Ghost Killer|Tickets please|Audiophile|No Man Left|Sprinter|Made it Out Alive|Completionist|Cabin Fever|City Dweller|Any Means necessary|Trash Picker|Throttled Escape|Safeguard|You've earned it");
        Add("Onslaught", "Undiscovered Truths|Pushing Ahead|Weapon Facility|Survived Nightfall|Speed Slayer|Turnabout is Fair Play|Nightfall Completionist|Throttled Survival|Phantom Exterminator|Egg-stra XP!|Pea Shooter");
        Add("Devastation", "The Belly of the Beast|Come Up For Air|Survived Mayday|Upping the Ante|Mayday Completionist|Inquisitive Mind|Deforestation|Big Game Trapper|Egg-stra Devastation!|The Architect");
        Add("Invasion", "Targets Acquired|A Bridge To Somewhere|Escaped Awakening|Twice The Fun|Awakening Completionist|Spelunker|Dog Fight|Egg-stra Awakening!|Like A Glove|Well Rounded");
        Add("Nemesis", "Mass Exodus|Unstoppable|Nemesis Completionist|The Final Chapter|Postmaster|Eggstra Nemesis!|Timing is Everything|You Wish|Always Hard|Hat Trick");
    }
    static void Add(string pack,string names){foreach(var n in names.Split('|'))Ghosts.Add(Normalize(n),pack);}
    static SharedDlcCatalogue.Title? CatalogueTitle(object? game) => SharedDlcCatalogue.Current.Find(
        Prop(game,"Id")?.ToString() ?? "", Prop(game,"Platform")?.ToString() ?? "");
    // Keep legacy callers without platform metadata working; explicit mismatched platforms are rejected.
    static bool LegacyGhosts(object? game) => Prop(game,"Id")?.ToString()=="572802557" && string.IsNullOrEmpty(Prop(game,"Platform")?.ToString());
    public static bool Supports(object? game) => CatalogueTitle(game) != null || LegacyGhosts(game);
    public static string Group(object? row)=>Ghosts.TryGetValue(Normalize(Prop(row,"Name")?.ToString()??""),out var pack)?pack:"Unclassified";
    public static string Group(object? row, object? game) => CatalogueTitle(game) is { } title
        ? SharedDlcCatalogue.Group(title, Prop(row,"Id")?.ToString() ?? "", Prop(row,"Name")?.ToString() ?? "")
        : LegacyGhosts(game) ? Group(row) : "Unclassified";
    public static string[] PacksFor(object? game) => CatalogueTitle(game) is { } title
        ? new[] {"All packs"}.Concat(title.Packs.OrderBy(p => p.Kind == "base" ? 0 : 1).Select(p=>p.Name)).Append("Unclassified").ToArray()
        : Packs;
    public sealed class Settings
    {
        public int Sort; public string Pack = "All packs"; internal object? LastGame; internal string GameKey = "";
        public readonly Dictionary<string, HashSet<string>> CollapsedByGame = new(StringComparer.Ordinal);
        public HashSet<string> Collapsed => CollapsedByGame.TryGetValue(GameKey, out var groups) ? groups
            : CollapsedByGame[GameKey] = new(StringComparer.Ordinal);
    }
    static readonly ConditionalWeakTable<object,Settings> States=new();
    public static Settings For(object model)
    {
        var s=States.GetValue(model,_=>new Settings());object? game=Prop(model,"SelectedGame");
        var key = (Prop(game,"Id")?.ToString() ?? "") + "/" + (Prop(game,"Platform")?.ToString() ?? "");
        if(key != s.GameKey) { s.Pack="All packs"; s.GameKey=key; }
        s.LastGame=game;
        return s;
    }
    public static object[] Arrange(IEnumerable rows,bool grouped,int sort,string pack,object? game=null)
    {
        IEnumerable<object> result=rows.Cast<object>();
        string GetGroup(object row) => game == null ? Group(row) : Group(row,game);
        var packNames = game == null ? Packs : PacksFor(game);
        if(grouped && pack!="All packs")result=result.Where(r=>GetGroup(r)==pack);
        IOrderedEnumerable<object> ordered=result.OrderBy(r=>grouped?Array.IndexOf(packNames,GetGroup(r)):0);
        if(sort==1)ordered=ordered.ThenBy(r=>Prop(r,"Name")?.ToString(),StringComparer.CurrentCultureIgnoreCase);
        if(sort==2)ordered=ordered.ThenByDescending(r=>Prop(r,"Name")?.ToString(),StringComparer.CurrentCultureIgnoreCase);
        if(sort is 3 or 4)
        {
            ordered=ordered.ThenBy(r=>Prop(r,"GlobalUnlockPercentage") is not double);
            ordered=sort==3 ? ordered.ThenBy(r=>Prop(r,"GlobalUnlockPercentage") as double?)
                : ordered.ThenByDescending(r=>Prop(r,"GlobalUnlockPercentage") as double?);
            ordered=ordered.ThenBy(r=>Prop(r,"Name")?.ToString(),StringComparer.CurrentCultureIgnoreCase);
        }
        return ordered.ToArray();
    }
    public static Array Transform(Array rows,object model, bool respectCollapse = true)
    {
        var s=For(model);var selected=Arrange(rows,Supports(Prop(model,"SelectedGame")),s.Sort,s.Pack,Prop(model,"SelectedGame"));
        // Retain a real representative row so its section heading stays available when collapsed.
        // Searching temporarily expands sections, so matching achievements are never hidden.
        if (respectCollapse && Supports(Prop(model,"SelectedGame")) && string.IsNullOrEmpty(Prop(model,"Search")?.ToString()))
        {
            var retained = new HashSet<string>(StringComparer.Ordinal);
            selected = selected.Where(row => {
                var group = Group(row, Prop(model,"SelectedGame"));
                return !s.Collapsed.Contains(group) || retained.Add(group);
            }).ToArray();
        }
        Array typed=Array.CreateInstance(rows.GetType().GetElementType()!,selected.Length);
        for(int i=0;i<selected.Length;i++)typed.SetValue(selected[i],i);
        return typed;
    }
    public static bool IsCollapsed(object model, object row) => Supports(Prop(model,"SelectedGame")) &&
        string.IsNullOrEmpty(Prop(model,"Search")?.ToString()) &&
        For(model).Collapsed.Contains(Group(row,Prop(model,"SelectedGame")));
    public static bool StartsGroup(IEnumerable? rows,object row,object? game=null)
    {
        string? previous=null;
        if(rows is null)return false;
        foreach(object current in rows)
        {
            string group=game == null ? Group(current) : Group(current,game);
            if(ReferenceEquals(current,row))return previous!=group;
            previous=group;
        }
        return false;
    }
    public static Border Divider(string group)=>new Border {
        Name="AchievementPackDivider", BorderBrush=new SolidColorBrush(Color.Parse("#58DBA1")),
        BorderThickness=new Thickness(0,2,0,0),Margin=new Thickness(0,14,0,8),Padding=new Thickness(0,7,0,0),
        HorizontalAlignment=HorizontalAlignment.Stretch,
        Child=new TextBlock {Text=group,FontSize=14,FontWeight=FontWeight.SemiBold,Foreground=new SolidColorBrush(Color.Parse("#58DBA1"))}
    };
    public static void Attach(Window owner,object model,Panel panel,Button before)
    {
        var controls=new StackPanel { Spacing=5,Margin=new Thickness(0,12,0,10) };
        var sort=new ComboBox {Name="AchievementNameSort",ItemsSource=Sorts,SelectedIndex=For(model).Sort,HorizontalAlignment=HorizontalAlignment.Stretch};
        var packs=new ComboBox {Name="AchievementPackFilter",ItemsSource=Packs,SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var packLabel=new TextBlock {Text="BASE GAME / DLC",FontSize=11,Opacity=0.75};
        var info=new TextBlock {FontSize=11,Opacity=0.7,TextWrapping=TextWrapping.Wrap};
        var toolbar = owner.GetLogicalDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "AchievementSortToolbar");
        if (toolbar != null) { sort.MinWidth=150; toolbar.Children.Add(sort); }
        else { controls.Children.Add(new TextBlock{Text="SORT ACHIEVEMENTS",FontSize=11,Opacity=0.75}); controls.Children.Add(sort); }
        controls.Children.Add(packLabel);controls.Children.Add(packs);controls.Children.Add(info);
        panel.Children.Insert(panel.Children.IndexOf(before),controls);
        void Changed()=>model.GetType().GetMethod("Changed",Flags)?.Invoke(model,new object[]{"VisibleAchievements"});
        bool updating=false;
        sort.SelectionChanged+=(_,_)=>{if(updating)return;For(model).Sort=Math.Max(0,sort.SelectedIndex);Changed();};
        packs.SelectionChanged+=(_,_)=>{if(updating)return;For(model).Pack=packs.SelectedItem as string??"All packs";Changed();};
        void Update()
        {
            updating=true;
            bool grouped=Supports(Prop(model,"SelectedGame"));var s=For(model);
            packs.IsVisible=packLabel.IsVisible=grouped;
            var available=PacksFor(Prop(model,"SelectedGame"));
            if(packs.ItemsSource is not string[] old || !old.SequenceEqual(available)) packs.ItemsSource=available;
            if(!available.Contains(s.Pack))s.Pack="All packs";
            packs.SelectedItem=s.Pack;
            info.IsVisible=grouped;info.Text=s.Pack=="All packs"?string.Join(" → ",available.Where(p=>p is not ("All packs" or "Unclassified"))):"Showing "+s.Pack;
            updating=false;
        }
        if(model is System.ComponentModel.INotifyPropertyChanged npc)
        {
            System.ComponentModel.PropertyChangedEventHandler changed=(_,e)=>{if(e.PropertyName is "SelectedGame" or "VisibleAchievements" or "")Update();};
            npc.PropertyChanged+=changed;owner.Closed+=(_,_)=>npc.PropertyChanged-=changed;
        }
        // Keep real achievement rows selectable; draw a heading only at each visible pack boundary.
        var list=owner.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault(l=>l.Name=="AchievementLabsAchievementList");
        if(list?.ItemTemplate is IDataTemplate template)
        {
            list.ItemTemplate=new FuncDataTemplate<object>((row,_)=>
            {
                // Avalonia clears item content while recycling virtualized rows.
                if(row is null)return null;
                Control? original=template.Build(row);
                if(!Supports(Prop(model,"SelectedGame")))return original;
                var box=new StackPanel {Spacing=3};
                var group = Group(row, Prop(model,"SelectedGame"));
                var state = For(model);
                bool collapsed = state.Collapsed.Contains(group) && string.IsNullOrEmpty(Prop(model,"Search")?.ToString());
                var divider=Divider(group);divider.Tag=row;divider.IsVisible=StartsGroup(list.ItemsSource,row,Prop(model,"SelectedGame"));
                var toggle = new Button { Name="ToggleAchievementSection", Content=(collapsed ? "▸  " : "▾  ") + group,
                    HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Left,
                    Background=Brushes.Transparent, BorderThickness=new Thickness(0), Padding=new Thickness(0,4),
                    Foreground=new SolidColorBrush(Color.Parse("#58DBA1")), FontWeight=FontWeight.SemiBold };
                ToolTip.SetTip(toggle, (collapsed ? "Expand " : "Collapse ") + group);
                toggle.Click += (_,e) => {
                    e.Handled = true;
                    var current = For(model);
                    if (!current.Collapsed.Remove(group)) current.Collapsed.Add(group);
                    if (Prop(model,"SelectedAchievement") is { } selected && Group(selected,Prop(model,"SelectedGame")) == group)
                        model.GetType().GetProperty("SelectedAchievement",Flags)?.SetValue(model,null);
                    Changed();
                };
                divider.Child=toggle;box.Children.Add(divider);
                if(original!=null) { original.IsVisible=!collapsed; box.Children.Add(original); } return box;
            },false);
            list.PropertyChanged+=(_,e)=>
            {
                if(e.Property.Name!="ItemsSource")return;
                Avalonia.Threading.Dispatcher.UIThread.Post(()=> {
                    foreach(var divider in list.GetLogicalDescendants().OfType<Border>().Where(b=>b.Name=="AchievementPackDivider"))
                        divider.IsVisible=Supports(Prop(model,"SelectedGame")) && divider.Tag is object row && StartsGroup(list.ItemsSource,row,Prop(model,"SelectedGame"));
                });
            };
        }
        Update();
    }
}
