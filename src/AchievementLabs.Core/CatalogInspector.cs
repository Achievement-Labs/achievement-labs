using Newtonsoft.Json.Linq;
namespace AchievementLabs.Core;
public sealed record CatalogFinding(string TitleId, string Status, string Detail);
public sealed record CatalogInspection(int Titles, int Supported, bool SupportedLast, IReadOnlyList<CatalogFinding> Findings);
public static class CatalogInspector
{
    public static async Task<CatalogInspection> InspectAsync(string directory, CancellationToken cancellationToken = default)
    {
        var root = EventCatalogStorage.Parse(await File.ReadAllTextAsync(EventCatalogStorage.IndexPath(directory), cancellationToken));
        var properties = root.Properties().ToArray();
        var titles = properties.Where(p => p.Name.All(char.IsDigit) && p.Name.Length > 0).ToArray();
        var last = properties.LastOrDefault()?.Name == "SupportedTitleIDs";
        var supported = root["SupportedTitleIDs"];
        var ids = supported is JArray array ? array.Values<string>().Where(v => v != null).Cast<string>().ToHashSet() : new HashSet<string>();
        var findings = new List<CatalogFinding>();
        if (!last) findings.Add(new("—", "Ordering error", "SupportedTitleIDs must be the final property."));
        if (supported is not JArray) findings.Add(new("—", "Catalog error", "SupportedTitleIDs is missing or is not an array."));
        EventCatalogStorage? storage = null;
        if (last && supported is JArray) {
            try { storage = new EventCatalogStorage(directory); }
            catch (InvalidDataException e) { findings.Add(new("�", "Catalog error", e.Message)); }
        }
        foreach (var title in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (storage != null) await storage.ReadTitleAsync(title.Name, cancellationToken); }
            catch (Exception e) when (e is IOException or InvalidDataException or Newtonsoft.Json.JsonException) { findings.Add(new(title.Name, "Invalid title file", e.Message)); continue; }
            var template = Path.Combine(directory, File.Exists(Path.Combine(directory, "index.json")) ? title.Name : "", title.Name + ".json");
            if (!File.Exists(template)) { findings.Add(new(title.Name, "Missing template", "No standalone title template found.")); continue; }
            // Templates contain bare placeholders; strict JSON validation belongs after replacement.
            if (string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(template, cancellationToken))) findings.Add(new(title.Name, "Empty template", "Standalone template is empty."));
            if (!ids.Contains(title.Name)) findings.Add(new(title.Name, "Not supported", "Title block is absent from SupportedTitleIDs."));
        }
        foreach (var id in ids.Where(id => root[id] == null)) findings.Add(new(id, "Missing block", "Supported ID has no title block."));
        return new(titles.Length, ids.Count, last, findings);
    }
}
