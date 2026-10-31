using Avalonia.Controls;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private void ConfigurePlatformSidebar()
    {
        var names = new[] { "XboxSidebar", "SteamSidebar", "EpicSidebar", "UbisoftSidebar", "EaSidebar", "GogSidebar" };
        var groups = names.Select(n => this.FindControl<Expander>(n)).OfType<Expander>().ToArray();
        foreach (var group in groups)
        {
            group.Height = group.IsExpanded ? double.NaN : 36;
            group.MinHeight = 0;
            group.Margin = new Avalonia.Thickness(0);
            group.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;
                group.Height = group.IsExpanded ? double.NaN : 36;
                if (group.IsExpanded) foreach (var other in groups) if (other != group) other.IsExpanded = false;
            };
        }
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(model.PageTitle)) return;
            var name = model.IsSteam ? "SteamSidebar" : model.IsEpic ? "EpicSidebar" : model.IsUbisoft ? "UbisoftSidebar" : model.IsEa ? "EaSidebar" : model.IsGog ? "GogSidebar" : model.IsLibrary || model.IsAchievements || model.IsSpoofer || model.IsQueues ? "XboxSidebar" : "";
            foreach (var group in groups) group.IsExpanded = group.Name == name;
        };
    }
}
