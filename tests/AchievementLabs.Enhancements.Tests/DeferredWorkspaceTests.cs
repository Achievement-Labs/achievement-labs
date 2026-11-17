using AchievementLabs.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;

static class DeferredWorkspaceTests
{
    internal static void Run()
    {
        AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var model = (DesktopModel)window.DataContext!;
        var workspaces = window.GetLogicalDescendants().OfType<DeferredWorkspace>().ToArray();
        if (workspaces.Length != 7 || workspaces.Any(workspace => workspace.Content != null))
            throw new Exception("Home startup constructed a deferred workspace.");

        foreach (var page in new[] { "Mirror", "Stats", "Diagnostics", "Settings", "About", "SteamQueue", "Gfwl" })
        {
            model.Navigate(page);
            Dispatcher.UIThread.RunJobs();
            var workspace = workspaces.Single(workspace => workspace.IsActive);
            var content = workspace.Content as Control ?? throw new Exception(page + " did not create a view.");
            if (content.DataContext != model || !content.GetLogicalDescendants().OfType<TextBlock>().Any())
                throw new Exception(page + " did not attach its controls and model.");

            model.Navigate("Home");
            model.GoBack();
            Dispatcher.UIThread.RunJobs();
            if (!workspace.IsActive || !ReferenceEquals(workspace.Content, content))
                throw new Exception(page + " did not keep its view when navigating back.");
            model.GoForward();
        }
        window.Close();
        Console.WriteLine("PASS: Home defers secondary workspaces, first navigation builds their controls, and back/forward reuses them.");
    }
}
