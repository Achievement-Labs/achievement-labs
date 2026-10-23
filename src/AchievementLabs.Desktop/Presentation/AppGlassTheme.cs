using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace AchievementLabs.Desktop.Presentation;

public static class AppGlassTheme
{
    static SolidColorBrush B(string color)=>new(Color.Parse(color));
    public static void Apply(Window owner)
    {
        var theme=AppearanceController.For(owner);
        owner.Resources["Panel"]=theme.Panel;
        owner.Resources["Line"]=theme.Line;
        owner.Resources["Ink"]=B("#E5EFF2");
        owner.Resources["Muted"]=B("#AFBEC8");
        owner.Styles.Add(new Style(x=>x.OfType<Border>().Class("panel")){Setters={new Setter(Border.BackgroundProperty,theme.Panel),new Setter(Border.BorderBrushProperty,theme.Line),new Setter(Border.CornerRadiusProperty,new CornerRadius(18))}});
        owner.Styles.Add(new Style(x=>x.OfType<Button>()){Setters={new Setter(Button.BackgroundProperty,theme.Button),new Setter(Button.BorderBrushProperty,theme.Line),new Setter(Button.BorderThicknessProperty,new Thickness(1)),new Setter(Button.CornerRadiusProperty,new CornerRadius(11))}});
        owner.Styles.Add(new Style(x=>x.OfType<Button>().Class("primary")){Setters={new Setter(Button.BackgroundProperty,B("#86D7AC")),new Setter(Button.ForegroundProperty,B("#0C2019")),new Setter(Button.BorderBrushProperty,B("#A0CDF3E1"))}});
        owner.Styles.Add(new Style(x=>x.OfType<TextBox>()){Setters={new Setter(TextBox.BackgroundProperty,theme.Input),new Setter(TextBox.BorderBrushProperty,theme.Line),new Setter(TextBox.CornerRadiusProperty,new CornerRadius(11))}});
        owner.Styles.Add(new Style(x=>x.OfType<ComboBox>()){Setters={new Setter(ComboBox.BackgroundProperty,theme.Input),new Setter(ComboBox.BorderBrushProperty,theme.Line),new Setter(ComboBox.CornerRadiusProperty,new CornerRadius(11))}});
        owner.Styles.Add(new Style(x=>x.OfType<NumericUpDown>()){Setters={new Setter(NumericUpDown.BackgroundProperty,theme.Input),new Setter(NumericUpDown.BorderBrushProperty,theme.Line),new Setter(NumericUpDown.CornerRadiusProperty,new CornerRadius(9))}});
        owner.Styles.Add(new Style(x=>x.OfType<Expander>()){Setters={new Setter(Expander.BackgroundProperty,theme.Panel),new Setter(Expander.BorderBrushProperty,theme.Line),new Setter(Expander.CornerRadiusProperty,new CornerRadius(12))}});
        owner.Styles.Add(new Style(x=>x.OfType<ListBox>()){Setters={new Setter(ListBox.BackgroundProperty,theme.Panel),new Setter(ListBox.BorderBrushProperty,theme.Line),new Setter(ListBox.CornerRadiusProperty,new CornerRadius(12))}});
        owner.Styles.Add(new Style(x=>x.OfType<ListBoxItem>()){Setters={new Setter(ListBoxItem.CornerRadiusProperty,new CornerRadius(9))}});
        owner.Styles.Add(new Style(x=>x.OfType<ListBoxItem>().Class(":selected")){Setters={new Setter(ListBoxItem.BackgroundProperty,B("#5053927D"))}});
        owner.Styles.Add(new Style(x=>x.OfType<TabControl>()){Setters={new Setter(TabControl.BackgroundProperty,Brushes.Transparent)}});
    }
}
