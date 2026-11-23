using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Per-title catalog status for the support badges and the Event catalogue status line.</summary>
/// <param name="Source">"bundled", "catalog" or "custom".</param>
/// <param name="OverridesOfficial">A custom/user mapping is shadowing an official title.</param>
public sealed record EventTitleCatalogStatus(string TitleId, int Revision, string Source, string? LastError, bool OverridesOfficial = false);

public static class EventCatalogSource
{
    public const string Bundled = "bundled", Catalog = "catalog", Custom = "custom";
}

public enum CatalogRefreshOutcome { Throttled, Unchanged, Applied, Rejected, Unreachable }

/// <summary>
/// Precedence between catalog tiers for one title. Higher revision wins. Ties: catalog, then legacy release zip, then bundled.
/// <para>Legacy sources (the exe's Events/ bundle and the release-zip cache) have no per-title revisions, so they
/// carry the bundled-snapshot marker: the manifest revision of the snapshot embedded in this exe (0 without one).
/// The bundled tier uses the snapshot's per-title revision when the snapshot has the title, and the marker for
/// titles only in Events/. The release-zip cache gets the marker only when it came from a release newer than the
/// installed exe; otherwise it gets -1, so a stale zip cache can never shadow data that shipped with this exe.</para>
/// </summary>
public static class CatalogPrecedence
{
    public enum Tier { None, Catalog, Legacy, Bundled }
    public static Tier Choose(int? catalog, int? legacy, int? bundled)
    {
        var best = Tier.None; int rank = int.MinValue;
        void Consider(int? revision, Tier tier) { if (revision is { } r && r > rank) { rank = r; best = tier; } }
        // Evaluated in tie-break order: only a strictly higher revision displaces an earlier tier.
        Consider(catalog, Tier.Catalog); Consider(legacy, Tier.Legacy); Consider(bundled, Tier.Bundled);
        return best;
    }
}

/// <remarks>
/// Revision scale (shared with the catalog builder): the manifest revision is `git rev-list --count` of the catalog
/// repository, and each title's revision is that same global count for the last change touching the title's folder.
/// So a title revision is never above its manifest's revision, and a snapshot's manifest revision (the bundled
/// marker) is directly comparable with any title revision. Manifests that break this are rejected.
/// </remarks>
/// <summary>
/// Signed catalog published separately from app releases (Achievement-Labs/achievement-labs-catalog).
/// The manifest is verified with an embedded ECDSA P-256 key; title bundles are verified by sha256 and size,
/// validated one at a time, and cached so one bad title never blocks the rest.
/// </summary>
public sealed class SignedEventCatalog
{
    public const string ManifestUrl = "https://achievement-labs.github.io/achievement-labs-catalog/v1/manifest.json";
    /// <summary>Optional second mirror of the same manifest and titles. Empty means no mirror.</summary>
    public const string MirrorManifestUrl = "";
    // ===== PLACEHOLDER: SIGNING KEY =====
    // Replace with the catalog repository's ECDSA P-256 public key (SubjectPublicKeyInfo PEM). Public key only.
    // While this placeholder is present no manifest can verify, and the client falls back to the release zip.
    public const string PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nPLACEHOLDER-CATALOG-SIGNING-KEY\n-----END PUBLIC KEY-----";
    public static bool IsPlaceholderKey(string pem) => pem.Contains("PLACEHOLDER", StringComparison.Ordinal);

    // Removed: an accepted manifest later dropped the title. It stays disabled and outranks bundled/legacy data.
    // FromManifest: revision of the signed manifest that vouches for this title's revision, sha256 and size.
    internal sealed record Title(string Id, int Revision, string Sha256, string? FileName, bool Enabled, JObject Block, string Template, bool Removed = false, int FromManifest = 0);
    private sealed record Failure(string Sha256, string Error);
    // Listed: every title id that appeared in an accepted manifest; ids later dropped become removed tombstones.
    private sealed record Snapshot(int ManifestRevision, string Commit, DateTime CheckedUtc, Dictionary<string, Title> Cached, Dictionary<string, Failure> Failures,
        HashSet<string> Listed, byte[]? ManifestBytes = null);

