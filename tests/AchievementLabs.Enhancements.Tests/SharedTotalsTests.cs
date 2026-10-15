using System.Net;
using System.Text.Json;
using AchievementLabs.Core;

public static class SharedTotalsTests
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Handler(string json, bool fail = false) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert(request.RequestUri == SharedAchievementTotals.PublishedUri && request.Method == HttpMethod.Get,
                "Only the public totals document is fetched");
            Assert(request.Content == null && !request.Headers.Any(), "Shared lookup sends no account headers or body");
            return Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(json) });
        }
    }
    public static void Run()
    {
        var totals = SharedAchievementTotals.Bundled();
        Assert(totals.Count == 937, "All 937 successful cached definition totals are bundled");
        Assert(totals.TryGet("907086072", "XboxOne / XboxSeries", out var console) && console == 58, "Console edition total");
        Assert(totals.TryGet("1805003112", "PC", out var pc) && pc == 58, "Separate PC edition total");
        Assert(!totals.TryGet("907086072", "PC", out _) && !totals.TryGet("1805003112", "XboxOne", out _), "Title ID and matching platform both required");
        Assert(!totals.TryGet("1629064702", "Win32", out _), "Empty responses are not shared zero totals");
        string Document(int total, DateTimeOffset checkedAt) => JsonSerializer.Serialize(new {
            schemaVersion = 1, entries = new[] { new { titleId = "123", name = "Synthetic title", platforms = new[] { "XboxOne" }, total, checkedAt, endpoint = "Modern" } } });
        var updated = Document(20, DateTimeOffset.UtcNow);
        var folder = Path.Combine(Path.GetTempPath(), "labs-shared-" + Guid.NewGuid());
        var cache = Path.Combine(folder, "totals.json");
        try
        {
            using var model = new AchievementLabs.Desktop.DesktopModel();
            var gamesProperty = typeof(AchievementLabs.Desktop.DesktopModel).GetProperty("Games")!;
            gamesProperty.SetValue(model, new[] {
                new AchievementLabs.Desktop.Game("907086072", "Prey", "XboxOne", 5, 0, 50),
                new AchievementLabs.Desktop.Game("1805003112", "Prey PC", "PC", 1, 0, 10) });
            Assert(model.Games[0].Total == 58 && model.Games[0].Completed == 5 && model.Games[1].Completed == 1,
                "Inherited totals preserve each account's unlocked counts");
            var known = (Dictionary<string, int>)typeof(AchievementLabs.Desktop.DesktopModel)
                .GetField("knownLibraryTotals", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model)!;
            known["907086072"] = 60;
            gamesProperty.SetValue(model, new[] {
                new AchievementLabs.Desktop.Game("907086072", "Prey", "XboxOne", 5, 0, 50),
                new AchievementLabs.Desktop.Game("1805003112", "Prey PC", "PC", 1, 0, 10) });
            Assert(model.Games[0].Total == 60, "Local scanned totals take priority over the bundled catalogue");
            var exported = model.VerifiedTotalsForExport();
            Assert(exported.Length == 1 && exported[0].Total == 60 && exported[0].PersistentUnlocked == null && exported[0].HistoryUnlocked == null,
                "Full export contains cached successful totals and excludes shared-only entries/account progress");
            Directory.CreateDirectory(folder);
            var exportPath = Path.Combine(folder, "successful.csv");
            model.ExportVerifiedTotalsAsync(exportPath).GetAwaiter().GetResult();
            var exportedText = File.ReadAllText(exportPath);
            Assert(exportedText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2 &&
                !exportedText.Contains("unlocked", StringComparison.OrdinalIgnoreCase) && !exportedText.Contains("XUID"), "Shareable export has only title metadata and totals");
            var handler = new Handler(updated);
            using var http = new HttpClient(handler);
            totals.RefreshAsync(http, cache, CancellationToken.None).GetAwaiter().GetResult();
            Assert(handler.Calls == 1 && totals.TryGet("123", "XboxOne", out var count) && count == 20, "Online catalogue adds verified entries");
            totals.RefreshAsync(http, cache, CancellationToken.None).GetAwaiter().GetResult();
            Assert(handler.Calls == 1, "Fresh public cache avoids repeated downloads");
            totals.Merge(Document(10, DateTimeOffset.UtcNow.AddDays(-2)));
            Assert(totals.TryGet("123", "XboxOne", out count) && count == 20, "Older catalogue cannot replace newer counts");
            var offline = SharedAchievementTotals.Bundled();
            using var unavailable = new HttpClient(new Handler("", true));
            offline.RefreshAsync(unavailable, cache, CancellationToken.None, true).GetAwaiter().GetResult();
            Assert(offline.TryGet("123", "XboxOne", out count) && count == 20 && offline.Count == 938, "Offline uses bundled and cached entries");
            try { totals.Merge(Document(0, DateTimeOffset.UtcNow)); throw new Exception("Invalid catalogue accepted"); }
            catch (InvalidDataException) { }
            Assert(totals.TryGet("123", "XboxOne", out count) && count == 20, "Invalid update leaves existing totals intact");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
