using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async Task<bool> PromptAppUpdateAsync(string version, bool ready)
    {
        if (!IsVisible) return false;
        var dialog = new Window {
            Name = "AppUpdatePrompt", Title = ready ? "Update ready" : "New update available",
            Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var accept = new Button { Name = "AcceptAppUpdate", Content = ready ? "Install and restart" : "Download update" };
        accept.Classes.Add("primary");
        var later = new Button { Name = "DeferAppUpdate", Content = "Later" };
        accept.Click += (_, _) => dialog.Close(true);
        later.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            new TextBlock { Text = "Achievement Labs " + version, FontSize = 22, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = ready
                ? "The update has been downloaded and verified. Save your work before installing. You can also install it later using the update button."
                : "A new version is available. Download it now to get the latest fixes and features. Your current version is " + AppUpdater.CurrentDisplayVersion + ".",
                TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Children = { later, accept } }
        } };
#if LABS_UPDATE_TESTING
        if (Environment.GetEnvironmentVariable("LABS_UPDATE_TEST_AUTOMATION") == "1")
            dialog.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(true));
#endif
        var accepted = await dialog.ShowDialog<bool>(this);
        if (ready && accepted && model.InstallUpdate()) Close();
        return accepted;
    }
}
