using System.Net;
using AchievementLabs.Core;
using AchievementLabs.Desktop.Workflows;
using Newtonsoft.Json.Linq;

internal static class EventStatsCatalogChecks
{
    private sealed class Capture : HttpMessageHandler
    {
        public string TitleId = "";
        public int Count;
        public string? ExpectedField;
        public JToken? ExpectedValue;
        public string StatName = "";
        public readonly List<string> NumericTypeFindings = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post || request.Content == null) throw new Exception("Unexpected catalog test request.");
            var text = await request.Content.ReadAsStringAsync(ct);
            using var strict = System.Text.Json.JsonDocument.Parse(text);
            var body = JObject.Parse(text);
            if (body["data"]?["baseData"]?["titleId"]?.ToString() != TitleId || text.Contains("REPLACE"))
                throw new Exception("Wrong title or unresolved stat placeholder.");
            foreach (var group in new[] { "properties", "measurements" })
            {
                var types = body["ext"]?["metadata"]?["f"]?["baseData"]?["f"]?[group]?["f"] as JObject;
                if (types == null) continue;
                foreach (var typedField in types.Properties().Where(p => p.Value.Type == JTokenType.Integer && p.Value.Value<int>() == 4))
                    if (body["data"]?["baseData"]?[group]?[typedField.Name]?.Type == JTokenType.String)
                        NumericTypeFindings.Add(TitleId + "/" + StatName + "/" + group + "." + typedField.Name + ": Int32 metadata with a string payload value");
            }
            if (ExpectedField is { } field)
            {
                var data = body["data"]!["baseData"]!;
                var actual = data["properties"]?[field] ?? data["measurements"]?[field];
                if (!JToken.DeepEquals(actual, ExpectedValue)) throw new Exception("Edited field or JSON value type changed.");
                ExpectedField = null;
            }
            Count++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
    internal static async Task RunAsync(string directory, string reportPath)
    {
        directory = Path.GetFullPath(directory);
        var reportRoot = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(reportRoot);
        var capture = new Capture();
        var account = new NativeAccountContext { XAUTH = "synthetic", XUIDOnly = "0", EventsToken = "synthetic", EventsDirectory = directory };
        var notices = new List<string>();
        XboxApiClient OfflineClient(string auth)
        {
            // The API constructor dumps its static event headers. Avoid thousands
            // of duplicate header blocks in an exhaustive synthetic-only audit.
            var output = Console.Out;
            XboxApiClient client;
            try { Console.SetOut(TextWriter.Null); client = new XboxApiClient(auth, capture); }
            finally { Console.SetOut(output); }
            // Events use a distinct client; the public transport overload only replaces normal API reads.
            foreach (var name in new[] { "_eventBasedClient", "_spooferClient" })
            {
                var field = typeof(XboxApiClient).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                ((HttpClient)field.GetValue(client)!).Dispose();
                var intercepted = new HttpClient(capture, disposeHandler: false);
                field.SetValue(client, intercepted);
                if (!ReferenceEquals(field.GetValue(client), intercepted)) throw new Exception("Offline transport setup failed.");
            }
            return client;
        }
        var vm = new StatsEditorViewModel(new NativeNotices(notices.Add), account, OfflineClient, allowRemoteCatalog: false);
        typeof(StatsEditorViewModel).GetProperty("DebugRootDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(vm, Path.Combine(reportRoot, "stat-render-fixtures"));
        // Inventory both catalog layouts. Per-title files have the same priority
        // as the editor, including authoring folders named "<id> - <title>".
        var combined = Path.Combine(directory, "Stats.json");
        var stats = File.Exists(combined) ? JObject.Parse(await File.ReadAllTextAsync(combined)) : new JObject();
        foreach (var folder in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, "Stats.json");
            if (!File.Exists(path)) continue;
            var document = JObject.Parse(await File.ReadAllTextAsync(path));
            foreach (var title in document.Properties().Where(p => uint.TryParse(p.Name, out _) && p.Value is JObject))
                stats[title.Name] = title.Value.DeepClone();
        }
        var results = new JArray();
        var failures = new List<string>();
        foreach (var title in stats.Properties().Where(p => uint.TryParse(p.Name, out _) && p.Value is JObject))
        {
            var definitions = (JObject)title.Value["Stats"]!;
            var expectedRecipes = definitions.Properties().Where(stat => stat.Value is JObject map &&
                map.Properties().Any(op => System.Text.RegularExpressions.Regex.IsMatch(op.Name, "^Request[0-9]+$") ||
                    op.Value is JObject operation && operation["ReplacementType"] != null))
                .Select(stat => stat.Name).ToHashSet(StringComparer.Ordinal);
            if ((bool?)title.Value["Enabled"] == false)
            {
                results.Add(new JObject { ["titleId"] = title.Name, ["status"] = "Disabled", ["editableStats"] = 0 });
                continue;
            }
            vm.TitleId = title.Name; vm.RefreshSupport();
            if (!vm.StatModeLabel.Contains("Event-based") || !vm.StatSupportLabel.StartsWith("Supported"))
            {
                if (expectedRecipes.Count != 0)
                    failures.Add(title.Name + ": editor skipped mapped stats.");
                results.Add(new JObject { ["titleId"] = title.Name,
                    ["status"] = expectedRecipes.Count == 0 ? "MetadataOnly" : "MappedStatsNotLoaded",
                    ["editableStats"] = 0, ["expectedRecipes"] = expectedRecipes.Count,
                    ["reason"] = vm.StatSupportLabel });
                continue;
            }
            await vm.LoadStats();
            capture.TitleId = title.Name;
            var editable = vm.StatItems.Where(s => s.IsEditable && s.IsEventBasedStat).ToArray();
            foreach (var missing in expectedRecipes.Except(editable.Select(s => s.Name)))
                failures.Add(title.Name + "/" + missing + ": editor did not load mapped stat.");
            foreach (var unexpected in editable.Select(s => s.Name).Except(expectedRecipes))
                failures.Add(title.Name + "/" + unexpected + ": editor loaded a recipe absent from the selected local catalog.");
            var before = capture.Count;
            foreach (var item in editable)
            {
                var requestsBefore = capture.Count;
                capture.StatName = item.Name;
                item.NewValue = item.Type == "Boolean" ? "true" : "1";
                var definition = title.Value["Stats"]?[item.Name];
                if (definition?["PayloadField"] is JValue field)
                {
                    capture.ExpectedField = field.ToString();
                    capture.ExpectedValue = item.Type == "String" ? new JValue("1") :
                        item.Type == "Boolean" ? new JValue(true) :
                        item.Type == "Double" ? new JValue(1d) : new JValue(1);
                }
                var console = Console.Out;
                try { Console.SetOut(TextWriter.Null); await vm.UpdateSingleStat(item); }
                finally { Console.SetOut(console); }
                if (capture.Count == requestsBefore || item.EditStatus != "Submitted; refresh to verify")
                    failures.Add(title.Name + "/" + item.Name + ": " + item.EditStatus);
            }
            results.Add(new JObject { ["titleId"] = title.Name, ["name"] = title.Value["TitleName"] ?? title.Value["Name"] ?? title.Name,
                ["status"] = "OfflineRendered", ["editableStats"] = editable.Length,
                ["expectedRecipes"] = expectedRecipes.Count, ["renderedPayloads"] = capture.Count - before });
        }
        vm.ReleaseClient();
        await File.WriteAllTextAsync(reportPath, new JObject { ["titles"] = results, ["failures"] = new JArray(failures),
            ["numericTypeFindings"] = new JArray(capture.NumericTypeFindings.Distinct()), ["payloads"] = capture.Count }.ToString());
        Console.WriteLine($"Event-stat consumer: {results.Count} inventoried titles, {capture.Count} intercepted payloads, {failures.Count} rendering failures, {capture.NumericTypeFindings.Distinct().Count()} numeric type findings. Offline rendering does not establish service-side stat updates. No network or telemetry.");
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
    }
}
