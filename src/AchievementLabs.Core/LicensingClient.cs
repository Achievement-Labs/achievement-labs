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
    // Signed catalog (official mode only). Precedence per title, see CatalogPrecedence:
    //   custom pack > max-revision of { signed cache, release-zip cache, bundled (snapshot title or Events/) }.
    private SignedEventCatalog? signed;
    private SignedEventCatalog? Signed => officialCatalog && useLocal ? signed ??= new(AchievementLabsPaths.LocalFile("signed-event-catalog"),
        ["345903534"], BundledCatalogSnapshot.Present ? BundledCatalogSnapshot.Read : null) : null;
    private bool usedLegacyFallback;
    private int Marker => Signed?.BundledRevision ?? 0;
    private enum Winner { None, Custom, Signed, Legacy, Local }
    private Winner Resolve(string id, out SignedEventCatalog.Title? signedTitle)
    {
        signedTitle = null;
        if (IsCustom(id)) return Winner.Custom;
        var s = Signed; var cached = s?.Cached(id); var snapshot = s?.BundledTitle(id);
        if (cached is { Removed: true }) { signedTitle = cached; return Winner.Signed; } // Removed from the signed catalog: stays off.
        int? bundled = snapshot?.Revision ?? (Local?.ReservedTitleIds.Contains(id) == true ? Marker : null);
        int? legacy = Additions?.KnownTitleIds.Contains(id) == true
            ? (Additions.CachedReleaseVersion is { } release && UpstreamEventCatalogue.InstalledRelease is { } installed && release > installed ? Marker : -1)
            : null;
        switch (CatalogPrecedence.Choose(cached?.Revision, legacy, bundled))
        {
            case CatalogPrecedence.Tier.Catalog: signedTitle = cached; return Winner.Signed;
            case CatalogPrecedence.Tier.Legacy: return Winner.Legacy;
            case CatalogPrecedence.Tier.Bundled: if (snapshot != null) { signedTitle = snapshot; return Winner.Signed; } return Winner.Local;
            default: return Winner.None;
        }
    }
    private bool OfficialSourceHas(string id) => Signed?.KnownTitleIds.Contains(id) == true || Local?.ReservedTitleIds.Contains(id) == true || Additions?.KnownTitleIds.Contains(id) == true;
    /// <summary>Revision, source and last error for one title (official mode). Custom packs report OverridesOfficial.</summary>
    public EventTitleCatalogStatus? TitleCatalogStatus(string id)
    {
        var winner = Resolve(id, out var title);
        var error = Signed?.LastError(id);
        return winner switch
        {
            Winner.Custom => new(id, 0, EventCatalogSource.Custom, error, OfficialSourceHas(id)),
            Winner.Signed => new(id, title!.Revision, ReferenceEquals(title, Signed!.Cached(id)) ? EventCatalogSource.Catalog : EventCatalogSource.Bundled, error),
            Winner.Legacy => new(id, Marker, EventCatalogSource.Catalog, error),
            Winner.Local => new(id, Marker, EventCatalogSource.Bundled, error),
            _ => null
        };
    }
    public string UpstreamStatus => Signed is { } s
        ? s.Status + (usedLegacyFallback && Additions is { } a ? " · Release-zip fallback: " + a.Status : "")
        : Additions?.Status ?? "New-title updates unavailable.";
    public Task<int> ImportEventPackAsync(string text, CancellationToken ct) => (Custom ?? Additions ?? throw new InvalidOperationException("Local event data is unavailable.")).ImportPackAsync(text, ct);
    public Dictionary<string, LibraryEventSupport> GetLibrarySupport()
    {
        var support = Local?.LibrarySupport() ?? new();
        if (Signed is { } catalog)
        {
            // Official mode: each title answers from the tier CatalogPrecedence picks.
            var localSupport = support; var legacySupport = Additions?.LibrarySupport() ?? new();
            var customSupport = Custom?.LibrarySupport() ?? new();
            var result = new Dictionary<string, LibraryEventSupport>();
            foreach (var id in localSupport.Keys.Concat(legacySupport.Keys).Concat(catalog.KnownTitleIds).Concat(customSupport.Keys).Distinct().ToArray())
            {
                if (TitleCatalogStatus(id) is not { } status) continue;
                LibraryEventSupport? item = Resolve(id, out var title) switch
                {
                    Winner.Custom => customSupport.GetValueOrDefault(id) is { } c ? c with { FullySupported = false, PartiallySupported = false, Note = "Custom event pack; not an official support classification." } : null,
                    Winner.Signed => SignedEventCatalog.Support(title!),
                    Winner.Legacy => legacySupport.GetValueOrDefault(id),
                    Winner.Local => localSupport.GetValueOrDefault(id),
                    _ => null
                };
                if (item != null) result[id] = item with { CatalogRevision = status.Revision, CatalogSource = status.Source, CatalogError = status.LastError, CustomOverride = status.OverridesOfficial };
            }
            return result;
        }
        if (Additions is { } updates) foreach (var item in updates.LibrarySupport()) support[item.Key] = item.Value;
        if (Custom is { } packs) foreach (var item in packs.LibrarySupport().Where(p => IsCustom(p.Key)))
            support[item.Key] = item.Value with { FullySupported = false, PartiallySupported = false, Note = "Custom event pack; not an official support classification." };
        return support;
    }
    public string[] BundledTitleIds => Local?.TitleIds ?? [];
    /// <summary>
    /// Signed catalog first. Only when it is unreachable (network/HTTP failure, or no signing key in this build) does the
    /// legacy release-zip refresh run; a bad signature or rollback is a rejection, not an outage, so it does not fall back.
    /// Safe while Auto Unlock runs: each source swaps its snapshot atomically.
    /// </summary>
    public async Task RefreshUpstreamAsync(HttpClient sourceHttp, bool force, CancellationToken ct, bool startup = false)
    {
#if LABS_DEV
        return; // Dev builds stay local-only.
#else
        if (Signed is { } s)
        {
            var outcome = await s.RefreshAsync(sourceHttp, force, ct, startup).ConfigureAwait(false);
            if (outcome != CatalogRefreshOutcome.Unreachable) { usedLegacyFallback = false; return; }
            usedLegacyFallback = true;
        }
        if (Additions is { } a) await a.RefreshAsync(sourceHttp, force, ct, startup).ConfigureAwait(false);
#endif
    }

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
        if (Signed != null)
            switch (Resolve(id, out var signedTitle))
            {
                case Winner.Signed: return signedTitle!.Enabled
                    ? AchievementLabs.MultiSelect.EventIdMapping.NormalizeCatalog(Task.FromResult(SignedEventCatalog.Mapping(signedTitle)), id)
                    : Task.FromResult(new EventTitleMapping([]));
                case Winner.Local: return AchievementLabs.MultiSelect.EventIdMapping.NormalizeCatalog(Local!.Contains(id) ? Local.TitleAsync(id, ct) : Task.FromResult(new EventTitleMapping([])), id);
            }
        var updates = Additions;
        if (updates?.KnownTitleIds.Contains(id) == true)
            return updates.Contains(id) ? AchievementLabs.MultiSelect.EventIdMapping.NormalizeCatalog(Task.FromResult(updates.Title(id)), id)
                : Task.FromResult(new EventTitleMapping([]));
        return AchievementLabs.MultiSelect.EventIdMapping.NormalizeCatalog(Local?.Contains(id) == true ? Local.TitleAsync(id, ct)
            : SendAsync<EventTitleMapping>(HttpMethod.Get, "titles/" + Uri.EscapeDataString(id), null, ct), id);
    }
    public Task<EventPayloads> GetPayloadsAsync(string id, string achievementId, string xuid, CancellationToken ct)
    {
#if LABS_UPDATE_TESTING
        if (id == "4294967294") return Task.FromException<EventPayloads>(new InvalidOperationException("The catalog-update fixture cannot submit telemetry."));
#endif
        if (IsCustom(id)) return Custom!.PayloadsAsync(id, achievementId, xuid, ct);
        // Read the winning snapshot once: an in-flight achievement finishes on it even if a refresh swaps the catalog.
        if (Signed != null)
            switch (Resolve(id, out var signedTitle))
            {
                case Winner.Signed: return SignedEventCatalog.PayloadsAsync(signedTitle!, AchievementLabs.MultiSelect.EventIdMapping.PayloadId(id, achievementId), xuid, ct);
                case Winner.Local: return Local!.Contains(id) ? Local.PayloadsAsync(id, AchievementLabs.MultiSelect.EventIdMapping.PayloadId(id, achievementId), xuid, ct)
                    : Task.FromException<EventPayloads>(new InvalidOperationException("Event mapping is disabled for this title."));
            }
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
