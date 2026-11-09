using System.Net;
using System.Text.Json;

namespace AchievementLabs.Core;

// Public metadata transport: never shares the authenticated Xbox HttpClient.
public sealed class PublicCatalogClient(HttpClient http)
{
    public static readonly Uri Endpoint = new("https://achievementlabs.org/api/v2/catalog/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Title(string TitleId, string Name, string[] Aliases, string[] Platforms, string Status,
        bool EventAvailable, int MappedAchievementCount, int? DefinitionAchievementCount)
    {
        public string StatusLabel => Status == "PartiallySupported" ? "Partially supported" : Status;
        public string TotalLabel => DefinitionAchievementCount is int total ? $"{total} achievements" : "Total unverified";
    }
    public sealed record Page(string CatalogRevision, string SourceRevision, int TotalMatched, Title[] Items, string? NextCursor)
    {
        public bool Offline { get; init; }
    }
    public sealed record Total(string TitleId, int TotalCount, string[] Platforms, DateTimeOffset CheckedAt);
    private static bool Id(string? value) => uint.TryParse(value, out var id) && id > 0 && value == id.ToString();
    private static void Validate(Title title)
    {
        if (!Id(title.TitleId) || string.IsNullOrWhiteSpace(title.Name) || title.Name.Length > 1024 ||
            title.Platforms == null || title.Aliases == null || title.Status is not ("Supported" or "PartiallySupported" or "Testing" or "Unsupported") ||
            title.MappedAchievementCount < 0 || title.DefinitionAchievementCount is < 1 or > 10000)
            throw new InvalidDataException("Invalid public title metadata.");
    }
    private async Task<JsonDocument> ReadAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, relative));
        request.Headers.UserAgent.ParseAdd("AchievementLabs-Catalog/1.3.3");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Oversized metadata response.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Oversized metadata response.");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
    }
    public async Task<Page> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length > 200) throw new ArgumentException("Search must be at most 200 characters.");
        using var doc = await ReadAsync("titles?limit=100&query=" + Uri.EscapeDataString(query), ct);
        var page = doc.RootElement.Deserialize<Page>(Json) ?? throw new InvalidDataException("Missing catalog page.");
        if (page.Items == null || page.Items.Length > 100 || page.TotalMatched < page.Items.Length ||
            page.CatalogRevision?.Length != 64 || string.IsNullOrWhiteSpace(page.SourceRevision)) throw new InvalidDataException("Invalid catalog page.");
        foreach (var title in page.Items) Validate(title);
        if (page.Items.Select(t => t.TitleId).Distinct().Count() != page.Items.Length) throw new InvalidDataException("Duplicate title metadata.");
        return page;
    }
    public static Page BundledSearch(string query)
    {
        using var stream = typeof(PublicCatalogClient).Assembly.GetManifestResourceStream("AchievementLabs.PublicMetadata.json")
            ?? throw new InvalidDataException("Bundled public metadata missing.");
        var all = JsonSerializer.Deserialize<Page>(stream, Json) ?? throw new InvalidDataException("Bundled public metadata invalid.");
        var filtered = all.Items.Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.TitleId.Contains(query, StringComparison.Ordinal) ||
            t.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        return all with { Items = filtered.Take(100).ToArray(), TotalMatched = filtered.Length, NextCursor = null, Offline = true };
    }
    public async Task<SharedAchievementTotals.Entry[]> TotalsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var requested = ids.Distinct().ToArray();
        if (requested.Length > 5000 || requested.Any(id => !Id(id))) throw new ArgumentException("Invalid title ID batch.");
        var result = new List<SharedAchievementTotals.Entry>();
        string? revision = null;
        foreach (var batch in requested.Chunk(100))
        {
            using var doc = await ReadAsync("totals?ids=" + string.Join(',', batch), ct);
            var root = doc.RootElement;
            var current = root.GetProperty("catalogRevision").GetString();
            if (current?.Length != 64 || revision != null && revision != current) throw new InvalidDataException("Catalog changed during totals refresh.");
            revision = current;
            var seen = new HashSet<string>();
            var items = root.GetProperty("items");
            if (items.GetArrayLength() > batch.Length) throw new InvalidDataException("Invalid totals batch.");
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("titleId").GetString()!;
                if (!batch.Contains(id) || !seen.Add(id)) throw new InvalidDataException("Unexpected totals ID.");
                result.Add(new(id, "", item.GetProperty("platforms").Deserialize<string[]>(Json)!, item.GetProperty("total").GetInt32(),
                    item.GetProperty("checkedAt").GetDateTimeOffset(), "Public catalog API"));
            }
            foreach (var missing in root.GetProperty("missingIds").EnumerateArray())
                if (!batch.Contains(missing.GetString()) || !seen.Add(missing.GetString()!)) throw new InvalidDataException("Invalid missing ID.");
            if (seen.Count != batch.Length) throw new InvalidDataException("Incomplete totals response.");
        }
        // Reuse the existing atomic catalogue validator before the caller merges/caches anything.
        var validator = new SharedAchievementTotals();
        validator.Merge(JsonSerializer.Serialize(new SharedAchievementTotals.Catalogue(1, result.ToArray()), Json));
        return result.ToArray();
    }
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false,
        UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(5) };
}
