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
        ConfigureStartup(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static void ConfigureStartup(string[] args)
    {
        // Preview isolation is opt-in; ordinary launches must attach to Xbox normally.
        if (args.Contains("--xbox360-local-test") || args.Contains("--local-360-smoke-test"))
        {
            AppContext.SetSwitch("AchievementLabs.StartOnXbox360", true);
            AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        }
        if (args.Contains("--visual-preview")) AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()

            .WithInterFont()
            .LogToTrace();
}
