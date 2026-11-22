using AchievementLabs.Core;
using AchievementLabs.Desktop.Workflows;
using Newtonsoft.Json.Linq;

internal static class SplitStatsCatalogChecks
{
    internal static async Task RunAsync()
    {
        const string id = "219630713";
        var root = Path.Combine(Path.GetTempPath(), "labs-split-stat-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var account = new NativeAccountContext { XAUTH = "synthetic", XUIDOnly = "0", EventsDirectory = root };
        var vm = new StatsEditorViewModel(new NativeNotices(_ => { }), account,
            _ => throw new Exception("Loading local split stats must not call an account API."), allowRemoteCatalog: false);
        var title = JObject.Parse("""{"StatsMode":"EventBased","Stats":{"LocalFixture":{"Request1":{"Data":{"ReplacementType":"Replace","Target":"REPLACEDATA","Replacement":"{}"}}}}}""");
        void Write(string folder, JObject block)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Stats.json"), new JObject { [id] = block, ["SupportedTitleIDs"] = new JArray(id) }.ToString());
            File.WriteAllText(Path.Combine(folder, id + ".json"), "local fixture template");
        }
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        try
        {
            // An explicit per-title source must win over an empty aggregate file.
            File.WriteAllText(Path.Combine(root, "Stats.json"), "{}");
            foreach (var folderName in new[] { id, id + " - Halo 5 Guardians" })
            {
                var folder = Path.Combine(root, folderName); Write(folder, (JObject)title.DeepClone());
                vm.TitleId = id; await vm.LoadStats();
                Check(vm.HasEditableStats && vm.StatItems.Single().Name == "LocalFixture", "Split title recipes did not override aggregate stats.");
                Check(EventCatalogFileLocator.Find(root, id, id + ".json") == Path.Combine(folder, id + ".json"), "Stat template came from another catalog.");
                Write(folder, new JObject { ["StatsMode"] = "EventBased", ["Stats"] = new JObject { ["MetadataOnly"] = new JObject { ["Type"] = "Integer" } } });
                vm.RefreshSupport(); Check(!vm.HasEditableEventData, "Metadata-only local definitions fell back to bundled recipes.");
                Write(folder, new JObject { ["Enabled"] = false, ["Stats"] = new JObject() });
                vm.RefreshSupport(); Check(!vm.HasEventData, "Disabled split definitions fell back to bundled recipes.");
                Directory.Delete(folder, true);
            }
            Write(Path.Combine(root, id + " - First"), (JObject)title.DeepClone());
            Write(Path.Combine(root, id + " - Second"), (JObject)title.DeepClone());
            vm.RefreshSupport();
            Check(!vm.HasEventData && vm.EventDataInfo.Contains("Multiple local folders"), "Ambiguous named folders were silently combined.");
            Console.WriteLine("PASS: numeric/named split stats and templates, local priority, metadata-only/disabled definitions and ambiguity errors. No network.");
        }
        finally { vm.ReleaseClient(); Directory.Delete(root, true); }
    }
}
