using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

internal static class ReportedMappingChecks
{
    public static async Task RunAsync(string directory)
    {
        var catalog = new EventCatalog(directory);
        // Publisher's numbered definitions, matched to Xbox IDs. The 36 reported
        // Battlefront II observations agree with this order; they are not UI row indexes.
        int[] battlefrontIds = [13,14,15,16,17,29,18,19,20,21,30,22,23,25,27,24,28,26,32,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61];
        var expectedCodes = battlefrontIds.Select((id, index) => (Id: id.ToString(), Code: $"ach{index + 1:00}"))
            .ToDictionary(x => x.Id, x => x.Code);
        int achievements = 0, payloads = 0;
        foreach (var titleId in new[] { "1386529057", "833960563", "1285166951", "976905576", "1050025307" })
        {
            var title = await catalog.ReadTitleAsync(titleId);
            var mappings = (JObject)title["Achievements"]!;
            if (titleId == "833960563" && !mappings.Properties().Select(p => p.Name).ToHashSet().SetEquals(expectedCodes.Keys))
                throw new Exception("Battlefront II must retain all 43 achievement IDs.");
            if (titleId == "1386529057" && mappings.Count != 50)
                throw new Exception("Battlefield 1 must retain all 50 achievement IDs.");
            if (titleId == "976905576" && mappings.Count != 69)
                throw new Exception("Dragon Age must retain all 69 achievement IDs.");
            if (titleId == "1050025307" && (mappings.Count != 77 ||
                new[] { "50", "58", "60", "76", "78", "79" }.Any(id => mappings[id] == null) ||
                new[] { "59", "77", "80" }.Any(id => mappings[id] != null)))
                throw new Exception("GTA V must use the corrected Xbox achievement IDs.");
            if (titleId == "1285166951" && (mappings["107"] != null || title["PartiallySupported"]?.Value<bool>() != true))
                throw new Exception("ESO's incorrect 107 recipe must stay unavailable under partial support.");
            foreach (var mapping in mappings.Properties())
            {
                var bodies = await catalog.BuildPayloadsAsync(titleId, mapping.Name, "0", DateTime.UnixEpoch);
                achievements++; payloads += bodies.Count;
                var expected = titleId == "833960563" ? expectedCodes[mapping.Name]
                    : titleId == "1386529057" ? mapping.Name switch { "69" => "ach27", "81" => "ach24", _ => null }
                    : titleId == "976905576" ? mapping.Name switch { "52" => "2", "53" => "3", "102" => "52", "103" => "53", _ => null }
                    : null;
                if (titleId == "976905576" && expected != null && bodies.Count != 2)
                    throw new Exception("Dragon Age must retain only the two App/Game requests for its corrected identifier.");
                foreach (var body in bodies)
                {
                    var parsed = JObject.Parse(body);
                    var data = parsed["data"]!["baseData"]!;
                    if (expected != null)
                    {
                        var properties = (JObject)data["properties"]!;
                        var ids = properties.Properties().Where(p => p.Value.Type == JTokenType.String).ToArray();
                        if (ids.Length == 0 || ids.Any(p => !string.Equals(p.Value.Value<string>(), expected, StringComparison.OrdinalIgnoreCase)))
                            throw new Exception($"Wrong identifier in {titleId}/{mapping.Name}.");
                    }
                    if (titleId == "1285166951" && mapping.Name == "109" &&
                        ((string?)data["name"] != "ICZoneSweepers" ||
                         (string?)parsed["name"] != "Microsoft.XboxLive.T1285166951.ICZoneSweepers"))
                        throw new Exception("ESO 109 must use the observed ICZoneSweepers event in both names.");
                    if (titleId == "1050025307")
                    {
                        var expectedEvent = mapping.Name switch
                        {
                            "50" => "JA_ACH51_Event", "51" => "JA_ACHH1_Event", "58" => "JA_ACHH8_Event",
                            "60" => "JA_ACHH10_Event", "76" => "JA_ACHGO6_Event", "78" => "JA_ACHGO7_Event",
                            "79" => "JA_ACHGO8_Event", _ => null
                        };
                        if (expectedEvent != null && (string?)data["name"] != expectedEvent)
                            throw new Exception($"GTA V {mapping.Name} has the wrong event recipe.");
                    }
                }
            }
        }
        Console.WriteLine($"PASS: {directory}: {achievements} achievements, {payloads} offline payloads; reported mapping corrections verified. No network requests.");
    }
}
