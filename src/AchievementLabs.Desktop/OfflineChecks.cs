using AchievementLabs.Core;
using AchievementLabs.Services.Gfwl;
using System.IO.MemoryMappedFiles;
using System.Diagnostics;
namespace AchievementLabs.Desktop;
internal static class OfflineChecks
{
    private static async Task CheckGfwlProtocolAsync()
    {
        var name = "AchievementLabs.GfwlFixture." + Guid.NewGuid().ToString("N");
        using var session = new GfwlAchievementSession(Environment.ProcessId, name);
        using var mapping = MemoryMappedFile.OpenExisting(name);
        using var view = mapping.CreateViewAccessor();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var unlocked = false;
        var engine = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = view.ReadInt32(0);
                if (command == 0x1000)
                {
                    view.Write(4, 1);
                    view.Write(8, 7);
                    var title = System.Text.Encoding.Unicode.GetBytes("Offline achievement\0");
                    view.WriteArray(12, title, 0, title.Length);
                    var description = System.Text.Encoding.Unicode.GetBytes("Fixture description\0");
                    view.WriteArray(8 + 0x804, description, 0, description.Length);
                    view.Write(8 + 0x1808, 15);
                    view.Write(8 + 0x1814, unlocked ? 0x20000 : 0);
                    view.Write(0, 0x2000);
                }
                else if (command == 0x1001)
                {
                    unlocked = view.ReadInt32(4) == 7;
                    view.Write(0, unlocked ? 0x2002 : 0x2003);
                }
                await Task.Delay(5, stop.Token);
            }
        }, stop.Token);
        try
        {
            var catalog = await session.ReadAchievementsAsync(stop.Token);
            if (catalog.Count != 1 || catalog[0].Id != 7 || catalog[0].Name != "Offline achievement" || catalog[0].Gamerscore != 15 || catalog[0].Unlocked)
                throw new Exception("GFWL shared-memory catalog decode failed.");
            var earned = await session.EarnAsync(7, stop.Token);
            if (!earned.Unlocked || earned.UnlockedOnline || earned.State != "Achieved offline")
                throw new Exception("GFWL shared-memory write or verification failed.");
        }
        finally
        {
            stop.Cancel();
            try { await engine; } catch (OperationCanceledException) { }
        }
    }
    private static async Task CheckWindows8Async(string root)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var body = System.Text.Encoding.UTF8.GetBytes("{\"achievement\":{\"id\":3}}");
        const string url = "https://progress.xboxlive.com/users/xuid(1)/progress/achievements/3?titleId=2";
        const string auth = "XBL3.0 x=offline;offline";
        var header = Convert.FromBase64String(Workflows.Windows8ViewModel.Sign(key, url, auth, body));
        if (header.Length != 76 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header) != 1) throw new Exception("Win8 signature wire format failed.");
        using var input = new MemoryStream();
        input.Write(header, 0, 4); input.WriteByte(0); input.Write(header, 4, 8); input.WriteByte(0);
        foreach (var part in new[] { "POST", new Uri(url).PathAndQuery, auth, System.Text.Encoding.UTF8.GetString(body) }) { input.Write(System.Text.Encoding.UTF8.GetBytes(part)); input.WriteByte(0); }
        if (!key.VerifyData(input.ToArray(), header[12..], System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new Exception("Win8 signature failed verification.");
        var state = Path.Combine(root, "bridge-state-test.json");
        using var vm = new Workflows.Windows8ViewModel { BridgeStatePath = state };
        var privateKey = key.ExportParameters(true).D!;
        await File.WriteAllTextAsync(state, System.Text.Json.JsonSerializer.Serialize(new { ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), xuid = "123", gamertag = "Offline fixture", xbl3_auth = auth, progress_signed_auth = auth, signing_key_hex = Convert.ToHexString(privateKey) }));
        await vm.LoadSessionCommand.ExecuteAsync(null);
        if (!vm.AccountSummary.Contains("Signed Windows 8 session ready")) throw new Exception("Win8 state/key import failed: " + vm.Status);
        await File.WriteAllTextAsync(state, "{\"ts\":0}");
        await vm.LoadSessionCommand.ExecuteAsync(null);
        if (!vm.AccountSummary.Contains("No session selected") || !vm.Status.Contains("stale")) throw new Exception("Stale Win8 session was accepted.");
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(privateKey);
        File.Delete(state);
        await CheckWindows8DirectAsync();
        using var catalogVm = new Workflows.Windows8ViewModel(() => ("XBL3.0 x=test;fake", "123"), new Windows8CatalogServer()) { TitleId = "1515264857" };
        await catalogVm.LoadAchievementsCommand.ExecuteAsync(null);
        if (catalogVm.Achievements.Count != 3 || catalogVm.Achievements.Count(a => a.Unlocked) != 1 || catalogVm.Achievements[0].Name != "Catalog name") throw new Exception("Win8 partial unlock history hid full catalog or overwrote metadata.");
        using var mainHttp = new System.Net.Http.HttpClient(new Windows8CatalogServer());
        var mainRows = await Windows8Achievements.ReadAsync(mainHttp, "XBL3.0 x=test;fake", "123", "1515264857", CancellationToken.None);
        if (mainRows.Length != 3 || mainRows.Count(a => a.Unlocked) != 1 || mainRows[0].Name != "Catalog name") throw new Exception("Main editor did not merge Windows 8 unlock history with its catalog.");
        using var historyHttp = new System.Net.Http.HttpClient(new Windows8HistoryOnlyServer());
        var historyRows = await Windows8Achievements.ReadAsync(historyHttp, "XBL3.0 x=test;fake", "123", "1297292136", CancellationToken.None);
        if (historyRows.Length != 1 || !historyRows[0].Unlocked) throw new Exception("Windows 8 history was not loaded when the title catalog returned 404.");
        catalogVm.StateFilter = "Locked";
        if (catalogVm.VisibleAchievements.Count() != 2) throw new Exception("Win8 locked filter failed.");
        catalogVm.Search = "3";
        if (catalogVm.VisibleAchievements.Single().Id != "3") throw new Exception("Win8 ID search failed.");
    }

    private sealed class Windows8CatalogServer : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != System.Net.Http.HttpMethod.Get) throw new Exception("Catalog test must be read only.");
            var uri = request.RequestUri!;
            var json = uri.Host == "progress.xboxlive.com"
                ? uri.Query.Contains("continuationToken=next")
                    ? "{\"achievements\":[{\"id\":3,\"name\":\"Third\",\"unlocked\":false}]}"
                    : "{\"achievements\":[{\"id\":1,\"name\":\"Catalog name\",\"unlocked\":false},{\"id\":2,\"name\":\"Second\",\"unlocked\":false}],\"pagingInfo\":{\"continuationToken\":\"next\"}}"
                : "{\"achievements\":[{\"id\":\"1\",\"name\":\"History name\",\"unlocked\":true}]}";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
        }
    }

    private sealed class Windows8HistoryOnlyServer : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.RequestUri!.Host == "progress.xboxlive.com"
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent("{\"achievements\":[{\"id\":\"1\",\"name\":\"From history\",\"unlocked\":true}]} ") });
    }

    private sealed class Windows8FakeServer : System.Net.Http.HttpMessageHandler
    {
        public int Writes, Requests;
        public bool ReportUnlocked;
        public System.Net.HttpStatusCode WriteStatus = System.Net.HttpStatusCode.OK;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.Headers.GetValues("Authorization").Single() != "XBL3.0 x=test;fake" || request.Headers.Contains("Signature")) throw new Exception("Direct route did not use app auth without bridge proof.");
            if (request.Method == System.Net.Http.HttpMethod.Post)
            {
                Writes++;
                using var json = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var record = json.RootElement.GetProperty("achievement");
                if (record.GetProperty("id").GetInt32() != 7 || record.GetProperty("platform").GetInt32() != 17 || record.GetProperty("titleId").GetInt32() != 1297292157) throw new Exception("Wrong legacy record.");
                return new(WriteStatus) { Content = new System.Net.Http.StringContent("{}") };
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{\"achievements\":[{\"id\":7,\"name\":\"Fixture\",\"unlocked\":" + (ReportUnlocked && Writes > 0 ? "true" : "false") + "}]}") };
        }
    }

    private static async Task CheckWindows8DirectAsync()
    {
        var active = (Authorization: "XBL3.0 x=test;fake", Xuid: "123");
        using var server = new Windows8FakeServer();
        using var direct = new Workflows.Windows8ViewModel(() => active, server);
        // First read should automatically choose the app session, without a file or helper.
        await direct.LoadAchievementsCommand.ExecuteAsync(null);
        if (direct.Achievements.Count != 1 || !direct.AccountSummary.Contains("App Xbox session")) throw new Exception("Default direct session/read failed.");
        direct.SelectedAchievement = direct.Achievements[0];
        await direct.SubmitSelectedCommand.ExecuteAsync(null);
        if (server.Writes != 1 || !direct.Status.Contains("NOT yet verified")) throw new Exception("Accepted request was incorrectly treated as an unlock.");
        server.ReportUnlocked = true;
        await direct.SubmitSelectedCommand.ExecuteAsync(null);
        if (!direct.Status.StartsWith("Verified unlocked:") || !direct.Achievements[0].Unlocked) throw new Exception("Confirmed unlock was not reported.");
        server.ReportUnlocked = false;
        await direct.LoadAchievementsCommand.ExecuteAsync(null);
        direct.SelectedAchievement = direct.Achievements[0];
        server.WriteStatus = System.Net.HttpStatusCode.Forbidden;
        await direct.SubmitSelectedCommand.ExecuteAsync(null);
        if (!direct.Status.Contains("403") || direct.Achievements[0].Unlocked) throw new Exception("Forbidden submission handling failed.");
        var writesBeforeSwitch = server.Writes;
        active = ("", "");
        await direct.SubmitSelectedCommand.ExecuteAsync(null);
        if (server.Writes != writesBeforeSwitch || direct.Achievements.Count != 0 || !direct.Status.Contains("changed or disconnected")) throw new Exception("Disconnected account retained write access.");
        await direct.LoadAchievementsCommand.ExecuteAsync(null);
        if (!direct.Status.Contains("Connect Xbox")) throw new Exception("Missing app session was not handled.");
    }
    public static async Task RunAsync(string output)
    {
        await CheckGfwlProtocolAsync();
        if (Workflows.StatsEditorViewModel.GetCatalogStatMode("322252289") != "Event" ||
            Workflows.StatsEditorViewModel.GetCatalogStatMode("309062877") != "Title")
            throw new Exception("Bundled stat-mode catalogs did not distinguish event-based and title-based titles.");
        if (DesktopModel.ConvertTitleIdToDecimal("0D174C79") != "219630713")
            throw new Exception("Title search did not convert the hexadecimal Xbox title ID to decimal.");
        if (DesktopModel.NormalizeLookalikes("Lord of the Сlick") != "Lord of the Click" ||
            !DesktopModel.SearchSpellings("Lord of the Click").Contains("Lord of the Сlick"))
            throw new Exception("Title search did not handle mixed-script lookalike letters.");
        var darkSoulsGfwl = new Game(Convert.ToUInt32("4E4D0FA1", 16).ToString(), "Dark Souls: Prepare to Die Edition", "GFWL", 0, 0, 0);
        if (!DesktopModel.IsKnownGfwlTitle(darkSoulsGfwl.Id) ||
            !DesktopModel.MatchesPlatform(darkSoulsGfwl, "GFWL") ||
            DesktopModel.MatchesPlatform(darkSoulsGfwl, "Xbox 360"))
            throw new Exception("GFWL title classification or library filter failed.");
        if (!DesktopModel.IsKnownWindows8Title("1297292136") ||
            !DesktopModel.IsKnownWindows8Title("1679995514") ||
            DesktopModel.ResolveWindows8TitleId("1679995514") != "1297292136" ||
            !DesktopModel.IsKnownWindows8Title("1297292157"))
            throw new Exception("Known Windows 8 titles were not recognized.");
        var root = Path.Combine(Path.GetFullPath(output), "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await CheckWindows8Async(root);
        var preferences = new DesktopPreferencesStore(Path.Combine(root, "settings.json"));
        var expected = new DesktopPreferences { EventsDirectory = root, SessionPath = Path.Combine(root, "auth.json"), MintAccent = true, RegionOverride = true };
        await preferences.SaveAsync(expected);
        if (await preferences.LoadAsync() != expected) throw new Exception("Preferences round trip failed.");
        var savedSessionPath = Path.Combine(root, "session.bin");
        var savedSession = new NativeSessionStore.SavedSession("Xbox App PC", new XboxAuthNet.OAuth.MicrosoftOAuthResponse { AccessToken = "offline-access", RawRefreshToken = "M.R3_BAY.offline-refresh", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });
        await NativeSessionStore.SaveAsync(savedSessionPath, savedSession);
        var sessionRoundTrip = await NativeSessionStore.ReadAsync(savedSessionPath);
        if (sessionRoundTrip.ProfileName != savedSession.ProfileName || sessionRoundTrip.OAuth.RawRefreshToken != savedSession.OAuth.RawRefreshToken || sessionRoundTrip.OAuth.AccessToken != savedSession.OAuth.AccessToken) throw new Exception("Protected session round trip failed.");
        if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(savedSessionPath)).Contains("offline-refresh")) throw new Exception("Session stored in plaintext.");
        var damagedSession = await File.ReadAllBytesAsync(savedSessionPath); damagedSession[^1] ^= 1; await File.WriteAllBytesAsync(savedSessionPath, damagedSession);
        try { await NativeSessionStore.ReadAsync(savedSessionPath); throw new Exception("Damaged session accepted."); } catch (System.Security.Cryptography.CryptographicException) { }
        await File.WriteAllTextAsync(Path.Combine(root, "Data.json"), "{\"1\":{},\"2\":{},\"SupportedTitleIDs\":[\"1\",\"2\",\"3\"]}");
        await File.WriteAllTextAsync(Path.Combine(root, "1.json"), "{}");
        var result = await CatalogInspector.InspectAsync(root);
        if (!result.SupportedLast || result.Titles != 2 || result.Supported != 3 || !result.Findings.Any(f => f.TitleId == "2" && f.Status == "Missing template") || !result.Findings.Any(f => f.TitleId == "3" && f.Status == "Missing block")) throw new Exception("Catalog missing-file checks failed.");
        await File.WriteAllTextAsync(Path.Combine(root, "Data.json"), "{\"SupportedTitleIDs\":[\"1\"],\"1\":{}}");
        if ((await CatalogInspector.InspectAsync(root)).SupportedLast) throw new Exception("Catalog ordering check failed.");
        var nativeStats = new Workflows.StatsEditorViewModel(new Workflows.NativeNotices(_ => { }), new Workflows.NativeAccountContext { EventsDirectory = root });
        nativeStats.TitleId = "9"; nativeStats.StatItems.Add(new Workflows.StatsEditorViewModel.StatDisplayItem { Name = "TestStat", Type = "Integer", CurrentValue = "1" }); nativeStats.SaveDiscoveredStats();
        var savedCatalog = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(Path.Combine(root, "Data.json")));
        if (savedCatalog.Properties().Last().Name != "SupportedTitleIDs" || savedCatalog["1"] == null || savedCatalog["9"]?["Stats"]?["TestStat"] == null || Directory.GetFiles(root, "Data.json.*.bak").Length != 1) throw new Exception("Discovered-stat save did not preserve ordering, existing data, or backup.");
        var export = Path.Combine(root, "achievements.json");
        await File.WriteAllTextAsync(export, "{\"achievements\":[{\"id\":\"1\",\"name\":\"Completed\",\"progressState\":\"Achieved\",\"rewards\":[{\"type\":\"Gamerscore\",\"value\":\"15\"}]},{\"id\":\"2\",\"name\":\"Pending\",\"progressState\":\"NotStarted\"},{\"id\":\"3\",\"name\":\"Unknown\"}]}");
        using var model = new DesktopModel();
        await model.OpenExportAsync(export);
        model.Filter("Unlocked"); if (model.VisibleAchievements.Length != 1 || model.VisibleAchievements[0].Id != "1" || model.VisibleAchievements[0].Score != 15) throw new Exception("Unlocked filter or score mapping failed.");
        model.Filter("Locked"); if (model.VisibleAchievements.Length != 1 || model.VisibleAchievements[0].Id != "2") throw new Exception("Locked filter failed.");
        model.Filter("All"); model.Search = "3"; if (model.VisibleAchievements.Length != 1 || model.VisibleAchievements[0].ProgressKnown) throw new Exception("Unknown progress mapping failed.");
        if (model.CanUnlockSelected || model.CanUnlockAll) throw new Exception("Local exports must not enable account mutations.");
        if (BrowserXboxLogin.ValidateRedirect("https://login.live.com/oauth20_desktop.srf?code=test&state=expected", "expected") != "test") throw new Exception("Login redirect parsing failed.");
        foreach (var invalid in new[] { "https://evil.example/oauth20_desktop.srf?code=test&state=expected", "https://login.live.com/oauth20_desktop.srf?code=test&state=wrong", "https://login.live.com/oauth20_desktop.srf?code=test&code=other&state=expected" })
        {
            try { BrowserXboxLogin.ValidateRedirect(invalid, "expected"); throw new Exception("Invalid login redirect accepted."); } catch (InvalidDataException) { }
        }
        var catalog = new EventCatalog(Path.GetFullPath("src/AchievementLabs.App/Events"));
        var payloads = await catalog.BuildPayloadsAsync("1479758055", "1", "123456789", DateTime.UtcNow);
        if (payloads.Count == 0 || payloads.Any(p => p.Contains("REPLACE"))) throw new Exception("Arkham payload construction failed.");
        var csv = Path.Combine(root, "export.csv"); await model.ExportCsvAsync(csv);
        var lines = await File.ReadAllLinesAsync(csv);
        if (lines.Length != 2 || !lines[1].Contains("\"3\",\"Unknown\"") || !lines[1].EndsWith(",\"\"")) throw new Exception("Filtered CSV export failed.");
        var console = new Game("1", "Console", "XboxOne / PC", 1, 10, 10);
        var legacy = new Game("2", "Legacy", "WindowsPhone", 0, 5, 0);
        if (!DesktopModel.MatchesPlatform(console, "Xbox One/Series") || !DesktopModel.MatchesPlatform(console, "PC") || DesktopModel.MatchesPlatform(console, "Xbox 360") || !DesktopModel.MatchesPlatform(console, "Incomplete Games") || !DesktopModel.UsesLegacyEndpoint(legacy)) throw new Exception("Platform filters or legacy routing failed.");
        model.Navigate("Settings"); if (!model.IsSettings || model.IsAchievements || model.IsLibrary) throw new Exception("Settings navigation failed.");

        var stressPath = Path.Combine(root, "achievements-10000.json");
        var stressRows = new Newtonsoft.Json.Linq.JArray();
        for (var index = 1; index <= 10_000; index++)
        {
            stressRows.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["id"] = index.ToString(), ["name"] = "Achievement " + index,
                ["description"] = "Synthetic performance fixture " + index,
                ["progressState"] = index % 3 == 0 ? "Achieved" : "NotStarted",
                ["rewards"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject { ["type"] = "Gamerscore", ["value"] = "10" })
            });
        }
        await File.WriteAllTextAsync(stressPath, new Newtonsoft.Json.Linq.JObject { ["achievements"] = stressRows }.ToString(Newtonsoft.Json.Formatting.None));
        var stressClock = Stopwatch.StartNew();
        await model.OpenExportAsync(stressPath);
        var loadMilliseconds = stressClock.ElapsedMilliseconds;
        if (model.VisibleAchievements.Length != 10_000) throw new Exception("Large achievement import failed.");
        stressClock.Restart();
        for (var index = 0; index < 100; index++) model.Search = "Achievement " + (index + 1);
        var searchMilliseconds = stressClock.ElapsedMilliseconds;
        model.Search = ""; model.Filter("Unlocked");
        if (model.VisibleAchievements.Length != 3_333) throw new Exception("Large achievement filtering failed.");
        await File.WriteAllTextAsync(Path.Combine(output, "performance.txt"), $"Synthetic 10,000-row import: {loadMilliseconds} ms{Environment.NewLine}100 search updates: {searchMilliseconds} ms{Environment.NewLine}This measures model import/filter work, not GPU rendering or Xbox network latency.{Environment.NewLine}");
        model.Navigate("Diagnostics"); if (!model.IsDiagnostics || model.IsSettings) throw new Exception("Diagnostics navigation failed.");
    }
}
