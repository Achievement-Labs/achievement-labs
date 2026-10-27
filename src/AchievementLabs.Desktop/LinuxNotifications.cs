using System.Diagnostics;
namespace AchievementLabs.Desktop;

internal static class LinuxNotifications
{
    public static async Task<bool> ShowAsync(string title, string body)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            using var process = new Process { StartInfo = new("/usr/bin/notify-send") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add("--app-name=Achievement Labs"); process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(title); process.StartInfo.ArgumentList.Add(body);
            process.Start();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); return process.ExitCode == 0; }
            finally { if (!process.HasExited) process.Kill(); }
        }
        catch { return false; }
    }
}
