using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

static class PreviewFeatureChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static JObject Metadata(string requirement) => JObject.Parse("{\"achievements\":[{\"serviceConfigId\":\"11111111-2222-3333-4444-555555555555\",\"progression\":{\"requirements\":[{\"id\":\""+requirement+"\"}]}}]}");
    static JObject PackTitle(string id, string classification = "Testing", string value = "1") => new()
    {
        ["template"] = classification == "Unsupported" ? "" : Template(id),
        ["status"] = new JObject { ["classification"] = classification, ["count"] = classification == "Unsupported" ? 0 : 1, ["reason"] = classification == "Unsupported" ? "No mapping" : JValue.CreateNull() },
        ["recipes"] = new JObject { ["recipes"] = classification == "Unsupported" ? new JObject() : new JObject { ["1"] = new JArray { new JArray { new JObject { ["target"] = "REPLACEACH", ["value"] = value } } } } }
    };
    static string Template(string id) => "{\"name\":\"Microsoft.XboxLive.T" + id + ".Test\",\"data\":{\"baseData\":{\"titleId\":\""+id+"\",\"serviceConfigId\":\"11111111-2222-3333-4444-555555555555\",\"name\":\"Test\",\"value\":REPLACEACH}}}";
    sealed class ReadFixture : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "achievements.xboxlive.com", "Support checks are GET only on Xbox achievement host");
            Check(request.Headers.GetValues("x-xbl-contract-version").Single() == "4", "Modern metadata contract");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Metadata(Guid.Empty.ToString()).ToString()) });
        }
    }
    public static async Task RunAsync()
    {
        Check(LibrarySupportDiscovery.Classify(Metadata(Guid.Empty.ToString())) == false, "Sentinel requirement confirms title-based metadata");
        Check(LibrarySupportDiscovery.Classify(Metadata("11111111-2222-3333-4444-555555555555")) == true, "Nonzero requirement confirms events");
        Check(LibrarySupportDiscovery.Classify(Metadata("bad")) == null, "Malformed requirement is unknown");
        var page = Metadata(Guid.Empty.ToString()); page["pagingInfo"] = new JObject { ["continuationToken"] = "next" };
        Check(LibrarySupportDiscovery.Classify(page) == null, "Incomplete page cannot confirm title-based metadata");
        Check(LibrarySupportDiscovery.Classify(new JObject { ["achievements"] = new JArray() }) == null, "Empty responses remain unknown");
        var invalid = Metadata(Guid.Empty.ToString()); invalid["achievements"]![0]!["serviceConfigId"] = Guid.Empty.ToString();
        Check(LibrarySupportDiscovery.Classify(invalid) == null, "Invalid SCID remains unknown");
        Check(LibrarySupportBadge.Resolve(false, false, false, false, false, 0, 30).Kind == "supported", "Title-based support");
        Check(LibrarySupportBadge.Resolve(false, true, true, true, true, 20, 30).Kind == "partial", "Missing mappings cannot claim full support");
        Check(LibrarySupportBadge.Resolve(false, true, true, false, true, 30, 30).Kind == "unsupported", "Disabled mapping unsupported");
        Check(LibrarySupportBadge.Resolve(true, false, true, true, true, 30, 30).Kind == "unknown", "Legacy tool availability is not online unlock support");
        using (var fixture = new ReadFixture()) using (var http = new HttpClient(fixture))
        {
            Check(await LibrarySupportDiscovery.ReadAsync(http, "synthetic", "0", "7", default) == false && fixture.Calls == 1, "Read-only discovery fixture");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await LibrarySupportDiscovery.ReadAsync(http, "synthetic", "0", "7", cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        }
        var exported = SharedUnlockMethods.Export(new Dictionary<string,bool> { ["7"] = true, ["8"] = false });
        Check(SharedUnlockMethods.Parse(exported).Count == 2 && !exported.Contains("synthetic"), "Confirmed export contains methods only");
        foreach (var bad in new[] { "{\"schemaVersion\":1,\"methods\":{\"7\":true,\"7\":false}}", "{\"schemaVersion\":1,\"methods\":{\"7\":\"yes\"}}", "{\"schemaVersion\":1,\"methods\":{\"07\":true}}" })
        { bool rejected = false; try { SharedUnlockMethods.Parse(bad); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Malformed support catalogue rejected"); }
        var folder = Path.Combine(Path.GetTempPath(), "labs-preview-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var cache = Path.Combine(folder, "cache.json");
            var updates = new UpstreamEventCatalogue(cache, ["99"], bundledTitleIds: ["7"]);
            async Task<int> Import(JObject entry, string title = "7", int version = 2) => await updates.ImportPackAsync(new JObject { ["schemaVersion"] = version, ["titles"] = new JObject { [title] = entry } }.ToString());
            Check(await Import(PackTitle("7"), version: 1) == 0, "v1 pack cannot replace bundled title");
            Check(await Import(PackTitle("7", "Supported")) == 1, "v2 pack updates bundled title");
            Check(updates.LibrarySupport()["7"] is { Available: true, FullySupported: true, Mapped: 1 }, "Imported support status reflects mapping");
            Check((await updates.PayloadsAsync("7", "1", "0", default)).Payloads.Single().Contains("\"value\":1"), "Updated mapping renders through actual consumer");
            Check(await Import(PackTitle("7", "Supported", "2")) == 1, "Existing cached mapping updated");
            Check((await updates.PayloadsAsync("7", "1", "0", default)).Payloads.Single().Contains("\"value\":2"), "New mapping takes effect");
            var before = File.ReadAllText(cache); bool rejected = false;
            try { await Import(PackTitle("8", "Supported")); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(cache) == before, "Wrong title identity rolls back disk and memory");
            Check(await Import(PackTitle("99", "Supported"), "99") == 0, "Protected title cannot be overwritten");
            Check(await Import(PackTitle("7", "Unsupported")) == 1 && !updates.Contains("7"), "Disabled mapping ceases payload availability");
            var restarted = new UpstreamEventCatalogue(cache, []);
            Check(restarted.KnownTitleIds.Contains("7") && !restarted.LibrarySupport()["7"].Available, "Disabled override survives restart");
            // Force an explicit cached disabled override over a valid local title: neither read nor payload lookup may fall back.
            var localFolder = Path.Combine(folder, "local"); Directory.CreateDirectory(localFolder);
            var localBlock = new JObject { ["Achievements"] = new JObject { ["1"] = new JObject { ["Id"] = new JObject { ["ReplacementType"] = "Replace", ["Target"] = "REPLACEACH", ["Replacement"] = "1" } } } };
            await File.WriteAllTextAsync(Path.Combine(localFolder, "Data.json"), new JObject { ["7"] = localBlock, ["SupportedTitleIDs"] = new JArray("7") }.ToString());
            await File.WriteAllTextAsync(Path.Combine(localFolder, "7.json"), Template("7"));
            using (var client = new EventCatalogClient(true, () => localFolder))
            {
                Check(client.GetLibrarySupport()["7"].Available, "Local fixture mapping initially available");
                typeof(EventCatalogClient).GetField("additions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(client, restarted);
                Check(!client.GetLibrarySupport()["7"].Available && (await client.GetTitleAsync("7", default)).AchievementIds.Length == 0, "Disabled cached override takes precedence over valid bundled/local recipe");
                var status = await client.GetCatalogAsync(default);
                Check(!status.TitleIds.Contains("7") && !(status.TestingTitleIds ?? []).Contains("7"), "Disabled override removed from actionable catalog status");
                bool disabled = false; try { await client.GetPayloadsAsync("7", "1", "0", default); } catch (InvalidOperationException) { disabled = true; }
                Check(disabled, "Disabled payload never falls back to local recipe or remote licensing");
            }
            var saved = JObject.Parse(File.ReadAllText(cache)); var data = (JObject)saved["data"]!;
            Check(data.Properties().Last().Name == "SupportedTitleIDs" && ((JObject)data["7"]!).Properties().All(p => p.Name is "Enabled" or "FullySupported" or "Achievements"), "Runtime cache excludes research and keeps supported IDs last");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS: support classification, badges, read-only discovery, strict exports, v2 mapping refresh, disabled persistence, rendering and rollback. Synthetic responses only.");
    }
}
