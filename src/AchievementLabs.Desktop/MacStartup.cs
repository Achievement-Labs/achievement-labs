using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
namespace AchievementLabs.Desktop;

/// <summary>Startup stages and exception types only; never account data or exception messages.</summary>
internal static class MacStartup
{
    private static string? log;
    internal static void Begin()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Logs","AchievementLabs");
            Directory.CreateDirectory(dir);log=Path.Combine(dir,"startup.log");
            File.WriteAllText(log,$"Achievement Labs {Assembly.GetExecutingAssembly().GetName().Version}\nRuntime: {Environment.Version}; architecture: {RuntimeInformation.ProcessArchitecture}\n");
            Stage("Starting");
        }
        catch { log=null; }
    }
    internal static void Stage(string stage)
    {
        if (log==null) return;
        try { File.AppendAllText(log,DateTimeOffset.UtcNow.ToString("O")+" "+stage+"\n"); } catch { }
    }
    internal static void Failed(Exception error)
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Exception messages can contain server payloads, paths or credentials. Do not record them.
        for (var e=error;e!=null;e=e.InnerException) Stage("Failure type: "+e.GetType().FullName);
        Stage("Startup did not finish");
        try
        {
            var start=new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute=false };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("display alert \"Achievement Labs could not start\" message \"A startup report is available in Library/Logs/AchievementLabs/startup.log in your home folder. It contains startup stages and error types, not account or token data.\" as critical");
            Process.Start(start);
        }
        catch { }
    }
}
