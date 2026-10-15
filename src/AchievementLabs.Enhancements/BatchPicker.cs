using System.Collections;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace AchievementLabs.MultiSelect;

public static class BatchPicker
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object? Prop(object o, string name) => o.GetType().GetProperty(name, Flags)?.GetValue(o);
    static object? Field(object o, string name) => o.GetType().GetField(name, Flags)?.GetValue(o);
    static bool Yes(object? o) => o is true;
    static object? Game(object model) => Prop(model, "SelectedGame");
    static bool Legacy(object model, object game) => Yes(model.GetType().GetMethod("UsesLegacyEndpoint", Flags)?.Invoke(null, new[] { game }));
    public static bool CanOpen(object model) => Yes(Prop(model,"CanQuery")) && Yes(Field(model,"liveAchievements")) && Game(model) is object game && !Legacy(model, game);
    public static bool CanUnlockSelected(object model) => CanOpen(model) && Prop(model,"SelectedAchievement") is object row && !AchievementView.IsCollapsed(model,row) && IneligibleReason(model,row)==null;
    public static string? IneligibleReason(object model, object row)
    {
        string? id = Prop(row,"Id") as string;
        if (string.IsNullOrEmpty(id) || Field(model,"definitions") is not IDictionary definitions || !definitions.Contains(id)) return "No achievement definition";
        if (Yes(Field(model,"eventBased")))
        {
            if (!Yes(Field(model,"eventTitleSupported"))) return "Event template unavailable";
            if (Field(model,"mappedIds") is not IEnumerable mapped || !mapped.Cast<object>().Any(x => Equals(x,id))) return "No mapped event data";
        }
        else if (Yes(Prop(row,"Unlocked"))) return "Already unlocked (not event-based)";
        return null;
    }
    public static string[] EligibleIds(object model, IEnumerable rows) => rows.Cast<object>()
        .Where(row => IneligibleReason(model,row) == null).Select(row => (string)Prop(row,"Id")!).Distinct(StringComparer.Ordinal).ToArray();
    static void Summary(object model,string text) => model.GetType().GetProperty("ActionSummary",Flags)?.SetValue(model,text);

    public static void Attach(Window owner)
    {
        object? model = Field(owner,"model");
        if (model == null) return;
        try
        {
            PlaytimeView.Attach(owner,model);
            PresenceDiagnostics.Attach(owner,model);
            EventTokenView.Attach(owner,model);
            QueueTools.Attach(owner,model);
            Button? single = owner.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string)?.Trim() == "Unlock / retry");
            if (single?.Parent is not Panel panel) throw new InvalidOperationException("Achievement action panel was not found.");
            var multi = new Button { Content="Select multiple…", HorizontalAlignment=HorizontalAlignment.Stretch, Margin=new Thickness(0,8,0,0) };
            multi.Classes.Add("primary");
            ToolTip.SetTip(multi,"Choose achievements to unlock or retry in one batch.");
            panel.Children.Insert(panel.Children.IndexOf(single)+1,multi);
            AchievementView.Attach(owner,model,panel,multi);
            bool open=false;
            void Update() => multi.IsEnabled = !open && CanOpen(model);
            PropertyChangedEventHandler changed = (_,_) => Update();
            if (model is INotifyPropertyChanged npc) npc.PropertyChanged += changed;
            owner.Closed += (_,_) => { if (model is INotifyPropertyChanged n) n.PropertyChanged -= changed; };
            multi.Click += async (_,_) =>
            {
                if (open || !CanOpen(model)) return;
                open=true; Update();
                try { await ShowPicker(owner,model); }
                catch(Exception ex) { Summary(model,"Batch selection failed: " + (ex.InnerException?.Message ?? ex.Message)); }
                finally { open=false;Update(); }
            };
            Update();
        }
        catch(Exception ex) { Summary(model,"Multi-selection could not be added: " + ex.Message); }
    }

    static async Task ShowPicker(Window owner,object model)
    {
        object game = Game(model)!;
        object[] rows = ((Prop(model,"BatchAchievements") ?? Prop(model,"VisibleAchievements")) as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        string title = Prop(game,"Name")?.ToString() ?? "Current game";
        var dialog = new Window {
            Title="Select achievements — " + title, Width=700,Height=650,MinWidth=480,MinHeight=380,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, Background=new SolidColorBrush(Color.Parse("#111817")),
            RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark
        };
        var root=new DockPanel { Margin=new Thickness(20),LastChildFill=true };
        var header=new StackPanel { Spacing=8,Margin=new Thickness(0,0,0,12) };
        header.Children.Add(new TextBlock { Text=title,FontSize=22,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap });
        header.Children.Add(new TextBlock {Text="Choose from your currently filtered list. Selections stay checked when you change the pack filter. Requests run one by one.",TextWrapping=TextWrapping.Wrap,Opacity=0.75});
        var tools=new StackPanel { Orientation=Orientation.Horizontal,Spacing=10 };
        var all=new Button {Content="Select all eligible"}; var clear=new Button {Content="Clear"};
        tools.Children.Add(all);tools.Children.Add(clear);header.Children.Add(tools);
        var sorts=new ComboBox {Name="PickerNameSort",ItemsSource=AchievementView.Sorts,SelectedIndex=AchievementView.For(model).Sort,MinWidth=160};
        var packs=new ComboBox {Name="PickerPackFilter",ItemsSource=AchievementView.PacksFor(game),SelectedIndex=0,MinWidth=170,IsVisible=AchievementView.Supports(game)};
        var viewTools=new WrapPanel {Orientation=Orientation.Horizontal};
        viewTools.Children.Add(new TextBlock {Text="Sort: ",VerticalAlignment=VerticalAlignment.Center});viewTools.Children.Add(sorts);
        viewTools.Children.Add(packs);header.Children.Add(viewTools);
        DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var footer=new StackPanel { Spacing=8,Margin=new Thickness(0,12,0,0) };
        var count=new TextBlock();
        var error=new TextBlock { Foreground=Brushes.Orange,TextWrapping=TextWrapping.Wrap,IsVisible=false };
        var actions=new StackPanel { Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=new Button {Content="Cancel"};var submit=new Button {Content="Unlock / retry selected",IsEnabled=false};
        submit.Classes.Add("primary");actions.Children.Add(cancel);actions.Children.Add(submit);
        footer.Children.Add(count);footer.Children.Add(error);footer.Children.Add(actions);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var list=new StackPanel { Spacing=6 };var choices=new List<(CheckBox Check, object Row)>();
        int eligible=0;
        foreach(object row in rows)
        {
            string? reason=IneligibleReason(model,row);bool unlocked=Yes(Prop(row,"Unlocked"));
            var text=new StackPanel { Spacing=3 };
            text.Children.Add(new TextBlock {Text=Prop(row,"Name")?.ToString() ?? "Achievement",FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
            text.Children.Add(new TextBlock {Text=$"ID {Prop(row,"Id")} · {Prop(row,"Score")} G · " + (reason ?? (unlocked ? "Unlocked — resend event data" : "Locked")),Opacity=0.7,TextWrapping=TextWrapping.Wrap});
            var check=new CheckBox { Content=text,IsEnabled=reason==null,HorizontalAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(8),HorizontalContentAlignment=HorizontalAlignment.Stretch };
            if(reason==null)eligible++;
            choices.Add((check,row));list.Children.Add(check);
        }
        void UpdateCount()
        {
            int n=choices.Count(c=>c.Check.IsChecked==true && c.Check.IsEnabled);
            int shown=choices.Count(c=>c.Check.IsVisible);
            count.Text=$"{n} selected · {eligible} eligible · {shown} shown";
            submit.IsEnabled=n>0;
        }
        void Render()
        {
            list.Children.Clear();
            var ordered=AchievementView.Arrange(rows,AchievementView.Supports(game),Math.Max(0,sorts.SelectedIndex),packs.SelectedItem as string??"All packs",game);
            foreach(var c in choices)c.Check.IsVisible=ordered.Contains(c.Row);
            string? lastGroup=null;
            foreach(var row in ordered)
            {
                string group=AchievementView.Group(row,game);
                if(AchievementView.Supports(game) && group!=lastGroup)
                {
                    list.Children.Add(AchievementView.Divider(group));
                    lastGroup=group;
                }
                list.Children.Add(choices.First(c=>ReferenceEquals(c.Row,row)).Check);
            }
            UpdateCount();
        }
        sorts.SelectionChanged+=(_,_)=>Render();packs.SelectionChanged+=(_,_)=>Render();
        foreach(var choice in choices) choice.Check.IsCheckedChanged+=(_,_)=>UpdateCount();
        all.Click+=(_,_)=>{foreach(var c in choices) if(c.Check.IsEnabled && c.Check.IsVisible)c.Check.IsChecked=true;};
        clear.Click+=(_,_)=>{foreach(var c in choices)c.Check.IsChecked=false;};
        cancel.Click+=(_,_)=>dialog.Close(Array.Empty<string>());
        submit.Click+=(_,_)=>
        {
            if(!CanOpen(model) || !ReferenceEquals(Game(model),game)) { error.Text="The account or game changed. Close this window and reopen it.";error.IsVisible=true;return; }
            object[] selected=choices.Where(c=>c.Check.IsChecked==true && c.Check.IsEnabled).Select(c=>c.Row).ToArray();
            string[] ids=EligibleIds(model,selected);
            if(ids.Length==0) {error.Text="No eligible achievements are selected.";error.IsVisible=true;return;}
            dialog.Close(ids);
        };
        if(rows.Length==0)list.Children.Add(new TextBlock {Text="No achievements in this filter. Close this window and change the achievement filter.",TextWrapping=TextWrapping.Wrap});
        root.Children.Add(new ScrollViewer {Content=list,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled});
        dialog.Content=root;Render();
        string[]? picked=await dialog.ShowDialog<string[]>(owner);
        if(picked==null || picked.Length==0 || !CanOpen(model) || !ReferenceEquals(Game(model),game))return;
        MethodInfo submitMethod=model.GetType().GetMethod("SubmitAchievementsAsync",Flags) ?? throw new MissingMethodException("SubmitAchievementsAsync");
        await (Task)(submitMethod.Invoke(model,new object[]{picked}) ?? throw new InvalidOperationException("Submission did not start."));
    }
}
