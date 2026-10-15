using Avalonia.Controls;
using Avalonia.LogicalTree;
namespace AchievementLabs.MultiSelect;
public static class EventTokenView
{
    public static void Attach(Window owner, object model)
    {
        var save = owner.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Content, "Save event token"));
        var input = owner.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => t.PlaceholderText == "Paste event token");
        if (input == null || save?.Parent is not Control actions || actions.Parent is not Panel panel) return;
        var mask = input.PasswordChar;
        var reveal = new CheckBox { Name = "ShowEventToken", Content = "Show event token", IsChecked = false };
        reveal.IsCheckedChanged += (_, _) => input.PasswordChar = reveal.IsChecked == true ? '\0' : mask;
        owner.Closed += (_, _) => input.PasswordChar = mask;
        panel.Children.Insert(panel.Children.IndexOf(actions), reveal);
    }
}
