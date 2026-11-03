namespace AchievementLabs.Desktop;

// Prototype diagnostics for the overlay; written next to the app's other local files.
internal static class OverlayLog
{
    private static readonly string path = AchievementLabs.Core.AchievementLabsPaths.LocalFile("overlay.log");
    private static readonly object gate = new();

    public static void Write(string message)
    {
        try { lock (gate) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { }
    }
}
