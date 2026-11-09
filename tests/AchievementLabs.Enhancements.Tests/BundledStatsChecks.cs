using AchievementLabs.Core;
using AchievementLabs.Desktop.Workflows;
using Newtonsoft.Json.Linq;

internal static class BundledStatsChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "labs-bundled-stats-" + Guid.NewGuid().ToString("N"));
        // Leave the Events directory absent: this is the packaged first-run fallback.
        var events = Path.Combine(root, "Events");
        var account = new NativeAccountContext { XAUTH = "synthetic", XUIDOnly = "123", EventsDirectory = events };
        var vm = new StatsEditorViewModel(new NativeNotices(_ => { }), account,
            _ => throw new Exception("Bundled event-stat loading must not make account API requests."), allowRemoteCatalog: false);
        try
        {
            var stats = JObject.Parse(BundledEventFiles.Read("Stats.json") ?? throw new Exception("Bundled Stats.json is missing."));
            Check(BundledEventFiles.Read("missing.json") == null, "Missing bundled file did not return null.");
            var loaded = 0;
            string? selected = null;
            foreach (var title in stats.Properties().Where(p => uint.TryParse(p.Name, out _) && p.Value is JObject))
            {
                if ((bool?)title.Value["Enabled"] == false) continue;
                if (!BundledEventFiles.Contains(title.Name + "/" + title.Name + ".json")) continue;
                vm.TitleId = title.Name; vm.RefreshSupport();
                if (vm.StatModeLabel.Contains("Title-based")) continue;
                if (!vm.HasEditableEventData) continue; // Metadata-only definitions remain non-editable.
                Check(vm.StatSupportLabel.StartsWith("Supported"), "Bundled template not found for " + title.Name);
                await vm.LoadStats();
                Check(vm.StatItems.Count > 0 && vm.StatItems.Any(s => s.IsEditable && s.IsEventBasedStat), "Bundled stats did not load for " + title.Name);
                Check(vm.StatItems.All(s => s.TitleId == title.Name && s.OwnerXuid == "123"), "Bundled rows have incorrect identity.");
                selected ??= title.Name; loaded++;
            }
            Check(loaded > 0 && selected != null, "No bundled event-stat definitions were exercised.");
            Check(!Directory.Exists(events), "Reading bundled stats created or modified the Events directory.");
            Directory.CreateDirectory(events);
            vm.TitleId = selected!;
            File.WriteAllText(Path.Combine(events, "Stats.json"), "{}");
            vm.RefreshSupport();
            Check(!vm.HasEventData, "An explicit local empty stats catalog fell back to bundled definitions.");
            File.WriteAllText(Path.Combine(events, "Stats.json"), new JObject { [selected!] = new JObject { ["Enabled"] = false } }.ToString());
            vm.RefreshSupport();
            Check(!vm.HasEventData, "A disabled local stats title was replaced by bundled definitions.");
            // Inspect the template selected by the actual editor: both local layouts retain priority.
            string? Template() => (string?)typeof(StatsEditorViewModel).GetMethod("ReadStatTemplate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
            var flat = Path.Combine(events, selected + ".json"); File.WriteAllText(flat, "local flat template");
            Check(Template() == "local flat template", "Flat local template lost priority.");
            File.WriteAllText(Path.Combine(events, "index.json"), "{\"SupportedTitleIDs\":[]}");
            Directory.CreateDirectory(Path.Combine(events, selected!));
            File.WriteAllText(Path.Combine(events, selected!, selected + ".json"), "local indexed template");
            Check(Template() == "local indexed template", "Indexed local template lost priority.");
            Console.WriteLine($"PASS: {loaded} bundled event-stat titles loaded without local Events or network requests; local catalog/disabled/flat/indexed overrides preserved. No telemetry sent.");
        }
        finally { vm.ReleaseClient(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