    private readonly string cacheDirectory;
    private readonly string publicKeyPem;
    private readonly string[] manifestUrls;
    private readonly HashSet<string> reserved;
    private readonly Dictionary<string, Title> bundled = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Snapshot current = new(0, "", DateTime.MinValue, new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    /// <summary>Manifest revision of the snapshot embedded in this exe; the marker for revision-less legacy data.</summary>
    public int BundledRevision { get; }
    public bool HasBundledSnapshot { get; }
    public int AcceptedRevision => Volatile.Read(ref current).ManifestRevision;
    public string Status { get; private set; }

    /// <param name="bundledSnapshot">Reads "manifest.json" and "titles/..." from the bundled snapshot; null when none is embedded.</param>
    /// <param name="publicKeyPem">Injectable for tests; defaults to <see cref="PublicKeyPem"/>.</param>
    public SignedEventCatalog(string cacheDirectory, IEnumerable<string>? reservedTitleIds = null, Func<string, byte[]?>? bundledSnapshot = null,
        string? publicKeyPem = null, IEnumerable<string>? manifestUrls = null)
    {
        this.cacheDirectory = cacheDirectory;
        this.publicKeyPem = publicKeyPem ?? PublicKeyPem;
        this.manifestUrls = (manifestUrls ?? [ManifestUrl, MirrorManifestUrl]).Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
        reserved = (reservedTitleIds ?? []).ToHashSet(StringComparer.Ordinal);
        Status = "Signed catalog not checked yet.";
        try { if (bundledSnapshot != null && LoadBundled(bundledSnapshot) is { } revision) { BundledRevision = revision; HasBundledSnapshot = true; } }
        catch { bundled.Clear(); }
        try { LoadCache(); }
        catch { Status = "Signed catalog cache could not be validated; bundled data retained."; }
    }

    private int? LoadBundled(Func<string, byte[]?> read)
    {
        // The snapshot is embedded in the (Authenticode-signed) exe, so its integrity rides on the exe. Title files
        // are still checked against the manifest's sha256/size so a mismatched build input fails loudly in tests.
        if (read("manifest.json") is not { } bytes) return null;
        var manifest = Parse(Encoding.UTF8.GetString(bytes));
        var revision = (int?)manifest["revision"] ?? throw new InvalidDataException("Snapshot revision missing.");
        foreach (var (id, entry) in Entries(manifest).ToList())
        {
            if (reserved.Contains(id)) continue;
            if (!entry.Enabled) { bundled[id] = Disabled(id, entry.Revision); continue; }
            try
            {
                var file = read(entry.Path!) ?? throw new InvalidDataException("Snapshot title file missing.");
                bundled[id] = ParseBundle(id, entry, file, revision);
            }
            catch (Exception e) when (IsDataError(e)) { }
        }
        return revision;
    }

    private string StatePath => Path.Combine(cacheDirectory, "state.json");
    private string TitlePath(string fileName) => Path.Combine(cacheDirectory, "titles", fileName);

    private string ManifestFolder => Path.Combine(cacheDirectory, "manifests");
    private string ManifestPath(int revision) => Path.Combine(ManifestFolder, revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

    /// <summary>
    /// Authority comes only from signed manifests kept in the cache (manifests/&lt;rev&gt;.json + .sig), re-verified here.
    /// The rollback floor is the newest verified manifest; each title's revision, sha256 and size come from a verified
    /// manifest. state.json is advisory only (check time, failure messages, ids seen in earlier manifests). If nothing
    /// verifies, the cache is discarded: floor 0 and bundled data wins.
    /// </summary>
    private void LoadCache()
    {
        var verified = new SortedDictionary<int, (JObject Manifest, Dictionary<string, Entry> Entries, byte[] Bytes)>();
        if (Directory.Exists(ManifestFolder) && !IsPlaceholderKey(publicKeyPem))
            foreach (var file in Directory.EnumerateFiles(ManifestFolder, "*.json"))
            {
                try
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rev)) continue;
                    var bytes = File.ReadAllBytes(file);
                    if (!VerifySignature(bytes, File.ReadAllText(file + ".sig").Trim())) continue;
                    var manifest = Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                    if ((int?)manifest["revision"] != rev) continue;
                    verified[rev] = (manifest, Entries(manifest).ToDictionary(e => e.Id, e => e.Entry, StringComparer.Ordinal), bytes);
                }
                catch (Exception e) when (IsDataError(e) || e is IOException or UnauthorizedAccessException) { }
            }
        if (verified.Count == 0) { Status = "No verified signed catalog cached; bundled data in use."; return; }
        var floor = verified.Keys.Max(); var top = verified[floor];
        var state = new JObject();
        try { if (File.Exists(StatePath)) state = Parse(File.ReadAllText(StatePath)); } catch (Exception e) when (IsDataError(e) || e is IOException) { }
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in verified.Values) listed.UnionWith(v.Entries.Keys);
        // Advisory: ids seen in manifests no longer retained. They can only produce removed tombstones (disable), never data.
        foreach (var token in state["listed"] as JArray ?? new JArray()) if (EventCatalogStorage.ValidId(token.ToString())) listed.Add(token.ToString());
        var cached = new Dictionary<string, Title>(StringComparer.Ordinal);
        var failures = new Dictionary<string, Failure>(StringComparer.Ordinal);
        foreach (var id in listed)
        {
            if (reserved.Contains(id)) continue;
            if (!top.Entries.TryGetValue(id, out var entry)) { cached[id] = Disabled(id, 0) with { Removed = true, FromManifest = floor }; continue; }
            if (!entry.Enabled) { cached[id] = Disabled(id, entry.Revision) with { FromManifest = floor }; continue; }
            // Newest retained manifest whose title file is present and matches (a failed title keeps an older good one).
            foreach (var (rev, v) in verified.Reverse())
            {
                if (!v.Entries.TryGetValue(id, out var e) || !e.Enabled) continue;
                try { cached[id] = ParseBundle(id, e, File.ReadAllBytes(TitlePath(e.Path![7..])), rev, validate: false); break; }
                catch (Exception error) when (IsDataError(error) || error is IOException or UnauthorizedAccessException) { }
            }
        }
        foreach (var p in (state["failures"] as JObject ?? new()).Properties())
            if (p.Value is JObject f) failures[p.Name] = new((string?)f["sha256"] ?? "", (string?)f["error"] ?? "Rejected.");
        DateTime checkedUtc = DateTime.MinValue;
        try { checkedUtc = (DateTime?)state["checkedUtc"] ?? DateTime.MinValue; } catch (Exception e) when (IsDataError(e)) { }
        Volatile.Write(ref current, new(floor, (string?)top.Manifest["commit"] ?? "", checkedUtc, cached, failures, listed, top.Bytes));
        Status = StatusLine(Volatile.Read(ref current), null);
    }

    private sealed record Entry(int Revision, string Sha256, long Size, string? Path, bool Enabled);
    private static IEnumerable<(string Id, Entry Entry)> Entries(JObject manifest)
    {
        if ((int?)manifest["schema"] != 1) throw new InvalidDataException("Unsupported catalog schema.");
        var manifestRevision = (int?)manifest["revision"] ?? throw new InvalidDataException("Manifest revision missing.");
        if (manifestRevision < 0) throw new InvalidDataException("Invalid manifest revision.");
        var titles = manifest["titles"] as JObject ?? throw new InvalidDataException("Manifest titles are missing.");
        if (titles.Count > 5000) throw new InvalidDataException("Title limit exceeded.");
        foreach (var p in titles.Properties())
        {
            if (!EventCatalogStorage.ValidId(p.Name) || p.Value is not JObject t) throw new InvalidDataException("Invalid manifest title.");
            var revision = (int?)t["revision"] ?? throw new InvalidDataException("Title revision missing.");
            if (revision < 0 || revision > manifestRevision) throw new InvalidDataException("Title " + p.Name + " revision " + revision + " is above manifest revision " + manifestRevision + ".");
            var enabled = (bool?)t["enabled"] ?? throw new InvalidDataException("Title enabled flag missing.");
            if (!enabled) { yield return (p.Name, new(revision, "", 0, null, false)); continue; }
            var sha = (string?)t["sha256"] ?? ""; var path = (string?)t["path"] ?? ""; var size = (long?)t["size"] ?? -1;
            if (!Regex.IsMatch(sha, "^[0-9a-f]{64}$") || size is < 1 or > 64 * 1024 * 1024
                || !Regex.IsMatch(path, @"^titles/" + p.Name + @"\.[0-9a-f]{12}\.json$"))
                throw new InvalidDataException("Invalid manifest entry for title " + p.Name + ".");
            yield return (p.Name, new(revision, sha, size, path, true));
        }
    }

    private static Title Disabled(string id, int revision) =>
        new(id, revision, "", null, false, new JObject { ["Enabled"] = false, ["FullySupported"] = false, ["Achievements"] = new JObject() }, "");

    private static Title ParseBundle(string id, Entry entry, byte[] bytes, int fromManifest, bool validate = true)
    {
        if (bytes.LongLength != entry.Size) throw new InvalidDataException("Title size mismatch.");
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != entry.Sha256) throw new InvalidDataException("Title sha256 mismatch.");
        var bundle = Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'));
        if ((string?)bundle["titleId"] != id || (int?)bundle["revision"] != entry.Revision) throw new InvalidDataException("Title identity or revision mismatch.");
        var block = bundle["data"] as JObject ?? throw new InvalidDataException("Title data is missing.");
        var template = bundle["template"]?.Type == JTokenType.String ? (string)bundle["template"]! : throw new InvalidDataException("Title template is missing.");
        if (bundle["stats"] is { } stats && stats.Type is not (JTokenType.Object or JTokenType.Null)) throw new InvalidDataException("Invalid title stats.");
        EventCatalogSchema.ValidateTitle(block);
        if ((bool?)block["Enabled"] == false) throw new InvalidDataException("Enabled title bundle carries a disabled block.");
        if (validate) UpstreamEventCatalogue.ValidateAsync(id, block, template, CancellationToken.None, officialCatalog: true).GetAwaiter().GetResult();
        return new(id, entry.Revision, entry.Sha256, entry.Path![7..], true, block, template, FromManifest: fromManifest);
    }

    internal Title? Cached(string id) => Volatile.Read(ref current).Cached.GetValueOrDefault(id);
    internal Title? BundledTitle(string id) => bundled.GetValueOrDefault(id);
    public string? LastError(string id) => Volatile.Read(ref current).Failures.GetValueOrDefault(id)?.Error;
    public string[] KnownTitleIds => Volatile.Read(ref current).Cached.Keys.Concat(bundled.Keys).Distinct().ToArray();
    public string[] FailedTitleIds => Volatile.Read(ref current).Failures.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>Newest revision between the signed cache and the bundled snapshot; ties go to the cache (same content).</summary>
    internal Title? Effective(string id)
    {
        var cache = Cached(id); var snap = BundledTitle(id);
        if (cache is { Removed: true }) return cache;
        return CatalogPrecedence.Choose(cache?.Revision, null, snap?.Revision) == CatalogPrecedence.Tier.Catalog ? cache : snap;
    }
    internal bool EffectiveIsCached(string id) => Effective(id) is { } t && ReferenceEquals(t, Cached(id));

    internal static LibraryEventSupport Support(Title title) => new(title.Enabled, (bool?)title.Block["FullySupported"] == true,
        (title.Block["Achievements"] as JObject)?.Count ?? 0, PartiallySupported: (bool?)title.Block["PartiallySupported"] == true);
    public Dictionary<string, LibraryEventSupport> LibrarySupport() => KnownTitleIds.Select(id => (id, t: Effective(id)!))
        .ToDictionary(x => x.id, x => Support(x.t) with { CatalogRevision = x.t.Revision,
            CatalogSource = EffectiveIsCached(x.id) ? EventCatalogSource.Catalog : EventCatalogSource.Bundled, CatalogError = LastError(x.id) });

    internal static EventTitleMapping Mapping(Title title) => new(((JObject)title.Block["Achievements"]!).Properties().Select(p => p.Name).ToArray());
    internal static async Task<EventPayloads> PayloadsAsync(Title title, string achievementId, string xuid, CancellationToken ct)
    {
        if (!title.Enabled) throw new InvalidOperationException("Event mapping is disabled for this title.");
        var catalog = new EventCatalog(new JObject { [title.Id] = title.Block, ["SupportedTitleIDs"] = new JArray(title.Id) },
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(title.Template); });
        return new((await catalog.BuildPayloadsAsync(title.Id, achievementId, xuid, DateTime.UtcNow, ct).ConfigureAwait(false)).ToArray());
    }
    public Task<EventPayloads> PayloadsAsync(string id, string achievementId, string xuid, CancellationToken ct) =>
        Effective(id) is { } title ? PayloadsAsync(title, achievementId, xuid, ct) : Task.FromException<EventPayloads>(new InvalidOperationException("Title is not in the signed catalog."));

    /// <summary>
    /// Fetches and verifies the manifest, then downloads only titles whose sha256 changed. Never blocks on the
    /// unlock queue: the new snapshot is published with Volatile.Write, so in-flight payload builds keep the old one.
    /// </summary>
    public async Task<CatalogRefreshOutcome> RefreshAsync(HttpClient http, bool force = false, CancellationToken ct = default, bool startup = false)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref current);
            if (!force && !startup && DateTime.UtcNow - previous.CheckedUtc < TimeSpan.FromHours(1)) return CatalogRefreshOutcome.Throttled;
            byte[]? manifestBytes = null; string? signature = null; string? baseUrl = null; string unreachable = "no catalog URL configured";
            foreach (var url in manifestUrls)
            {
                try
                {
                    manifestBytes = await ReadBytesAsync(http, url, 8 * 1024 * 1024, ct).ConfigureAwait(false);
                    signature = Encoding.ASCII.GetString(await ReadBytesAsync(http, url + ".sig", 4096, ct).ConfigureAwait(false)).Trim();
                    baseUrl = url[..(url.LastIndexOf('/') + 1)]; break;
                }
                catch (Exception e) when (e is HttpRequestException or InvalidDataException || e is TaskCanceledException && !ct.IsCancellationRequested)
                { manifestBytes = null; unreachable = e is HttpRequestException h ? $"HTTP {(int?)h.StatusCode}" : e.Message; }
            }
            if (manifestBytes == null || signature == null || baseUrl == null)
            { Status = $"Signed catalog unreachable ({unreachable}); existing data retained."; return CatalogRefreshOutcome.Unreachable; }
            if (IsPlaceholderKey(publicKeyPem))
            { Status = "Signed catalog signing key is not configured in this build; existing data retained."; return CatalogRefreshOutcome.Unreachable; }
            if (!VerifySignature(manifestBytes, signature))
            { Status = "Signed catalog rejected: bad manifest signature. Existing data retained."; return CatalogRefreshOutcome.Rejected; }

            var manifest = Parse(Encoding.UTF8.GetString(manifestBytes).TrimStart('﻿'));
            var revision = (int?)manifest["revision"] ?? throw new InvalidDataException("Manifest revision missing.");
            if (revision < previous.ManifestRevision)
            { Status = $"Signed catalog rejected: revision {revision} is older than accepted revision {previous.ManifestRevision}. Existing data retained."; return CatalogRefreshOutcome.Rejected; }
            if (revision == previous.ManifestRevision && previous.ManifestBytes is { } accepted && !accepted.AsSpan().SequenceEqual(manifestBytes))
            { Status = $"Signed catalog rejected: revision {revision} was republished with different content. Existing data retained."; return CatalogRefreshOutcome.Rejected; }
            var entries = Entries(manifest).ToList();

            var cached = new Dictionary<string, Title>(StringComparer.Ordinal);
            var failures = new Dictionary<string, Failure>(StringComparer.Ordinal);
            int downloaded = 0;
            foreach (var (id, entry) in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (reserved.Contains(id)) continue;
                var old = previous.Cached.GetValueOrDefault(id);
                if (!entry.Enabled) { cached[id] = Disabled(id, entry.Revision) with { FromManifest = revision }; continue; }
                if (old is { Enabled: true } && old.Sha256 == entry.Sha256) { cached[id] = old; continue; }
                // A bundled title at the same or newer revision wins anyway; no need to download it.
                if (bundled.GetValueOrDefault(id) is { } snap && snap.Revision >= entry.Revision) { if (old is { Removed: false }) cached[id] = old; continue; }
                if (!force && previous.Failures.GetValueOrDefault(id) is { } failed && failed.Sha256 == entry.Sha256)
                { failures[id] = failed; if (old is { Removed: false }) cached[id] = old; continue; }
                try
                {
                    var bytes = await ReadBytesAsync(http, baseUrl + entry.Path, checked((int)entry.Size), ct).ConfigureAwait(false);
                    downloaded++;
                    var title = ParseBundle(id, entry, bytes, revision);
                    Directory.CreateDirectory(Path.Combine(cacheDirectory, "titles"));
                    await WriteAtomicAsync(TitlePath(title.FileName!), bytes, ct).ConfigureAwait(false);
                    cached[id] = title;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (IsDataError(e) || e is HttpRequestException or IOException or TaskCanceledException)
                {
                    // Per-title isolation: keep the previous good version (or let the bundled one win) and continue.
                    failures[id] = new(entry.Sha256, e is HttpRequestException h ? $"HTTP {(int?)h.StatusCode}" : e.Message);
                    if (old is { Removed: false }) cached[id] = old;
                }
            }
            // A title dropped after appearing in an accepted manifest counts as disabled (old official behaviour):
            // it is kept as a removed tombstone so neither the bundled snapshot nor legacy data can revive it.
            // Tombstones are computed against every id listed by any accepted manifest, cached or not, so a title that
            // only ever came from the bundled snapshot cannot be revived after the catalog drops it.
            var inManifest = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var listed = new HashSet<string>(previous.Listed, StringComparer.Ordinal); listed.UnionWith(inManifest);
            foreach (var gone in listed.Where(id => !inManifest.Contains(id) && !reserved.Contains(id)))
                cached[gone] = Disabled(gone, previous.Cached.GetValueOrDefault(gone)?.Revision ?? 0) with { Removed = true, FromManifest = revision };
            var next = new Snapshot(revision, (string?)manifest["commit"] ?? "", DateTime.UtcNow, cached, failures, listed, manifestBytes);
            Directory.CreateDirectory(ManifestFolder);
            await WriteAtomicAsync(ManifestPath(revision) + ".sig", Encoding.ASCII.GetBytes(signature), ct).ConfigureAwait(false);
            await WriteAtomicAsync(ManifestPath(revision), manifestBytes, ct).ConfigureAwait(false);
            await SaveStateAsync(next, ct).ConfigureAwait(false);
            Volatile.Write(ref current, next);
            PruneTitleFiles(next);
            Status = StatusLine(next, downloaded);
            return downloaded == 0 && revision == previous.ManifestRevision && failures.Count == previous.Failures.Count
                ? CatalogRefreshOutcome.Unchanged : CatalogRefreshOutcome.Applied;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (IsDataError(e))
        { Status = $"Signed catalog rejected: {e.Message} Existing data retained."; return CatalogRefreshOutcome.Rejected; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Status = $"Signed catalog could not be saved: {e.Message} Existing data retained."; return CatalogRefreshOutcome.Rejected; }
        finally { gate.Release(); }
    }

    private string StatusLine(Snapshot s, int? downloaded)
    {
        var line = $"Catalog revision {s.ManifestRevision}";
        if (HasBundledSnapshot) line += $" (bundled {BundledRevision})";
        line += $" · {s.Cached.Count(t => t.Value.Enabled):N0} cached titles";
        if (downloaded is { } d) line += $" · {d:N0} downloaded";
        if (s.Failures.Count > 0) line += $" · {s.Failures.Count:N0} title failures: " + string.Join(", ", s.Failures.Keys.OrderBy(x => x, StringComparer.Ordinal).Take(5)) + (s.Failures.Count > 5 ? ", …" : "");
        if (s.CheckedUtc > DateTime.MinValue) line += $" · {s.CheckedUtc.ToLocalTime():g}";
        return line;
    }

    private bool VerifySignature(byte[] manifest, string signature)
    {
        try
        {
            using var key = ECDsa.Create(); key.ImportFromPem(publicKeyPem);
            // Require P-256 exactly: key size and the named-curve OID 1.2.840.10045.3.1.7 (secp256r1 / nistP256).
            var oid = key.ExportParameters(false).Curve.Oid;
            var value = oid?.Value ?? (oid?.FriendlyName is { } friendly ? Oid.FromFriendlyName(friendly, OidGroup.All).Value : null);
            if (key.KeySize != 256 || value != "1.2.840.10045.3.1.7") return false;
            return key.VerifyData(manifest, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException or PlatformNotSupportedException) { return false; }
    }

    // Advisory state only; see LoadCache. Title data and revisions are re-derived from the signed manifests.
    private async Task SaveStateAsync(Snapshot s, CancellationToken ct)
    {
        var failures = new JObject();
        foreach (var f in s.Failures.OrderBy(f => f.Key, StringComparer.Ordinal)) failures[f.Key] = new JObject { ["sha256"] = f.Value.Sha256, ["error"] = f.Value.Error };
        var state = new JObject { ["manifestRevision"] = s.ManifestRevision, ["checkedUtc"] = s.CheckedUtc, ["listed"] = new JArray(s.Listed.OrderBy(x => x, StringComparer.Ordinal)), ["failures"] = failures };
        Directory.CreateDirectory(cacheDirectory);
        await WriteAtomicAsync(StatePath, new UTF8Encoding(false).GetBytes(state.ToString(Formatting.None)), ct).ConfigureAwait(false);
    }

    private void PruneTitleFiles(Snapshot s)
    {
        try
        {
            var keep = s.Cached.Values.Where(t => t.FileName != null).Select(t => t.FileName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manifests = s.Cached.Values.Where(t => t.Enabled).Select(t => t.FromManifest).Append(s.ManifestRevision).ToHashSet();
            if (Directory.Exists(ManifestFolder))
                foreach (var file in Directory.EnumerateFiles(ManifestFolder, "*.json"))
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var rev) || !manifests.Contains(rev)) { File.Delete(file); if (File.Exists(file + ".sig")) File.Delete(file + ".sig"); }
            var folder = Path.Combine(cacheDirectory, "titles");
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*.json")) if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsDataError(Exception e) => e is InvalidDataException or JsonException or System.Text.Json.JsonException or ArgumentException
        or InvalidCastException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException or DecoderFallbackException;

    private static JObject Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    }

    private static async Task<byte[]> ReadBytesAsync(HttpClient http, string uri, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AchievementLabs-CatalogClient/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Catalog download exceeds its size limit.");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw new InvalidDataException("Catalog download exceeds its size limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}

/// <summary>Reads the catalog snapshot that release builds embed with -p:LabsCatalogSnapshot=&lt;dist folder&gt;.</summary>
public static class BundledCatalogSnapshot
{
    private const string Prefix = "AchievementLabs.CatalogSnapshot/";
    private static readonly Lazy<Dictionary<string, string>> Names = new(() => typeof(BundledCatalogSnapshot).Assembly.GetManifestResourceNames()
        .Where(n => n.Replace('\\', '/').StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(n => n.Replace('\\', '/')[Prefix.Length..], n => n, StringComparer.Ordinal));
    public static bool Present => Names.Value.ContainsKey("manifest.json");
    public static byte[]? Read(string path)
    {
        if (!Names.Value.TryGetValue(path, out var name)) return null;
        using var stream = typeof(BundledCatalogSnapshot).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
