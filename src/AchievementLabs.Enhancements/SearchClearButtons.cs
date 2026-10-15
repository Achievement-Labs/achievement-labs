using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace AchievementLabs.MultiSelect;

public static class SearchClearButtons
{
    public static void Attach(Window owner)
    {
        foreach (var box in owner.GetLogicalDescendants().OfType<TextBox>())
        {
            var hint = box.GetType().GetProperty("PlaceholderText")?.GetValue(box)?.ToString()
                ?? box.GetType().GetProperty("Watermark")?.GetValue(box)?.ToString() ?? "";
            if (!hint.Contains("search", StringComparison.OrdinalIgnoreCase) &&
                !hint.Contains("filter", StringComparison.OrdinalIgnoreCase) && hint != "Gamertag") continue;
            var slot = box.GetType().GetProperty("InnerRightContent");
            if (slot?.CanWrite != true || slot.GetValue(box) != null) continue;
            var clear = new Button { Name = "ClearSearch", Content = new Avalonia.Controls.Shapes.Path {
                    Data = Avalonia.Media.Geometry.Parse("M 1,1 L 11,11 M 11,1 L 1,11"),
                    Width = 12, Height = 12, StrokeThickness = 1.5,
                    Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D7DCE2")),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, Width = 28, Height = 28,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Padding = new Avalonia.Thickness(0), Margin = new Avalonia.Thickness(0, 0, 4, 0),
                IsVisible = !string.IsNullOrEmpty(box.Text) };
            ToolTip.SetTip(clear, "Clear search");
            clear.Click += (_, _) => { box.Text = ""; box.Focus(); };
            box.TextChanged += (_, _) => clear.IsVisible = !string.IsNullOrEmpty(box.Text);
            slot.SetValue(box, clear);
        }
    }
}
