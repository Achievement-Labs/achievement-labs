using Avalonia;
using System;

namespace AchievementLabs.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update") { Environment.ExitCode = AppUpdater.Apply(args[1]); return; }
        if (args.Contains("--visual-preview")) AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()

            .WithInterFont()
            .LogToTrace();
}
