using System.Net.Http.Json;
using System.Text.Json;

namespace AchievementLabs.Core;

// PartialTitleIds is optional and also listed in TestingTitleIds, so older builds and catalogs keep working.
public sealed record EventCatalogStatus(string[] TitleIds, string[]? TestingTitleIds, string[] ComingSoonTitleIds, string[]? PartialTitleIds = null);
public sealed record EventTitleMapping(string[] AchievementIds);
public sealed record EventPayloads(string[] Payloads);

/// <summary>Reads supported event mappings from the private remote catalog without persisting recipes.</summary>
public sealed class EventCatalogClient : IDisposable
{
    private readonly HttpClient http = new()
    {
        BaseAddress = new Uri("https://achievementlabs.org/api/licensing/"),
        Timeout = TimeSpan.FromSeconds(25)
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly bool useLocal;
    private readonly bool officialCatalog;
    private readonly Func<string?>? directory;
    private LocalEventCatalogue? local;
    private UpstreamEventCatalogue? additions;
    private UpstreamEventCatalogue? custom;
    private string? loadedPath;
    private DateTime loadedTimestamp;
    public EventCatalogClient() { }
    public EventCatalogClient(bool useBundledCatalog, Func<string?>? eventsDirectory = null, bool officialCatalog = false)
    { useLocal = useBundledCatalog; directory = officialCatalog ? null : eventsDirectory; this.officialCatalog = officialCatalog; }
    private LocalEventCatalogue? Local
    {
        get
        {
            if (!useLocal) return null;
            var path = directory?.Invoke();
            var stamp = path == null ? default : File.GetLastWriteTimeUtc(EventCatalogStorage.IndexPath(path));
            if (local == null || path != loadedPath || stamp != loadedTimestamp) { local = new(path); loadedPath = path; loadedTimestamp = stamp; additions = null; }
            return local;
        }
    }
    private UpstreamEventCatalogue? Additions => Local is { } catalog ? additions ??= new(
        AchievementLabsPaths.LocalFile(officialCatalog ? "official-event-catalog-v2.json.br" : "upstream-event-additions.json"),
        officialCatalog ? ["345903534"] : catalog.ProtectedTitleIds, catalog.Matches, catalog.ReservedTitleIds, officialCatalog) : null;
    private UpstreamEventCatalogue? Custom => officialCatalog && Local is { } catalog ? custom ??= new(
        AchievementLabsPaths.LocalFile("custom-event-packs-v2.json"), catalog.ReservedTitleIds.Concat(Additions!.KnownTitleIds)) : null;
    private bool IsCustom(string id) => officialCatalog && Local?.ReservedTitleIds.Contains(id) != true
        && Additions?.KnownTitleIds.Contains(id) != true && Custom?.KnownTitleIds.Contains(id) == true;
    public string UpstreamStatus => Additions?.Status ?? "New-title updates unavailable.";
    public Task<int> ImportEventPackAsync(string text, CancellationToken ct) => (Custom ?? Additions ?? throw new InvalidOperationException("Local event data is unavailable.")).ImportPackAsync(text, ct);
    public Dictionary<string, LibraryEventSupport> GetLibrarySupport()
    {
        var support = Local?.LibrarySupport() ?? new();
        if (Additions is { } updates) foreach (var item in updates.LibrarySupport()) support[item.Key] = item.Value;
        if (Custom is { } packs) foreach (var item in packs.LibrarySupport().Where(p => IsCustom(p.Key)))
            support[item.Key] = item.Value with { FullySupported = false, PartiallySupported = false, Note = "Custom event pack; not an official support classification." };
        return support;
    }
    public string[] BundledTitleIds => Local?.TitleIds ?? [];
    public async Task RefreshUpstreamAsync(HttpClient sourceHttp, bool force, CancellationToken ct) { if (Additions is { } a) await a.RefreshAsync(sourceHttp, force, ct); }

    public Task<EventCatalogStatus> GetCatalogAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Local is not { } catalog) return SendAsync<EventCatalogStatus>(HttpMethod.Get, "catalog", null, ct);
        var support = GetLibrarySupport();
        return Task.FromResult(new EventCatalogStatus(
            support.Where(p => p.Value.Available && p.Value.FullySupported).Select(p => p.Key).ToArray(),
            support.Where(p => p.Value.Available && !p.Value.FullySupported).Select(p => p.Key).ToArray(), [],
            support.Where(p => p.Value.Available && p.Value.PartiallySupported && !p.Value.FullySupported).Select(p => p.Key).ToArray()));
    }
    public Task<EventTitleMapping> GetTitleAsync(string id, CancellationToken ct)
    {
        if (IsCustom(id)) return Custom!.Contains(id) ? Task.FromResult(Custom.Title(id)) : Task.FromResult(new EventTitleMapping([]));
        var updates = Additions;
        if (updates?.KnownTitleIds.Contains(id) == true)
            return updates.Contains(id) ? AchievementLabs.MultiSelect.EventIdMapping.NormalizeCatalog(Task.FromResult(updates.Title(id)), id)
                : Task.FromResult(new EventTitleMapping([]));
        return AchievementLabs.MultiSelect.EventIdMapping.NormalizeCatalog(Local?.Contains(id) == true ? Local.TitleAsync(id, ct)
            : SendAsync<EventTitleMapping>(HttpMethod.Get, "titles/" + Uri.EscapeDataString(id), null, ct), id);
    }
    public Task<EventPayloads> GetPayloadsAsync(string id, string achievementId, string xuid, CancellationToken ct)
    {
        if (IsCustom(id)) return Custom!.PayloadsAsync(id, achievementId, xuid, ct);
        var updates = Additions;
        if (updates?.KnownTitleIds.Contains(id) == true)
            return updates.Contains(id) ? updates.PayloadsAsync(id, achievementId, xuid, ct)
                : Task.FromException<EventPayloads>(new InvalidOperationException("Event mapping is disabled for this title."));
        return Local?.Contains(id) == true ? Local.PayloadsAsync(id, AchievementLabs.MultiSelect.EventIdMapping.PayloadId(id, achievementId), xuid, ct)
            : SendAsync<EventPayloads>(HttpMethod.Post, "titles/" + Uri.EscapeDataString(id) + "/payloads", new { achievementId = AchievementLabs.MultiSelect.EventIdMapping.PayloadId(id, achievementId), xuid }, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = "The event catalog request could not be completed.";
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                if (error.TryGetProperty("error", out var value)) message = value.GetString() ?? message;
            }
            catch (JsonException) { }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("Empty event catalog response.");
    }

    public void Dispose() => http.Dispose();
}
