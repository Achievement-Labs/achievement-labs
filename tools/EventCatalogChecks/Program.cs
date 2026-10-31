using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception e) when (e is InvalidDataException or IOException or Newtonsoft.Json.JsonException) { return; }
    throw new Exception("Expected rejection: " + message);
}
static JObject Block() => JObject.Parse("""{"Enabled":true,"FullySupported":false,"Achievements":{"1":{"Request1":{"Id":{"ReplacementType":"Replace","Target":"REPLACEACH","Replacement":"1"}},"Request2":{"Id":{"ReplacementType":"Replace","Target":"REPLACEACH","Replacement":"2"}}}}}""");
static string Template(string id) => """{"name":"Microsoft.XboxLive.TID.Test","data":{"baseData":{"titleId":"ID","name":"Test","serviceConfigId":"11111111-2222-3333-4444-555555555555","userId":"REPLACEXUID","achievement":REPLACEACH}}}""".Replace("TID", "T" + id).Replace("\"ID\"", "\"" + id + "\"");
var temporary = Path.Combine(Path.GetTempPath(), "labs-catalog-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var flat = Path.Combine(temporary, "flat"); var split = Path.Combine(temporary, "split");
    Directory.CreateDirectory(flat); Directory.CreateDirectory(Path.Combine(split, "7"));
    var block = Block(); var data = new JObject { ["7"] = block.DeepClone(), ["SupportedTitleIDs"] = new JArray("7") };
    await File.WriteAllTextAsync(Path.Combine(flat, "Data.json"), data.ToString());
    await File.WriteAllTextAsync(Path.Combine(flat, "7.json"), Template("7"));
    await File.WriteAllTextAsync(Path.Combine(split, "7", "Data.json"), data.ToString());
    await File.WriteAllTextAsync(Path.Combine(split, "7", "7.json"), Template("7"));
    var summary = (JObject)block.DeepClone(); summary.Remove("Achievements");
    var index = new JObject { ["7"] = summary, ["9"] = new JObject(), ["SupportedTitleIDs"] = new JArray("7") };
    await File.WriteAllTextAsync(Path.Combine(split, "index.json"), index.ToString());
    var legacy = new EventCatalog(flat); var indexed = new EventCatalog(split);
    Check((await indexed.ReadIndexAsync())["7"]?["Achievements"] == null, "Index contains no mappings");
    var oldBodies = await legacy.BuildPayloadsAsync("7", "1", "0", DateTime.UnixEpoch);
    var newBodies = await indexed.BuildPayloadsAsync("7", "1", "0", DateTime.UnixEpoch);
    Check(oldBodies.SequenceEqual(newBodies) && newBodies.Count == 2, "Ordered multi-request payloads survive migration");
    await File.WriteAllTextAsync(Path.Combine(flat, "Data.json"), new JObject { ["SupportedTitleIDs"] = new JArray("7"), ["7"] = block.DeepClone() }.ToString());
    Check(!(await CatalogInspector.InspectAsync(flat)).SupportedLast, "Inspector reports legacy ordering errors without throwing");
    // A missing unrelated title file must not prevent reading or using a valid selected title.
    await Reject(async () => { await indexed.ReadTitleAsync("9"); }, "Missing selected title");
    await Reject(async () => { await indexed.ReadTitleAsync("../7"); }, "Path traversal");
    var inspection = await CatalogInspector.InspectAsync(split);
    Check(inspection.Findings.Any(f => f.TitleId == "9") && inspection.Findings.All(f => f.TitleId != "7"), "Inspection reports isolated title failures");
    using (var local = new EventCatalogClient(true, () => split))
    {
        Check((await local.GetTitleAsync("7", default)).AchievementIds.SequenceEqual(new[] { "1" }), "Local indexed route validates");
        var changed = (JObject)data.DeepClone();
        changed["7"]!["Achievements"]!["2"] = changed["7"]!["Achievements"]!["1"]!.DeepClone();
        await File.WriteAllTextAsync(Path.Combine(split, "7", "Data.json"), changed.ToString());
        Check((await local.GetTitleAsync("7", default)).AchievementIds.Length == 2, "Per-title edits invalidate validation cache without rewriting index");
    }
    async Task Bad(JObject value, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(split, "7", "Data.json"), value.ToString());
        await Reject(async () => { await new EventCatalog(split).ReadTitleAsync("7"); }, message);
    }
    var bad = (JObject)data.DeepClone(); bad["7"]!["Achievements"]!["1"]!["Enabled"] = true; await Bad(bad, "Scalar achievement housekeeping");
    bad = (JObject)data.DeepClone(); bad["7"]!["ResearchStatus"] = "research"; await Bad(bad, "Research title metadata");
    bad = (JObject)data.DeepClone(); bad["7"]!["Achievements"]!["1"]!["Request1"]!["Id"]!["ReplacementType"] = "RawBody"; await Bad(bad, "Unsupported raw payload operation");
    bad = (JObject)data.DeepClone(); bad["SupportedTitleIDs"] = new JArray("8"); await Bad(bad, "Wrong title identity");
    await File.WriteAllTextAsync(Path.Combine(split, "7", "Data.json"), "{\"7\":{},\"7\":{},\"SupportedTitleIDs\":[\"7\"]}");
    await Reject(async () => { await new EventCatalog(split).ReadTitleAsync("7"); }, "Duplicate title keys");
    using (var bundled = new EventCatalogClient(true))
    {
        Check(bundled.BundledTitleIds.Contains("572802557"), "Nested embedded index loaded");
        var mapping = await bundled.GetTitleAsync("572802557", default);
        Check(mapping.AchievementIds.Length == 91, "Nested embedded mappings and template loaded");
    }
    UpstreamEventCatalogueTests.Run();
    Console.WriteLine("PASS: legacy and indexed catalogs, lazy loading, cache refresh, schema rejection, embedded resources and archive compatibility. No live requests.");
}
finally { Directory.Delete(temporary, true); }

