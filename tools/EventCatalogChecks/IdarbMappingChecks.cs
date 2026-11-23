using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

internal static class IdarbMappingChecks
{
    public static async Task RunAsync(string directory)
    {
        const string titleId = "1019936697";
        // Cumulative gameplay requirements, with a single request for each one-shot.
        (string Id, int Count, string Event, string? ContentType)[] requirements =
        [
            ("2", 1, "Goal", null), ("3", 1, "AlleyOop", null),
            ("4", 1, "UnlockAchievementDeepThought", null), ("5", 25, "HeadStomp", null),
            ("6", 10, "ContentCreate", "Team"), ("7", 20, "ContentCreate", "Character"),
            ("8", 10, "ContentQR", "Character"), ("9", 10, "ContentQR", "Song"),
            ("10", 20, "ContentCreate", "Logo"), ("11", 10, "ContentQR", "Logo"),
            ("12", 20, "ContentCreate", "Song"), ("13", 1, "UnlockAchievementBot", null)
        ];
        var catalog = new EventCatalog(directory);
        var title = await catalog.ReadTitleAsync(titleId);
        var mappings = (JObject)title["Achievements"]!;
        if (!mappings.Properties().Select(p => p.Name).ToHashSet().SetEquals(requirements.Select(r => r.Id)))
            throw new Exception("#IDARB must retain its 12 achievement IDs.");
        var sequences = new HashSet<string>();
        int total = 0;
        foreach (var requirement in requirements)
        {
            var requests = (JObject)mappings[requirement.Id]!;
            if (!requests.Properties().Select(p => p.Name).SequenceEqual(Enumerable.Range(1, requirement.Count).Select(i => "Request" + i)))
                throw new Exception($"#IDARB {requirement.Id} must have {requirement.Count} contiguous requests.");
            var bodies = await catalog.BuildPayloadsAsync(titleId, requirement.Id, "42", DateTime.UnixEpoch);
            if (bodies.Count != requirement.Count)
                throw new Exception($"#IDARB {requirement.Id} rendered the wrong number of payloads.");
            JToken? firstData = null, firstMetadata = null;
            foreach (var text in bodies)
            {
                var payload = JObject.Parse(text);
                var data = payload["data"]!["baseData"]!;
                if ((string?)payload["name"] != $"Microsoft.XboxLive.T{titleId}.{requirement.Event}" ||
                    (string?)data["name"] != requirement.Event || (string?)data["userId"] != "42")
                    throw new Exception($"#IDARB {requirement.Id} event identity changed.");
                if (requirement.ContentType != null && (string?)data["properties"]?["ContentType"] != requirement.ContentType)
                    throw new Exception($"#IDARB {requirement.Id} targets the wrong content type.");
                if (requirement.Id == "2" && data["measurements"]?["Points"]?.Value<int>() != 15)
                    throw new Exception("#IDARB's fifteen-point shot must retain its original value.");
                var sequence = payload["ext"]!["utc"]!["seq"]!.ToString();
                if (!sequences.Add(sequence)) throw new Exception("#IDARB requests reused a telemetry sequence number.");
                var metadata = payload["ext"]!["metadata"]!;
                firstData ??= data.DeepClone(); firstMetadata ??= metadata.DeepClone();
                if (!JToken.DeepEquals(data, firstData) || !JToken.DeepEquals(metadata, firstMetadata))
                    throw new Exception($"#IDARB {requirement.Id} repeated requests must preserve the same action and metadata.");
            }
            total += bodies.Count;
        }
        Console.WriteLine($"PASS: {directory}: #IDARB's 12 achievements render {total} payloads with the required bounded counts and unique sequences. Offline only.");
    }
}
