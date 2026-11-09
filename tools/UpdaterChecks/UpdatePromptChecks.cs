namespace AchievementLabs.Desktop;

// Isolate the real update model from Xbox attachment, UI, and application launch.
public sealed partial class DesktopModel
{
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    public bool QueueActive => false;
    public bool WorkflowBusy => false;
    public bool PresenceRunning => false;
    public int PendingXbox360EditCount => 0;
    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null) { }
    internal DesktopModel(AppUpdater fixture) { updater = fixture; }
    internal void SimulateBusy() => busy = true;
}

static class UpdatePromptChecks
{
    public static async Task RunAsync(AppUpdater fixture)
    {
        var model = new DesktopModel(fixture) { AutomaticallyCheckUpdates = false };
        var prompts = new List<bool>();
        model.UpdatePrompt = (version, ready) => { prompts.Add(ready); return Task.FromResult(false); };
        await model.CheckForUpdatesAsync();
        if (prompts.Count != 0) throw new Exception("Background preference ignored.");
        await model.CheckForUpdatesAsync(startup: true);
        if (!prompts.SequenceEqual(new[] { false }) || model.UpdateReady) throw new Exception("Launch must notify before downloading, including old disabled preferences.");
        prompts.Clear();
        model.UpdatePrompt = (version, ready) => { prompts.Add(ready); return Task.FromResult(!ready); };
        await model.CheckForUpdatesAsync(manual: true);
        if (!prompts.SequenceEqual(new[] { false, true }) || !model.UpdateReady) throw new Exception("Download verification must precede install prompt.");
        await model.CheckForUpdatesAsync(manual: true);
        if (prompts.Count != 2) throw new Exception("Verified update prompted more than once.");
        model.SimulateBusy();
        if (model.InstallUpdate() || !model.UpdateStatus.Contains("finish active operations")) throw new Exception("Update must not restart during active work.");
        Console.WriteLine("PASS: launch notification, Later, manual retry, verified-download install prompt, duplicate prevention, and active-work restart protection. Synthetic HTTP only.");
    }
}
