using AchievementLabs.Desktop;
using System.Reflection;

public static class TitleStatusTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        using var model = new DesktopModel();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(DesktopModel);
        int Version() => (int)type.GetField("selectionVersion", flags)!.GetValue(model)!;
        void Report(int version, string title, string result) => type.GetMethod("ReportWin8Result", flags)!.Invoke(model, [version, title, "Synthetic achievement", result]);
        var a = new AchievementLabs.Desktop.Game("4294967293", "Synthetic A", "Windows8", 0, 1, 0);
        var b = new AchievementLabs.Desktop.Game("4294967294", "Synthetic B", "Windows8", 0, 1, 0);
        model.SelectGameAsync(a).GetAwaiter().GetResult();
        var first = Version();
        Report(first, a.Id, "HTTP 200");
        Assert(model.ActionReports.Count == 1 && model.ActionReports[0].Result.Contains("HTTP 200") && !model.Win8Status.Contains("HTTP 200"),
            "Current title keeps its result per achievement inside the Windows 8 panel");
        model.SelectGameAsync(b).GetAwaiter().GetResult();
        Assert(model.ActionReports.Count == 0 && !model.Win8Status.Contains("HTTP 200"), "Title switch clears reports and status");
        var status = model.Win8Status;
        Report(first, a.Id, "HTTP 404");
        Assert(model.ActionReports.Count == 0 && model.Win8Status == status, "Late result from A cannot update B");
        Report(Version(), b.Id, "HTTP 404");
        Assert(model.ActionReports.Count == 1 && model.ActionReports[0].Result.Contains("HTTP 404") && !model.Win8Status.Contains("HTTP 404"),
            "B receives only its own per-achievement result");
        model.SelectGameAsync(a).GetAwaiter().GetResult();
        Report(first, a.Id, "HTTP 200");
        Assert(model.ActionReports.Count == 0, "A to B to A rejects the previous A visit's late result");
        model.SelectGameAsync(b with { Platform = "XboxOne" }).GetAwaiter().GetResult();
        Assert(!model.IsWindows8Page && model.Win8Status == "" && !model.CanSendWin8, "Leaving Windows 8 resets its panel and actions");
        Assert(model.ActionSummary == "Load achievements from your account to enable actions.", "General action summary resets with selection");
        Console.WriteLine("PASS: title-specific results, reset on selection, late responses, A/B/A navigation and non-Windows-8 reset. No live requests.");
    }
}

