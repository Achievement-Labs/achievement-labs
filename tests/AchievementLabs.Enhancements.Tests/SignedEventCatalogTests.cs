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
        public int TitleDownloads, ReleaseCalls;
        public TaskCompletionSource? TitleGate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            // The legacy release-zip fallback talks to GitHub; count it and answer 403 so it leaves its cache untouched.
            if (url.StartsWith("https://api.github.com/repos/Achievement-Labs/achievement-labs/", StringComparison.Ordinal)) { ReleaseCalls++; return new HttpResponseMessage(HttpStatusCode.Forbidden); }
            Assert(url.StartsWith(Base, StringComparison.Ordinal), "Only the catalog endpoint is contacted: " + url);
            Assert(request.Method == HttpMethod.Get && !request.Headers.Contains("Authorization"), "Public read only; no tokens");
            if (Offline) throw new HttpRequestException("offline");
            if (url.Contains("/titles/", StringComparison.Ordinal)) { TitleDownloads++; if (TitleGate is { } gate) await gate.Task.WaitAsync(ct); }
            return Files.TryGetValue(url, out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    // Two requests for achievement 1, so a checkpoint can sit between them.
    static JObject TwoRequestBlock(string value) => JObject.Parse("{\"Enabled\":true,\"FullySupported\":true,\"Achievements\":{\"1\":{\"Request1\":{\"0\":{\"ReplacementType\":\"Replace\",\"Target\":\"REPLACEACH\",\"Replacement\":\"" + value + "\"}},\"Request2\":{\"0\":{\"ReplacementType\":\"Replace\",\"Target\":\"REPLACEACH\",\"Replacement\":\"" + value + "b\"}}}}}");
    static byte[] TwoRequestBundle(string id, int revision, string value) => Encoding.UTF8.GetBytes(new JObject
    { ["titleId"] = id, ["revision"] = revision, ["data"] = TwoRequestBlock(value), ["template"] = Template(id), ["stats"] = null }.ToString());
    /// <summary>Writes an indexed Events/ folder (index.json + per-title Data.json and template) for the given titles.</summary>
    static string EventsFolder(string root, params (string Id, string Value)[] titles)
    {
        Directory.CreateDirectory(root); var index = new JObject();
        foreach (var (id, value) in titles)
        {
            var block = Block(value); Directory.CreateDirectory(Path.Combine(root, id));
            File.WriteAllText(Path.Combine(root, id, "Data.json"), new JObject { [id] = block, ["SupportedTitleIDs"] = new JArray(id) }.ToString());
            File.WriteAllText(Path.Combine(root, id, id + ".json"), Template(id));
            var summary = (JObject)block.DeepClone(); summary.Remove("Achievements"); index[id] = summary;
        }
        index["SupportedTitleIDs"] = new JArray(titles.Select(t => t.Id)); File.WriteAllText(Path.Combine(root, "index.json"), index.ToString());
        return root;
    }
    static string ClientAchievement(EventCatalogClient client, string id, int request = 0) =>
        JObject.Parse(client.GetPayloadsAsync(id, "1", "0", default).GetAwaiter().GetResult().Payloads[request])["data"]!["baseData"]!["properties"]!["AchievementID"]!.ToString();
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

            // A title dropped from a later accepted manifest stays disabled; the bundled snapshot cannot revive it.
            Publish(server, key, Catalog(22, ("105", 1, Bundle("105", 1, "1"))));
            withSnapshot.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(!withSnapshot.LibrarySupport()["101"].Available && withSnapshot.LibrarySupport()["101"].CatalogSource == "catalog", "Removed title is disabled, not bundled");
            bool removedBlocked = false;
            try { withSnapshot.PayloadsAsync("101", "1", "0", default).GetAwaiter().GetResult(); } catch (InvalidOperationException) { removedBlocked = true; }
            Assert(removedBlocked && !Open(Snapshot(bundledSnapshot)).LibrarySupport()["101"].Available, "Removed title stays off after restart");
            Publish(server, key, Catalog(23, ("101", 8, Bundle("101", 8, "8")), ("105", 1, Bundle("105", 1, "1"))));
            withSnapshot.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(Achievement(withSnapshot, "101") == "8", "Re-published title becomes available again");

            // Review fixes ---------------------------------------------------------------------------------------

            // A snapshot title listed in an accepted manifest at the same revision (never downloaded or cached) and then
            // dropped must stay unavailable, including after restart.
            var tombCache = Path.Combine(dir, "tomb");
            var tombSnapshot = Snapshot(Catalog(30, ("401", 5, Bundle("401", 5, "5")), ("402", 5, Bundle("402", 5, "5"))));
            var tomb = new SignedEventCatalog(tombCache, [], tombSnapshot, pem);
            Publish(server, key, Catalog(30, ("401", 5, Bundle("401", 5, "5")), ("402", 5, Bundle("402", 5, "5"))));
            downloads = server.TitleDownloads; tomb.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(server.TitleDownloads == downloads && Achievement(tomb, "401") == "5", "Same-revision snapshot title is served bundled without download");
            Publish(server, key, Catalog(31, ("402", 5, Bundle("402", 5, "5"))));
            tomb.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(!tomb.LibrarySupport()["401"].Available && tomb.LibrarySupport()["402"].Available, "Never-cached snapshot title dropped by the catalog is unavailable");
            Assert(!new SignedEventCatalog(tombCache, [], tombSnapshot, pem).LibrarySupport()["401"].Available, "Dropped never-cached title stays off after restart");

            // state.json is not authoritative: a corrupted floor is ignored and real manifests are still accepted.
            var floorCache = Path.Combine(dir, "floor");
            Publish(server, key, Catalog(5, ("301", 5, Bundle("301", 5, "5"))));
            new SignedEventCatalog(floorCache, [], null, pem).RefreshAsync(http, true).GetAwaiter().GetResult();
            var floorState = Path.Combine(floorCache, "state.json");
            var tamperedState = JObject.Parse(File.ReadAllText(floorState)); tamperedState["manifestRevision"] = 2147483647; File.WriteAllText(floorState, tamperedState.ToString());
            var floorCatalog = new SignedEventCatalog(floorCache, [], null, pem);
            Assert(floorCatalog.AcceptedRevision == 5 && Achievement(floorCatalog, "301") == "5", "Floor and titles come from the verified cached manifest, not state.json");
            Publish(server, key, Catalog(6, ("301", 6, Bundle("301", 6, "6"))));
            Assert(floorCatalog.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Applied && Achievement(floorCatalog, "301") == "6", "Real newer manifest accepted despite a corrupted state floor");
            // A cached manifest whose signature no longer verifies discards the cache: floor 0, bundled data wins.
            File.WriteAllText(Path.Combine(floorCache, "manifests", "6.json.sig"), Convert.ToBase64String(new byte[64]));
            var discarded = new SignedEventCatalog(floorCache, [], Snapshot(Catalog(4, ("301", 4, Bundle("301", 4, "4")))), pem);
            Assert(discarded.AcceptedRevision == 0 && Achievement(discarded, "301") == "4", "Unverifiable cache is discarded and bundled data wins");

            // Title revision above its manifest revision breaks the shared scale and is rejected.
            Publish(server, key, Catalog(7, ("301", 8, Bundle("301", 8, "8"))));
            Assert(discarded.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Rejected && discarded.Status.Contains("above manifest revision"), "Title revision above manifest revision rejected");

            // Only P-256 keys verify (curve OID 1.2.840.10045.3.1.7), even with a valid signature from another curve.
            using (var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            {
                var other = new SignedEventCatalog(Path.Combine(dir, "p384"), [], null, p384.ExportSubjectPublicKeyInfoPem());
                Publish(server, p384, Catalog(9, ("301", 9, Bundle("301", 9, "9"))));
                Assert(other.RefreshAsync(http, true).GetAwaiter().GetResult() == CatalogRefreshOutcome.Rejected, "Non-P-256 key rejected");
            }

            // End to end through EventCatalogClient: snapshot marker (50) above title revisions, plus an Events/-only title.
            var clientRoot = Path.Combine(dir, "client");
            var events = EventsFolder(Path.Combine(clientRoot, "Events"), ("501", "events"), ("502", "events"));
            var clientSnapshot = Snapshot(Catalog(50, ("502", 30, TwoRequestBundle("502", 30, "s30")), ("503", 30, Bundle("503", 30, "s30"))));
            var clientSigned = new SignedEventCatalog(Path.Combine(clientRoot, "signed"), ["345903534"], clientSnapshot, pem);
            using var client = new EventCatalogClient(clientSigned, events, Path.Combine(clientRoot, "cache"));
            Publish(server, key, Catalog(60, ("502", 40, TwoRequestBundle("502", 40, "c40")), ("503", 20, Bundle("503", 20, "c20")), ("504", 60, Bundle("504", 60, "c60"))));
            var releaseCalls = server.ReleaseCalls;
            client.RefreshUpstreamAsync(http, true, default).GetAwaiter().GetResult();
            Assert(server.ReleaseCalls == releaseCalls, "Applied signed refresh does not touch the release-zip fallback");
            Assert(client.GetTitleAsync("502", default).GetAwaiter().GetResult().AchievementIds.SequenceEqual(new[] { "1" }), "Title mapping loads");
            Assert(ClientAchievement(client, "502") == "c40" && client.TitleCatalogStatus("502") is { Revision: 40, Source: "catalog" }, "Cache title 40 beats snapshot title 30 even though the marker is 50");
            Assert(ClientAchievement(client, "503") == "s30" && client.TitleCatalogStatus("503") is { Revision: 30, Source: "bundled" }, "Snapshot title 30 beats catalog title 20");
            client.GetTitleAsync("501", default).GetAwaiter().GetResult();
            Assert(ClientAchievement(client, "501") == "events" && client.TitleCatalogStatus("501") is { Revision: 50, Source: "bundled" }, "Events/-only title answers from Events/ at the marker revision");
            var support = client.GetLibrarySupport();
            Assert(support["501"].CatalogSource == "bundled" && support["502"].CatalogRevision == 40 && support["504"].Available, "Library support carries per-title revision and source");

            // Refresh between the two requests of one achievement: the resume checkpoint must restart at request 1.
            var entry = new AchievementLabs.Models.AutoUnlockQueueEntry { AchievementId = "1" };
            var before2 = client.PayloadFingerprint("502", "1");
            Assert(before2 != null && EventCheckpoint.Resume(entry, before2, 2, out _) == 0, "Fresh entry starts at 0");
            EventCheckpoint.Advance(entry, 1, before2);
            var persisted = Newtonsoft.Json.JsonConvert.DeserializeObject<AchievementLabs.Models.AutoUnlockQueueEntry>(Newtonsoft.Json.JsonConvert.SerializeObject(entry))!;
            Assert(persisted.EventRequestIndex == 1 && persisted.EventPayloadFingerprint == before2, "Checkpoint fingerprint survives restart");
            Assert(EventCheckpoint.Resume(persisted, client.PayloadFingerprint("502", "1"), 2, out var noReset) == 1 && noReset == null, "Unchanged recipe resumes at request 2");
            Publish(server, key, Catalog(61, ("502", 61, TwoRequestBundle("502", 61, "c61")), ("503", 20, Bundle("503", 20, "c20")), ("504", 60, Bundle("504", 60, "c60"))));
            client.RefreshUpstreamAsync(http, true, default).GetAwaiter().GetResult();
            var after2 = client.PayloadFingerprint("502", "1");
            Assert(after2 != before2 && EventCheckpoint.Resume(persisted, after2, 2, out var resetReason) == 0 && persisted.EventRequestIndex == 0
                && resetReason != null && resetReason.Contains("changed"), "Changed recipe after a mid-achievement refresh restarts at request 1 and says why");
            Assert(ClientAchievement(client, "502", 0) == "c61" && ClientAchievement(client, "502", 1) == "c61b", "Restarted achievement sends the new recipe's requests");
            var legacyEntry = new AchievementLabs.Models.AutoUnlockQueueEntry { AchievementId = "1", EventRequestIndex = 1 };
            Assert(EventCheckpoint.Resume(legacyEntry, after2, 2, out _) == 1, "Checkpoints saved before fingerprints keep the old resume behaviour");

            // Release-zip fallback only on Unreachable, never on Rejected.
            releaseCalls = server.ReleaseCalls;
            Publish(server, key, Catalog(62, ("502", 61, TwoRequestBundle("502", 61, "c61"))), signatureOverride: new byte[64]);
            client.RefreshUpstreamAsync(http, true, default).GetAwaiter().GetResult();
            Assert(server.ReleaseCalls == releaseCalls && ClientAchievement(client, "502") == "c61", "Rejected signed catalog does not fall back to the release zip");
            server.Offline = true;
            client.RefreshUpstreamAsync(http, true, default).GetAwaiter().GetResult();
            server.Offline = false;
            Assert(server.ReleaseCalls == releaseCalls + 1 && client.UpstreamStatus.Contains("Release-zip fallback") && ClientAchievement(client, "502") == "c61", "Unreachable signed catalog falls back to the release zip; data retained");

            // A custom pack overrides an official title and is flagged.
            var pack = new JObject { ["schemaVersion"] = 1, ["titles"] = new JObject { ["504"] = new JObject { ["template"] = Template("504"),
                ["recipes"] = new JObject { ["recipes"] = new JObject { ["1"] = new JArray { new JArray { new JObject { ["target"] = "REPLACEACH", ["value"] = "custom" } } } } } } } };
            Assert(client.ImportEventPackAsync(pack.ToString(), default).GetAwaiter().GetResult() == 1, "Custom pack imported");
            Assert(client.GetLibrarySupport()["504"] is { CustomOverride: true, CatalogSource: "custom" } && client.TitleCatalogStatus("504") is { OverridesOfficial: true }, "Custom override is flagged");
            Assert(ClientAchievement(client, "504") == "custom", "Custom pack wins for its title");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("PASS: signed catalog: precedence, per-title isolation, signature, rollback, sha mismatch, no-op refresh, bundled snapshot, refresh during queue, fallback signal, never-cached tombstones, signed cache authority, revision scale, P-256 OID, client end-to-end, checkpoint reset, fallback only on Unreachable, custom override.");
    }
}