if (args.Length == 2)
{
    var original = new EventCatalog(args[0]); var migrated = new EventCatalog(args[1]);
    var before = await original.ReadIndexAsync(); var after = await migrated.ReadIndexAsync();
    Check(JToken.DeepEquals(before["SupportedTitleIDs"], after["SupportedTitleIDs"]), "Supported IDs preserved exactly");
    int titles = 0, achievements = 0, bodies = 0, failed = 0;
    foreach (var property in after.Properties().Where(p => p.Name.All(char.IsDigit)))
    {
        var oldTitle = await original.ReadTitleAsync(property.Name); var newTitle = await migrated.ReadTitleAsync(property.Name);
        Check(((JObject)oldTitle["Achievements"]!).Properties().Select(p => p.Name).SequenceEqual(((JObject)newTitle["Achievements"]!).Properties().Select(p => p.Name)), "Achievement IDs preserved: " + property.Name);
        static JArray Operations(JToken mapping)
        {
            var entry = (JObject)mapping;
            var requests = entry.Properties().First().Value["ReplacementType"] != null ? new[] { entry } : entry.Properties().Select(p => (JObject)p.Value).ToArray();
            return new JArray(requests.Select(request => new JArray(request.Properties().Select(p => p.Value.DeepClone()))));
        }
        foreach (var achievement in ((JObject)newTitle["Achievements"]!).Properties())
        {
            Check(JToken.DeepEquals(Operations(oldTitle["Achievements"]![achievement.Name]!), Operations(achievement.Value)), "Ordered operations preserved: " + property.Name + "/" + achievement.Name);
            try { bodies += (await migrated.BuildPayloadsAsync(property.Name, achievement.Name, "0", DateTime.UnixEpoch)).Count; }
            catch (Exception e) when (e is InvalidDataException or Newtonsoft.Json.JsonException or ArgumentException or InvalidCastException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                // Report preexisting rendering failures; migration must never introduce a new one.
                bool originalFailed = false;
                try { await original.BuildPayloadsAsync(property.Name, achievement.Name, "0", DateTime.UnixEpoch); } catch { originalFailed = true; }
                Check(originalFailed, "New rendering regression: " + property.Name + "/" + achievement.Name + ": " + e.Message);
                failed++;
            }
            achievements++;
        }
        titles++;
    }
    Console.WriteLine($"PASS: {titles} titles, {achievements} achievement mappings, {bodies} rendered payloads; {failed} preexisting failures, zero new failures. Ordered replacement operations and all supported IDs preserved.");
}
