using System.Net;
using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

public static class UpstreamEventCatalogueTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class Fixture : HttpMessageHandler
    {
        public string Commit = new('a', 40);
        public bool Fail, BadJson;
        public List<string> Paths = [];
        public JObject Data = new();
        public Dictionary<string,string> Templates = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            Assert(request.RequestUri.AbsoluteUri.StartsWith("https://api.github.com/repos/Achievement-Labs/achievement-labs/", StringComparison.Ordinal) || request.RequestUri.AbsoluteUri.StartsWith("https://raw.githubusercontent.com/Achievement-Labs/achievement-labs/", StringComparison.Ordinal), "Event metadata and templates use the canonical repository");
            Assert(request.Method == HttpMethod.Get, "Event updates only read public data");
            Assert(!request.Headers.Contains("Authorization") && !request.Headers.Contains("tickets"), "No account tokens in upstream request");
            var text = path.EndsWith("commits/main") ? new JObject { ["sha"] = Commit }.ToString()
                : path.EndsWith("Data.json") ? BadJson ? "broken" : Data.ToString()
                : Templates.GetValueOrDefault(Path.GetFileNameWithoutExtension(path));
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : text == null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(text ?? "") });
        }
    }
    static JObject Block() => JObject.Parse("""
        {"FullySupported":true,"Achievements":{"1":{"Id":{"ReplacementType":"Replace","Target":"REPLACEACH","Replacement":"1"}}}}
        """);
    static string Template(string id) => new JObject {
        ["name"] = "Microsoft.XboxLive.T" + id + ".AchievementUnlock",
        ["data"] = new JObject { ["baseData"] = new JObject { ["titleId"] = id, ["serviceConfigId"] = "11111111-2222-3333-4444-555555555555", ["name"] = "AchievementUnlock", ["userId"] = "REPLACEXUID", ["properties"] = new JObject { ["AchievementID"] = "REPLACEACH" } } }
    }.ToString();
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "achievement-labs-upstream-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "events.json");
            var fixture = new Fixture { Data = new JObject { ["99"] = Block(), ["101"] = Block(), ["102"] = Block(), ["103"] = Block(), ["SupportedTitleIDs"] = new JArray("99", "101", "102", "103") } };
            fixture.Templates["99"] = Template("99"); fixture.Templates["101"] = Template("101"); fixture.Templates["102"] = Template("999");
            using var http = new HttpClient(fixture);
            var catalogue = new UpstreamEventCatalogue(path, new[]{"99", "345903534"});
            catalogue.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(catalogue.TitleIds.SequenceEqual(new[]{"101"}), "Only complete, new, title-matching entry imported");
            Assert(!fixture.Paths.Any(p=>p.EndsWith("/99.json")), "Existing mappings cannot be replaced or fetched");
            Assert(catalogue.Status.Contains("2 entries skipped"), "Invalid and missing templates reported");
            var payload = catalogue.PayloadsAsync("101", "1", "0", default).GetAwaiter().GetResult();
            Assert(JObject.Parse(payload.Payloads[0])["data"]!["baseData"]!["properties"]!["AchievementID"]!.ToString()=="1", "Downloaded ordinary replacement works offline");
            var saved = File.ReadAllText(path); var savedData = (JObject)JObject.Parse(saved)["data"]!;
            Assert(savedData.Properties().Last().Name=="SupportedTitleIDs", "Persisted Data preserves final SupportedTitleIDs ordering");
            var loaded = new UpstreamEventCatalogue(path, new[]{"99", "345903534"});
            Assert(loaded.Contains("101"), "Validated cache survives restart without network");
            var before = fixture.Paths.Count;
            catalogue.RefreshAsync(http).GetAwaiter().GetResult();
            Assert(fixture.Paths.Count==before, "Hourly throttle makes no duplicate requests");
            catalogue.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(catalogue.Contains("101"), "Manual same-commit retry preserves cached titles");
            fixture.Commit = new('b',40); fixture.BadJson = true;
            catalogue.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(catalogue.Contains("101") && !File.ReadAllText(path).Contains(new string('b',40)), "Malformed upstream snapshot does not replace cache");
            fixture.BadJson=false; fixture.Fail=true;
            catalogue.RefreshAsync(http, true).GetAwaiter().GetResult();
            Assert(catalogue.Contains("101") && catalogue.Status.Contains("retained"), "Network failure keeps last valid additions");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            before=fixture.Paths.Count; bool stopped=false;
            try { catalogue.RefreshAsync(http,true,cancelled.Token).GetAwaiter().GetResult(); } catch(OperationCanceledException) { stopped=true; }
            Assert(stopped && fixture.Paths.Count==before, "Cancellation causes no requests or cache mutation");
            File.WriteAllText(path,"invalid cache");
            Assert(new UpstreamEventCatalogue(path,new[]{"99"}).TitleIds.Length==0, "Corrupt cache fails closed");
        }
        finally { Directory.Delete(directory,true); }
        Console.WriteLine("PASS: additive upstream event updates, reserved edition protection, title/schema validation, missing-template skips, cache restart, ordering, throttling, rollback and cancellation. Only fixture HTTP reads; no account calls.");
    }
}
