using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace AchievementLabs.Core;

public sealed class EventCatalog
{
    private static long sequence;
    private JObject? snapshot;
    private readonly Dictionary<string, string> templates = new();
    private readonly string directory;
    private readonly Func<string, CancellationToken, Task<string>>? templateReader;
    internal EventCatalog(JObject catalog, Func<string, CancellationToken, Task<string>> readTemplate)
    { directory = ""; snapshot = catalog; templateReader = readTemplate; }
    public EventCatalog(string directory) => this.directory = Path.GetFullPath(directory);
    public async Task<JObject> ReadAsync(CancellationToken cancellationToken = default) => templateReader != null ? snapshot! : JObject.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "Data.json"), cancellationToken));
    public async Task<IReadOnlyList<string>> BuildPayloadsAsync(string titleId, string achievementId, string xuid, DateTime timestamp, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(titleId, @"^\d+$") || !Regex.IsMatch(achievementId, @"^\d+$")) throw new ArgumentException("Title and achievement IDs must be numeric.");
        var catalog = snapshot ??= await ReadAsync(cancellationToken);
        var entry = catalog[titleId]?["Achievements"]?[achievementId] as JObject ?? throw new InvalidOperationException("No event mapping is available for this achievement.");
        if (!templates.TryGetValue(titleId, out var template)) templates[titleId] = template = templateReader != null ? await templateReader(titleId, cancellationToken) : await File.ReadAllTextAsync(Path.Combine(directory, titleId + ".json"), cancellationToken);
        var first = entry.Properties().FirstOrDefault()?.Value as JObject ?? throw new InvalidDataException("Empty event mapping.");
        var requests = first["ReplacementType"] != null ? new[] { entry } : entry.Properties().Select(p => (JObject)p.Value).ToArray();
        var bodies = new List<string>();
        var session = Guid.NewGuid().ToString();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = template;
            foreach (var property in request.Properties())
            {
                var op = (JObject)property.Value;
                var type = (string?)op["ReplacementType"];
                string replacement = type switch
                {
                    "Replace" or "RawBody" => op["Replacement"]?.ToString() ?? "",
                    "RangeInt" => Random.Shared.Next((int)op["Min"]!, (int)op["Max"]!).ToString(),
                    "RangeFloat" => ((float)Random.Shared.NextDouble() * ((float)op["Max"]! - (float)op["Min"]!) + (float)op["Min"]!).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "StupidFuckingLDAPTimestamp" => timestamp.ToFileTimeUtc().ToString(),
                    _ => throw new InvalidDataException("Unknown replacement operation: " + type)
                };
                body = type == "RawBody" ? replacement : body.Replace((string?)op["Target"] ?? throw new InvalidDataException("Missing replacement target."), replacement);
            }
            body = body.Replace("REPLACESEQ", Interlocked.Increment(ref sequence).ToString()).Replace("REPLACETIME", timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")).Replace("REPLACEXUID", xuid).Replace("REPLACESESSIONGUID", session);
            if (Regex.IsMatch(body, @"REPLACE[A-Z0-9_]+")) throw new InvalidDataException("The event mapping contains an unresolved placeholder.");
            var parsed = JObject.Parse(body);
            if (parsed["data"]?["baseData"]?["titleId"] is { } payloadTitle && payloadTitle.ToString() != titleId) throw new InvalidDataException("Event title ID does not match the selected title.");
            bodies.Add(parsed.ToString(Newtonsoft.Json.Formatting.None));
        }
        return bodies;
    }
}
