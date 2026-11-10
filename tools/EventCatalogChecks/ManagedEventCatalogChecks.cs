using System.Reflection;
using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

static class ManagedEventCatalogChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static bool Install(string path, string version, Func<string, string?> read)
    {
        try { return (bool)typeof(ManagedEventCatalog).GetMethod("Install", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path, version, read])!; }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "labs-managed-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var block = JObject.Parse("""{"Enabled":true,"Achievements":{"1":{"Id":{"ReplacementType":"Replace","Target":"REPLACEACH","Replacement":"1"}}}}""");
            var summary = (JObject)block.DeepClone(); summary.Remove("Achievements");
            var files = new Dictionary<string, string> {
                ["index.json"] = new JObject { ["7"] = summary, ["SupportedTitleIDs"] = new JArray("7") }.ToString(),
                ["7/Data.json"] = new JObject { ["7"] = block, ["SupportedTitleIDs"] = new JArray("7") }.ToString(),
                ["7/7.json"] = """{"data":{"baseData":{"titleId":"7","achievement":REPLACEACH,"userId":"REPLACEXUID"}}}""",
                ["Stats.json"] = """{"8":{"Stats":{"new":{"Replacement":"8"}}}}"""
            };
            string? Read(string name) => files.GetValueOrDefault(name);
            var target = Path.Combine(root, "Events");
            Directory.CreateDirectory(target);
            var legacy = """{"7":{"Stats":{"legacy":{"Replacement":"5"}}},"SupportedTitleIDs":["7"]}""";
            File.WriteAllText(Path.Combine(target, "Data.json"), legacy);
            File.WriteAllText(Path.Combine(target, "7.json"), "old template");
            File.WriteAllText(Path.Combine(target, "Stats.json"), """{"9":{"Stats":{"custom":{"Replacement":"9"}}}}""");
            Check(Install(target, "v1", Read), "Legacy folder refreshed");
            var backups = Directory.GetDirectories(root, "Events.backup-*");
            Check(backups.Length == 1 && File.ReadAllText(Path.Combine(backups[0], "Data.json")) == legacy, "Original backup is byte-identical");
            Check(!File.Exists(Path.Combine(target, "Data.json")) && !File.Exists(Path.Combine(target, "7.json")), "Flat catalog removed from active folder");
            var stats = JObject.Parse(File.ReadAllText(Path.Combine(target, "Stats.json")));
            Check(stats["7"]?["Stats"]?["legacy"] != null && stats["9"] != null && stats["8"] != null, "Legacy, custom and bundled stats retained");
            var bodies = await new EventCatalog(target).BuildPayloadsAsync("7", "1", "0", DateTime.UnixEpoch);
            Check(bodies.Count == 1 && JObject.Parse(bodies[0])["data"]?["baseData"]?["achievement"]?.ToString() == "1", "Installed catalog renders through consumer");
            Check(!Install(target, "v1", _ => throw new Exception("Unexpected rebuild")), "Repeat launch is a no-op");
            var current = File.ReadAllText(Path.Combine(target, "index.json"));
            files.Remove("7/7.json");
            try { Install(target, "v2", Read); throw new Exception("Missing template accepted"); } catch (InvalidDataException) { }
            Check(File.ReadAllText(Path.Combine(target, "index.json")) == current && File.ReadAllText(target + ".bundle.sha256") == "v1", "Failed update preserves active folder and receipt");
            files["7/7.json"] = """{"data":{"baseData":{"titleId":"7","achievement":REPLACEACH}}}""";
            var valid = files["7/Data.json"];
            var invalid = JObject.Parse(valid); invalid["7"]!["Achievements"]!["1"]!["Enabled"] = true;
            files["7/Data.json"] = invalid.ToString();
            try { Install(target, "v2", Read); throw new Exception("Invalid schema accepted"); } catch (InvalidDataException) { }
            Check(File.ReadAllText(Path.Combine(target, "index.json")) == current, "Actual consumer rejects scalar housekeeping before swap");
            files["7/Data.json"] = valid;
            Check(Install(target, "v2", Read) && Directory.GetDirectories(root, "Events.backup-*").Length == 2, "New bundle refreshes indexed folder and retains another backup");
            var fresh = Path.Combine(root, "Fresh");
            Check(Install(fresh, "v2", Read) && File.Exists(Path.Combine(fresh, "7", "Data.json")), "First installation creates title folders");
            Check(!ManagedEventCatalog.EnsureCurrent(Path.Combine(root, "Custom")), "Custom catalog path remains untouched");
            Check(!Directory.EnumerateDirectories(root, "*.staging-*").Any(), "Failed staging directories cleaned");
            Console.WriteLine("PASS: managed catalog first install, legacy and indexed upgrades, backup integrity, stats preservation, idempotence, failure recovery, strict consumer validation, offline rendering and custom-path isolation.");
        }
        finally { Directory.Delete(root, true); }
    }
}
