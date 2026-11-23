using System.Net;
using System.Security.Cryptography;
using System.Text;
using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

public static class SignedEventCatalogTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    const string Base = "https://achievement-labs.github.io/achievement-labs-catalog/v1/";

    sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public bool Offline;
        public int TitleDownloads;
        public TaskCompletionSource? TitleGate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Assert(url.StartsWith(Base, StringComparison.Ordinal), "Only the catalog endpoint is contacted: " + url);
            Assert(request.Method == HttpMethod.Get && !request.Headers.Contains("Authorization"), "Public read only; no tokens");
            if (Offline) throw new HttpRequestException("offline");
            if (url.Contains("/titles/", StringComparison.Ordinal)) { TitleDownloads++; if (TitleGate is { } gate) await gate.Task.WaitAsync(ct); }
            return Files.TryGetValue(url, out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static JObject Block(string value) => JObject.Parse("{\"Enabled\":true,\"FullySupported\":true,\"Achievements\":{\"1\":{\"Id\":{\"ReplacementType\":\"Replace\",\"Target\":\"REPLACEACH\",\"Replacement\":\"" + value + "\"}}}}");
    static string Template(string id) => new JObject { ["name"] = "Microsoft.XboxLive.T" + id + ".AchievementUnlock", ["data"] = new JObject { ["baseData"] = new JObject { ["titleId"] = id, ["serviceConfigId"] = "11111111-2222-3333-4444-555555555555", ["name"] = "AchievementUnlock", ["userId"] = "REPLACEXUID", ["properties"] = new JObject { ["AchievementID"] = "REPLACEACH" } } } }.ToString();
    static byte[] Bundle(string id, int revision, string value, string? templateId = null) => Encoding.UTF8.GetBytes(new JObject
    { ["titleId"] = id, ["revision"] = revision, ["data"] = Block(value), ["template"] = Template(templateId ?? id), ["stats"] = null }.ToString());

    /// <summary>Builds a catalog: (id, revision, bundle bytes or null for disabled). Returns manifest bytes and title files by relative path.</summary>
    static (byte[] Manifest, Dictionary<string, byte[]> Titles) Catalog(int revision, params (string Id, int Revision, byte[]? Bytes)[] titles)
    {
        var entries = new JObject(); var files = new Dictionary<string, byte[]>();
        foreach (var (id, rev, bytes) in titles)
        {
            if (bytes == null) { entries[id] = new JObject { ["revision"] = rev, ["enabled"] = false, ["name"] = "T" + id, ["fullySupported"] = false, ["partiallySupported"] = false, ["achievements"] = 0 }; continue; }
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var path = "titles/" + id + "." + sha[..12] + ".json"; files[path] = bytes;
            entries[id] = new JObject { ["revision"] = rev, ["sha256"] = sha, ["size"] = bytes.Length, ["path"] = path, ["name"] = "T" + id, ["enabled"] = true, ["fullySupported"] = true, ["partiallySupported"] = false, ["achievements"] = 1 };
        }
        var manifest = new JObject { ["schema"] = 1, ["revision"] = revision, ["commit"] = new string('a', 40), ["generatedUtc"] = "2026-11-23T00:00:00Z", ["titles"] = entries };
        return (Encoding.UTF8.GetBytes(manifest.ToString()), files);
    }
    static void Publish(Server server, ECDsa key, (byte[] Manifest, Dictionary<string, byte[]> Titles) catalog, byte[]? signatureOverride = null)
    {
        server.Files.Clear();
        server.Files[Base + "manifest.json"] = catalog.Manifest;
        server.Files[Base + "manifest.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(signatureOverride
            ?? key.SignData(catalog.Manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        foreach (var file in catalog.Titles) server.Files[Base + file.Key] = file.Value;
    }
    static string Achievement(SignedEventCatalog catalog, string id) =>
        JObject.Parse(catalog.PayloadsAsync(id, "1", "0", default).GetAwaiter().GetResult().Payloads[0])["data"]!["baseData"]!["properties"]!["AchievementID"]!.ToString();
    static Func<string, byte[]?> Snapshot((byte[] Manifest, Dictionary<string, byte[]> Titles) catalog) =>
        path => path == "manifest.json" ? catalog.Manifest : catalog.Titles.GetValueOrDefault(path);

    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "labs-signed-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = key.ExportSubjectPublicKeyInfoPem();
            var server = new Server(); using var http = new HttpClient(server);
            var cache = Path.Combine(dir, "cache");
            SignedEventCatalog Open(Func<string, byte[]?>? snapshot = null) => new(cache, ["345903534"], snapshot, pem);

            // Precedence rules (pure): newest revision wins; ties catalog > legacy zip > bundled; stale zip (-1) never shadows bundled.
            Assert(CatalogPrecedence.Choose(null, -1, 0) == CatalogPrecedence.Tier.Bundled, "Stale release-zip cache cannot shadow bundled exe data");
            Assert(CatalogPrecedence.Choose(null, 0, 0) == CatalogPrecedence.Tier.Legacy, "Zip from a newer release wins a revision tie with Events/");
            Assert(CatalogPrecedence.Choose(3, 5, 5) == CatalogPrecedence.Tier.Legacy && CatalogPrecedence.Choose(5, 5, 5) == CatalogPrecedence.Tier.Catalog, "Ties go to the signed catalog");
            Assert(CatalogPrecedence.Choose(3, null, 5) == CatalogPrecedence.Tier.Bundled, "Lower-revision cache loses to bundled");

            // Initial accept: three good titles and one disabled title.
            var v1 = Catalog(10, ("101", 1, Bundle("101", 1, "1")), ("102", 1, Bundle("102", 1, "1")), ("103", 1, Bundle("103", 1, "1")), ("104", 1, null));
            Publish(server, key, v1);
            var catalog = Open();
            Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Applied && server.TitleDownloads == 3, "Initial manifest downloads enabled titles: " + catalog.Status);
            Assert(Achievement(catalog, "101") == "1" && !catalog.LibrarySupport()["104"].Available && catalog.AcceptedRevision == 10, "Accepted titles serve payloads; disabled titles are unavailable");
            Assert(catalog.LibrarySupport()["101"] is { CatalogRevision: 1, CatalogSource: "catalog", CatalogError: null }, "Per-title revision and source are exposed");
            var statePath = Path.Combine(cache, "state.json");
            Assert(File.Exists(statePath) && !Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(), "Cache is written atomically");

            // Unchanged manifest: no title downloads.
            var downloads = server.TitleDownloads;
            Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Unchanged && server.TitleDownloads == downloads, "Unchanged manifest causes no downloads");
            Assert(catalog.RefreshAsync(http).GetAwaiter().GetResult() == CatalogRefreshOutcome.Throttled, "Hourly throttle applies to unforced checks");

            // One bad title among good ones: 102 renders with the wrong title id; 101 and 103 still update; 102 keeps its last good version.
            var v2 = Catalog(11, ("101", 2, Bundle("101", 2, "2")), ("102", 2, Bundle("102", 2, "2", templateId: "999")), ("103", 2, Bundle("103", 2, "2")), ("104", 1, null));
            Publish(server, key, v2);
            Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Applied, "Partial update applies");
            Assert(Achievement(catalog, "101") == "2" && Achievement(catalog, "103") == "2" && Achievement(catalog, "102") == "1", "Bad title keeps its previous good version while others apply");
            Assert(catalog.LastError("102") != null && catalog.LastError("101") == null && catalog.Status.Contains("1 title failures: 102"), "Per-title failure recorded and shown: " + catalog.Status);
            Assert(Achievement(Open(), "102") == "1" && Open().LastError("102") != null, "Last good version and failure survive restart");

            // sha mismatch: the served file differs from the manifest digest.
            var v3 = Catalog(12, ("101", 3, Bundle("101", 3, "3")), ("102", 2, Bundle("102", 2, "2", templateId: "999")), ("103", 3, Bundle("103", 3, "3")), ("104", 1, null));
            var tampered = v3.Titles.Keys.Single(k => k.StartsWith("titles/103.", StringComparison.Ordinal));
            var forged = Bundle("103", 3, "9"); v3.Titles[tampered] = forged;
            Publish(server, key, v3);
            catalog.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(Achievement(catalog, "101") == "3" && Achievement(catalog, "103") == "2" && catalog.LastError("103") is { } shaError && (shaError.Contains("sha256") || shaError.Contains("size")), "sha/size mismatch rejects only that title");

            // Bad signature: nothing changes on disk or in memory.
            var before = File.ReadAllBytes(statePath);
            var v4 = Catalog(13, ("101", 4, Bundle("101", 4, "4")));
            Publish(server, key, v4, signatureOverride: new byte[64]);
            Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Rejected && catalog.Status.Contains("signature"), "Bad signature rejected");
            Assert(File.ReadAllBytes(statePath).SequenceEqual(before) && Achievement(catalog, "101") == "3" && catalog.AcceptedRevision == 12, "Bad signature cannot wipe data");
            using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                Publish(server, other, v4);
                Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Rejected, "Manifest signed by another key rejected");
            }

            // Lower manifest revision (validly signed): rollback rejected.
            var old = Catalog(5, ("101", 1, Bundle("101", 1, "1")));
            Publish(server, key, old);
            Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Rejected && catalog.Status.Contains("older"), "Lower manifest revision rejected");
            Assert(File.ReadAllBytes(statePath).SequenceEqual(before) && Achievement(catalog, "101") == "3", "Rollback cannot wipe data");

            // Unreachable / unconfigured key: report Unreachable so the client falls back to the release zip; keep data.
            server.Offline = true;
            Assert(catalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Unreachable && Achievement(catalog, "101") == "3", "Unreachable catalog keeps data and signals fallback");
            server.Offline = false; Publish(server, key, v3);
            Assert(new SignedEventCatalog(cache, [], null, SignedEventCatalog.PublicKeyPem).RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Unreachable
                && SignedEventCatalog.IsPlaceholderKey(SignedEventCatalog.PublicKeyPem), "Placeholder signing key falls back instead of trusting unsigned data");

            // Stale cache vs newer bundled snapshot: bundled must win, and the cache's older copy is not re-downloaded.
            var bundledSnapshot = Catalog(20, ("101", 7, Bundle("101", 7, "7")), ("105", 1, Bundle("105", 1, "1")));
            var withSnapshot = Open(Snapshot(bundledSnapshot));
            Assert(withSnapshot.BundledRevision == 20 && Achievement(withSnapshot, "101") == "7", "Newer bundled title beats the stale cache");
            Assert(withSnapshot.LibrarySupport()["101"] is { CatalogSource: "bundled", CatalogRevision: 7 } && Achievement(withSnapshot, "103") == "2" && Achievement(withSnapshot, "105") == "1", "Each title independently picks its newest source");
            Publish(server, key, Catalog(21, ("101", 3, Bundle("101", 3, "3"))));
            downloads = server.TitleDownloads;
            withSnapshot.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(server.TitleDownloads == downloads && Achievement(withSnapshot, "101") == "7", "Catalog titles older than the bundled snapshot are skipped");

            // Refresh while a queue is active: in-flight payload builds use the old snapshot until the atomic swap.
            var queueCache = Path.Combine(dir, "queue");
            var queued = new SignedEventCatalog(queueCache, [], null, pem);
            Publish(server, key, Catalog(1, ("201", 1, Bundle("201", 1, "1"))));
            queued.RefreshAsync(http, true).GetAwaiter().GetResult();
            Publish(server, key, Catalog(2, ("201", 2, Bundle("201", 2, "2"))));
            server.TitleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = Task.Run(() => queued.RefreshAsync(http, false, default, startup: true));
            var seen = new List<string>();
            for (int i = 0; i < 20; i++) seen.Add(Achievement(queued, "201"));
            Assert(seen.All(v => v == "1") && !refresh.IsCompleted, "Queue keeps unlocking on the old snapshot while refresh downloads");
            server.TitleGate.SetResult();
            Assert(refresh.GetAwaiter().GetResult() == CatalogRefreshOutcome.Applied && Achievement(queued, "201") == "2", "Next achievement uses the new snapshot");
            server.TitleGate = null;

            // Corrupt cache state fails closed to bundled data.
            File.WriteAllText(statePath, "broken");
            var corrupt = Open(Snapshot(bundledSnapshot));
            Assert(corrupt.LibrarySupport()["101"].CatalogSource == "bundled" && corrupt.AcceptedRevision == 0 && Achievement(corrupt, "101") == "7", "Corrupt cache falls back to bundled snapshot");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("PASS: signed catalog: precedence, per-title isolation, signature, rollback, sha mismatch, no-op refresh, bundled snapshot, refresh during queue, fallback signal.");
    }
}
