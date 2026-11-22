using AchievementLabs.Core;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

internal static class StatTemplateChecks
{
    internal static async Task RunAsync(string directory, string inventoryPath)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "labs-stat-templates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        int classified = 0, rendered = 0;
        using var reader = new TextFieldParser(inventoryPath) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        reader.SetDelimiters(",");
        var headers = reader.ReadFields()!;
        try
        {
            while (!reader.EndOfData)
            {
                var values = reader.ReadFields()!;
                var row = headers.Select((name, i) => (name, values[i])).ToDictionary(x => x.name, x => x.Item2);
                var id = row["TitleId"];
                var folder = Path.Combine(directory, id);
                if (!Directory.Exists(folder)) folder = Directory.GetDirectories(directory, id + " - *").Single();
                var templatePath = Path.Combine(folder, id + ".json");
                if (row["AchievementsType"] == "Title Based" && row["StatsType"] == "Event Based")
                {
                    classified++;
                    if (!File.Exists(templatePath)) throw new Exception("Classified stat title lacks template: " + id);
                }
                if (row["TemplateSource"] is "existing-standalone" or "missing-package-identity-no-template-generated") continue;
                var raw = await File.ReadAllTextAsync(templatePath);
                var target = Path.Combine(temporary, id); Directory.CreateDirectory(target);
                await File.WriteAllTextAsync(Path.Combine(target, id + ".json"), raw);
                var eventName = raw.Contains("Microsoft.XboxLive.T" + id + ".") ? "OfflineTemplateCheck" : "Microsoft.XboxLive.T" + id + ".OfflineTemplateCheck";
                var data = new JObject { ["baseType"] = "Microsoft.XboxLive.InGame", ["baseData"] = new JObject {
                    ["name"] = "OfflineTemplateCheck", ["titleId"] = id, ["serviceConfigId"] = row["ServiceConfigId"],
                    ["userId"] = "REPLACEXUID", ["ver"] = 1, ["properties"] = new JObject(), ["measurements"] = new JObject() } };
                JObject Op(string token, string value) => new() { ["ReplacementType"] = "Replace", ["Target"] = token, ["Replacement"] = value };
                var metadata = Regex.IsMatch(raw, "\"metadata\"\\s*:\\s*REPLACEMETADATA") ? "{}" : "\"metadata\":{},";
                var request = new JObject { ["Event"] = Op("REPLACEEVENTNAME", eventName),
                    ["Metadata"] = Op("REPLACEMETADATA", metadata), ["Data"] = Op("REPLACEDATA", data.ToString(Newtonsoft.Json.Formatting.None)) };
                foreach (var token in Regex.Matches(raw, @"REPLACE[A-Z0-9_]+").Select(m => m.Value).Distinct()
                    .Except(new[] { "REPLACEEVENTNAME", "REPLACEMETADATA", "REPLACEDATA", "REPLACETIME", "REPLACESEQ", "REPLACEXUID", "REPLACESESSIONGUID" }))
                    request[token] = Op(token, "1");
                var title = new JObject { ["TitleId"] = id, ["ServiceConfigId"] = row["ServiceConfigId"],
                    ["Achievements"] = new JObject { ["1"] = request } };
                await File.WriteAllTextAsync(Path.Combine(target, "Data.json"), new JObject { [id] = title, ["SupportedTitleIDs"] = new JArray(id) }.ToString());
                JObject body;
                try { body = JObject.Parse((await new EventCatalog(target).BuildPayloadsAsync(id, "1", "0", DateTime.UnixEpoch)).Single()); }
                catch (Exception error) { throw new Exception("Stat template fixture failed for " + id + " (" + row["TemplateSource"] + ")", error); }
                if (body["data"]?["baseData"]?["titleId"]?.ToString() != id) throw new Exception("Stat template title mismatch: " + id);
                if (row["TemplateSource"].StartsWith("catalog-PFN", StringComparison.Ordinal) &&
                    body["ext"]?["app"]?["id"]?.ToString() != "U:" + row["PackageFamilyName"] + "!App")
                    throw new Exception("Stat template package mismatch: " + id);
                rendered++;
            }
            Console.WriteLine($"PASS: {classified} classified titles have standalone templates; {rendered} added templates rendered through actual consumer with synthetic fixtures. No telemetry or runtime-success claim.");
        }
        finally { Directory.Delete(temporary, true); }
    }
}
