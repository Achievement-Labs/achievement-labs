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
        if (args.Length == 2 && args[0] == "--apply-update") { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This updater requires Windows."); Environment.ExitCode = AppUpdater.Apply(args[1]); return; }
        if (args.Length == 3 && args[0] == "--achievement-read-check" && args[1] is "EA" or "Epic" or "GOG" or "Ubisoft")
        {
            Environment.ExitCode = Platforms.LauncherSession.CheckAchievementsAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 3 && args[0] == "--launcher-session-check" && args[1] is "EA" or "Epic" or "Ubisoft" or "GOG")
        {
            Environment.ExitCode = Platforms.LauncherSession.CheckAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 2 && args[0] == "--gog-session-check")
        {
            Environment.ExitCode = Platforms.LauncherSession.CheckGalaxyAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        MacStartup.Begin();
        try
        {
            if (args.Contains("--mac-smoke-test") || args.Contains("--linux-smoke-test")) AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
            ConfigureStartup(args);
            MacStartup.Stage("Creating desktop lifetime");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error) when (OperatingSystem.IsMacOS())
        {
            MacStartup.Failed(error);Environment.ExitCode=1;
        }
    }

    internal static void ConfigureStartup(string[] args)
    {
        // Preview isolation is opt-in; ordinary launches must attach to Xbox normally.
        if (args.Contains("--xbox360-local-test") || args.Contains("--local-360-smoke-test"))
        {
            AppContext.SetSwitch("AchievementLabs.StartOnXbox360", true);
            AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        }
        if (args.Contains("--visual-preview") || args.Contains("--steam-ui-check") || args.Contains("--platform-ui-check")) AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()

            .WithInterFont()
            .LogToTrace();
}
