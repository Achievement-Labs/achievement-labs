using System.Reflection;
using AchievementLabs.Desktop;

public static class PlatformLibraryTests
{
    public static void Run()
    {
        var output = Path.Combine(Path.GetTempPath(), "labs-platform-api-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            // Exercise the exact adapters used by the app with their injected fixture handlers.
            foreach (var name in new[] { "AccountLibraryChecks", "PlatformAchievementChecks" })
            {
                var type = typeof(DesktopModel).Assembly.GetType("AchievementLabs.Desktop.Platforms." + name)!;
                var run = type.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                Task.Run(async () => await (Task)run.Invoke(null, new object[] { output })!).GetAwaiter().GetResult();
            }
            Console.WriteLine("PASS: owned-library pagination, account isolation, launcher verification, artwork decoding, achievement progress and Ubisoft definition/native-state fixtures. No live accounts or requests.");
        }
        finally { Directory.Delete(output, true); }
    }
}
