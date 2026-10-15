using System.Net;
using System.Reflection;
using System.Text.Json;
using AchievementLabs.Desktop;
using AchievementLabs.Core;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.Models;
using AchievementLabs.MultiSelect;
using LibraryGame = AchievementLabs.Desktop.Game;

public static class QueuePresenceTokenTests
{
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    sealed class ExchangeHandler(string identity = "123", string eventHash = "hash", bool expired = false) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            string json;
            if (Calls == 1)
            {
                Assert(request.RequestUri!.Host == "device.auth.xboxlive.com" && request.Headers.Contains("Signature"), "Device exchange is signed");
                Assert(root.GetProperty("Properties").GetProperty("ProofKey").GetProperty("alg").GetString() == "ES256", "Device proof key");
                json = "{\"Token\":\"fake-device\"}";
            }
            else if (Calls == 2) json = "{\"Token\":\"fake-user\"}";
            else
            {
                Assert(root.GetProperty("Properties").GetProperty("DeviceToken").GetString() == "fake-device", "Both XSTS requests carry the device token");
                Assert(root.GetProperty("RelyingParty").GetString() == (Calls == 3 ? "http://xboxlive.com" : "http://events.xboxlive.com"), "Identity is verified before events exchange");
                json = JsonSerializer.Serialize(new { Token = "fake-event", NotAfter = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1), DisplayClaims = new { xui = new[] { new { xid = identity, uhs = Calls == 3 ? "hash" : eventHash } } } });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
    sealed class TotalsRateHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var response = new HttpResponseMessage(Calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent("synthetic page") };
            if (Calls == 1) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
    private static void CollectorResponseChecks()
    {
        var accepted = EventCollectorResponse.Read("{\"acc\":1,\"rej\":0}");
        Assert(accepted.Accepted == 1 && accepted.Rejected == 0, "Collector counters retained");
        EventCollectorResponse.EnsureNoRejection(200, "{\"acc\":1,\"rej\":0}");
        foreach (var text in new[] { "{\"acc\":0,\"rej\":1}", "{\"acc\":1,\"rej\":1}",
            "{\"efi\":{\"private-tenant\":\"all\"}}", "{\"efi\":{\"private-tenant\":[0]}}",
            "{\"TokenCrackingFailure\":true}" })
        {
            bool rejected = false;
            try { EventCollectorResponse.EnsureNoRejection(200, text); }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains("HTTP 200") && !ex.Message.Contains("private-tenant"); }
            Assert(rejected, "HTTP 200 cannot hide explicit collector rejection");
        }
        foreach (var text in new[] { "", "not-json", "null", "[]", "{\"acc\":\"private-token\",\"rej\":null}", "{\"acc\":-1}", "{\"acc\":1e100}" })
        {
            EventCollectorResponse.EnsureNoRejection(200, text);
            Assert(!EventCollectorResponse.Read(text).Summary.Contains("private-token"), "Malformed or unknown response never exposes raw strings");
        }
        EventCollectorResponse.EnsureNoRejection(401, "{\"acc\":0}");
        Console.WriteLine("PASS: HTTP-success collector rejection, partial rejection, safe counters, unknown bodies and transport-auth precedence. Synthetic responses only.");
    }
    private static void SpooferNotificationChecks()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var model = new DesktopModel();
        var native = new List<string>(); int inApp = 0;
        model.WindowsNotification = (title, message) => native.Add(title + ": " + message);
        model.SpooferFailure = _ => inApp++;
        var complete = typeof(DesktopModel).GetMethod("CompletePresence", flags)!;
        typeof(DesktopModel).GetProperty("ActiveSpoofTitle")!.SetValue(model, "Synthetic game · 42");
        complete.Invoke(model, new object?[] { "Heartbeat HTTP 401", false });
        Assert(native.Count == 1 && native[0].Contains("Synthetic game · 42") && inApp == 1, "Unexpected stop sends one native and one in-app alert with active title");
        Assert(model.SpoofingStatus.Contains("unexpectedly"), "Failure remains visible after stopping");
        complete.Invoke(model, new object?[] { "Heartbeat HTTP 401", true });
        complete.Invoke(model, new object?[] { null, false });
        Assert(native.Count == 1 && inApp == 1, "Manual stop and normal completion never alert");
        model.WindowsNotificationsEnabled = false;
        complete.Invoke(model, new object?[] { "Heartbeat timed out", false });
        Assert(native.Count == 1 && inApp == 2, "Windows toggle preserves in-app failure notice");
        model.NotifySpooferStops = false;
        complete.Invoke(model, new object?[] { "Heartbeat HTTP 500", false });
        Assert(native.Count == 1 && inApp == 2, "Spoofer notification toggle suppresses both alerts");
        model.WindowsNotificationsEnabled = true;
        model.TestWindowsNotification();
        Assert(native.Count == 2, "Test notification uses injectable native sink without sending Windows toast");
        var path = Path.Combine(Path.GetTempPath(), "labs-notices-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new DesktopPreferencesStore(path);
            store.SaveAsync(new() { WindowsNotificationsEnabled = false, NotifySpooferStops = false, StopAutoUnlockOnSpoofFailure = true, NotifyAutoUnlockCompletion = false }).GetAwaiter().GetResult();
            var loaded = store.LoadAsync().GetAwaiter().GetResult();
            Assert(!loaded.WindowsNotificationsEnabled && !loaded.NotifySpooferStops && loaded.StopAutoUnlockOnSpoofFailure && !loaded.NotifyAutoUnlockCompletion, "Notification preferences persist");
            File.WriteAllText(path, "{}");
            loaded = store.LoadAsync().GetAwaiter().GetResult();
            Assert(loaded.WindowsNotificationsEnabled && loaded.NotifySpooferStops && !loaded.StopAutoUnlockOnSpoofFailure && loaded.NotifyAutoUnlockCompletion, "Old settings receive notification defaults");
        }
        finally { File.Delete(path); }
        model.NotifySpooferStops = false;
        model.StopAutoUnlockOnSpoofFailure = true;
        var queue = model.XboxQueue;
        var cancellation = new CancellationTokenSource();
        typeof(AutoUnlockerViewModel).GetField("_cancellationTokenSource", flags)!.SetValue(queue, cancellation);
        queue.IsRunning = true;
        complete.Invoke(model, new object?[] { "Heartbeat HTTP 401", true });
        Assert(!cancellation.IsCancellationRequested, "Manual spoof stop leaves queue running");
        model.StopAutoUnlockOnSpoofFailure = false;
        complete.Invoke(model, new object?[] { "Heartbeat HTTP 401", false });
        Assert(!cancellation.IsCancellationRequested, "Disabled stop option leaves queue running");
        model.StopAutoUnlockOnSpoofFailure = true;
        complete.Invoke(model, new object?[] { "Heartbeat HTTP 401", false });
        Assert(cancellation.IsCancellationRequested && queue.StatusText.Contains("spoofing failed"), "Spoof failure cancels queue independently of notification toggles");
        queue.IsRunning = false;
        cancellation.Dispose();
        typeof(AutoUnlockerViewModel).GetField("_cancellationTokenSource", flags)!.SetValue(queue, null);
        var completionState = new AutoUnlockState { GameName = "Synthetic game", CurrentIndex = 1, Queue = new() { new() { Completed = true } } };
        typeof(AutoUnlockerViewModel).GetField("_state", flags)!.SetValue(queue, completionState);
        int completionNotices = 0;
        model.AutoUnlockCompleted = _ => completionNotices++;
        queue.NotifyQueueCompletion();
        Assert(completionNotices == 1 && native.Count == 3 && native.Last().Contains("1/1"), "Completed queue routes in-app and native notifications");
        model.NotifyAutoUnlockCompletion = false;
        queue.NotifyQueueCompletion();
        Assert(completionNotices == 1 && native.Count == 3, "Completion toggle suppresses both notifications");
        model.NotifyAutoUnlockCompletion = true;
        completionState.CurrentIndex = 0;
        queue.NotifyQueueCompletion();
        Assert(completionNotices == 1, "Partial queues never notify completion");
        completionState.CurrentIndex = 1;
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel();
            typeof(AutoUnlockerViewModel).GetField("_cancellationTokenSource", flags)!.SetValue(queue, stopped);
            queue.NotifyQueueCompletion();
            Assert(completionNotices == 1, "Cancelled queue never notifies completion");
        }
        model.NotifySpooferStops = true;
        model.Dispose();
        complete.Invoke(model, new object?[] { "Heartbeat failed", false });
        Assert(native.Count == 3 && inApp == 2, "Closing the app suppresses stop alerts");
        Console.WriteLine("PASS: spoofer stop classification, native notification routing, toggles and preference persistence. No live requests or Windows notifications.");
    }
    public static void Run()
    {
        CollectorResponseChecks();
        SharedTotalsTests.Run();
        BundledCatalogTests.Run();
        SpooferNotificationChecks();
        var definitions = Newtonsoft.Json.JsonConvert.DeserializeObject<AchievementsResponse>(
            "{\"achievements\":[{\"id\":\"1\",\"name\":\"First\"},{\"id\":\"2\",\"name\":\"Event\",\"progression\":{\"requirements\":[{\"id\":\"00000000-0000-0000-0000-000000000000\"},{\"id\":\"11111111-1111-1111-1111-111111111111\"}]}}]}")!;
        Assert(AutoUnlockerViewModel.UsesEvents(definitions.achievements), "Later achievements and later requirements determine event routing");
        var routeVm = new AutoUnlockerViewModel(new NativeNotices(_ => { }), new NativeAccountContext { EventsToken = "stale-copy" });
        var routeState = new AutoUnlockState { TitleId = "123", IsEventBased = false, Queue = new() { new() { AchievementId = "2", DelaySeconds = 45 } } };
        typeof(AutoUnlockerViewModel).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(routeVm, routeState);
        routeVm.ValidateQueueMetadata(definitions);
        Assert(routeState.IsEventBased && routeState.Queue.Single().DelaySeconds == 45, "Saved queue routing repairs without changing custom delays");
        var sends = 0; var refreshes = 0;
        bool RetrySend() { sends++; if (sends == 1) throw new HttpRequestException("synthetic", null, HttpStatusCode.Unauthorized); return true; }
        Assert(EventUnlockRecovery.RunAsync(true, () => Task.FromResult(RetrySend()), _ => { refreshes++; return Task.FromResult(true); }, CancellationToken.None).GetAwaiter().GetResult() && sends == 2 && refreshes == 1, "Token auth failure refreshes once and retries the same operation");
        sends = 0; refreshes = 0;
        try { EventUnlockRecovery.RunAsync(true, () => { sends++; throw new HttpRequestException("synthetic", null, HttpStatusCode.Forbidden); }, _ => { refreshes++; return Task.FromResult(true); }, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Retry should fail"); }
        catch (HttpRequestException) { Assert(sends == 2 && refreshes == 1, "Repeated auth failure never loops"); }
        sends = 0; refreshes = 0;
        try { EventUnlockRecovery.RunAsync(false, () => { sends++; throw new MissingEventTokenException(); }, _ => { refreshes++; return Task.FromResult(true); }, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Disabled recovery should fail"); }
        catch (MissingEventTokenException) { Assert(sends == 1 && refreshes == 0, "Disabled recovery preserves existing failure behavior"); }
        sends = 0;
        using (var stopRetry = new CancellationTokenSource())
        {
            try { EventUnlockRecovery.RunAsync(true, () => { sends++; throw new MissingEventTokenException(); }, _ => { stopRetry.Cancel(); return Task.FromResult(true); }, stopRetry.Token).GetAwaiter().GetResult(); throw new Exception("Stop should cancel retry"); }
            catch (OperationCanceledException) { Assert(sends == 1, "Stopping during refresh prevents a retry unlock"); }
        }
        Assert(!EventUnlockRecovery.IsTokenFailure(new InvalidDataException("missing mapping")) && !EventUnlockRecovery.IsTokenFailure(new HttpRequestException("server", null, HttpStatusCode.InternalServerError)), "Unrelated failures do not refresh account tokens");
        Assert(DesktopModel.TotalsFailureReason(new HttpRequestException("private content", null, HttpStatusCode.NotFound)) == "HTTP 404", "Report retains status and excludes private error content");
        var rateHandler = new TotalsRateHandler();
        using (var totalsHttp = new HttpClient(rateHandler))
            Assert(AchievementTotals.ReadPageAsync(totalsHttp, "https://synthetic.invalid/achievements", CancellationToken.None).GetAwaiter().GetResult() == "synthetic page" && rateHandler.Calls == 2, "Totals retry rate limits without live HTTP");
        var pages = new Queue<string>(new[] {
            "{\"achievements\":[{\"id\":\"1\"},{\"id\":\"2\"}],\"pagingInfo\":{\"continuationToken\":\"next\"}}",
            "{\"achievements\":[{\"id\":\"2\"},{\"id\":\"3\"},{\"id\":\"4\",\"achievementType\":\"Challenge\"}]}" });
        var tokens = new List<string?>();
        var total = AchievementTotals.CountAsync(token => { tokens.Add(token); return Task.FromResult(pages.Dequeue()); }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(total == 3 && tokens.SequenceEqual(new string?[] { null, "next" }), "Totals count all pages, deduplicate IDs and exclude challenges");
        try { AchievementTotals.CountAsync(_ => Task.FromResult("{\"achievements\":[],\"pagingInfo\":{\"continuationToken\":\"repeat\"}}"), CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Repeated paging should fail"); }
        catch (InvalidDataException) { }
        var persistent = AchievementTotals.MeasureAsync(_ => Task.FromResult("{\"achievements\":[{\"id\":\"1\",\"progressState\":\"Achieved\"},{\"id\":\"2\",\"progressState\":\"NotStarted\"},{\"id\":\"3\",\"achievementType\":\"Challenge\",\"progressState\":\"Achieved\"}]}"), CancellationToken.None).GetAwaiter().GetResult();
        Assert(persistent.Total == 2 && persistent.Unlocked == 1, "Persistent progress excludes earned challenges from numerator and denominator");
        var missing = new LibraryGame("1", "Title", "XboxOne", 5, 0, 0);
        Assert(DesktopModel.WithKnownTotal(missing, new Dictionary<string, int> { ["1"] = 20 }).Total == 20, "Known totals repair omitted title-history counts");
        var conflicting = DesktopModel.WithKnownTotal(missing, new Dictionary<string, int> { ["1"] = 4 });
        Assert(conflicting.Total == 4 && !conflicting.ProgressKnown, "Conflicting history counts never display impossible completion progress");
        Assert(DesktopModel.WithKnownTotal(missing with { Total = 30 }, new Dictionary<string, int> { ["1"] = 20 }).Total == 30, "Fresh Xbox count takes priority");
        Assert((missing with { Completed = 0, NoDefinitionsReturned = true }).ProgressLabel == "No Xbox achievements returned", "Empty service lists have an honest distinct label");
        var notices = 0;
        var failureVm = new AutoUnlockerViewModel(new NativeNotices(_ => { }), new NativeAccountContext()) { FailureNotification = _ => notices++ };
        var failedEntry = new AutoUnlockQueueEntry { AchievementName = "Synthetic achievement" };
        Assert(!failureVm.HandleUnlockFailure(failedEntry, "synthetic failure") && notices == 1, "Default notifies and continues");
        failureVm.NotifyOnFailure = false; failureVm.StopOnFailure = true;
        Assert(failureVm.HandleUnlockFailure(failedEntry, "synthetic failure") && notices == 1 && failureVm.StatusText.Contains("Click Start to retry"), "Stopping is independent of notification toggle");
        var statePath = AutoUnlockState.GetSavePath();
        var backup = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
        try
        {
            var saveState = new AutoUnlockState { GameName = "Synthetic game", Queue = new() { new() { DelaySeconds = 123 } } };
            typeof(AutoUnlockerViewModel).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(failureVm, saveState);
            failureVm.SaveQueue();
            var saved = AutoUnlockState.Load()!;
            Assert(saved.Queue.Single().DelaySeconds == 123 && saved.StopOnFailure && !saved.NotifyOnFailure && !saved.RefreshTokenOnFailure && !saved.IsRunning, "Manual save preserves delays and failure options");
            failureVm.IsRunning = true;
            saveState.Queue[0].DelaySeconds = 456;
            failureVm.SaveQueue();
            Assert(AutoUnlockState.Load()!.Queue.Single().DelaySeconds == 123, "Manual save cannot overwrite a running queue");
            failureVm.IsRunning = false;
            var pendingIndex = saveState.CurrentIndex;
            Assert(failureVm.HandleUnlockFailure(saveState.Queue[0], "synthetic failure") && saveState.CurrentIndex == pendingIndex && !saveState.Queue[0].Completed, "Stop failure preserves pending achievement for retry");
        }
        finally { if (backup == null) File.Delete(statePath); else File.WriteAllBytes(statePath, backup); }
        var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AutoUnlockState>("{}")!;
        Assert(settings.NotifyOnFailure && !settings.StopOnFailure, "Old saved queues keep safe compatible failure defaults");
        var sortPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "sort.txt");
        try
        {
            Assert(LibrarySortPreferences.Load(sortPath) == "A-Z", "Default library sort");
            LibrarySortPreferences.Save(sortPath, "Last Played");
            Assert(LibrarySortPreferences.Load(sortPath) == "Last Played", "Library sort survives reload");
            File.WriteAllText(sortPath, "invalid");
            Assert(LibrarySortPreferences.Load(sortPath) == "A-Z", "Unknown saved sort falls back safely");
        }
        finally { Directory.Delete(Path.GetDirectoryName(sortPath)!, true); }
        var state = new AutoUnlockState { CurrentIndex = 1, RemainingDelaySeconds = 30, Queue = new() {
            new() { Completed = true, DelaySeconds = 1000 }, new() { DelaySeconds = 60 },
            new() { Completed = true, DelaySeconds = 999 }, new() { DelaySeconds = 90 } } };
        Assert(AutoUnlockerViewModel.EstimateRemaining(state, 2) == TimeSpan.FromSeconds(60), "ETA uses saved remaining delay, speed and pending entries");
        state.CurrentIndex = 4;
        Assert(AutoUnlockerViewModel.EstimateRemaining(state, 1) == TimeSpan.Zero, "Finished queue has no remaining time");
        using var model = new DesktopModel();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(DesktopModel).GetProperty("Games")!.SetValue(model, new[] {
            new LibraryGame("1", "Zulu", "XboxOne", 0, 1, 0, LastPlayed: DateTime.UtcNow),
            new LibraryGame("2", "Alpha", "PC", 0, 1, 0),
            new LibraryGame("3", "Bravo", "XboxOne", 0, 1, 0, LastPlayed: DateTime.UtcNow.AddDays(-1)) });
        Assert(model.VisibleGames.Select(g => g.Id).SequenceEqual(new[] { "2", "3", "1" }), "A-Z");
        model.LibrarySort = "Z-A";
        Assert(model.VisibleGames.First().Id == "1", "Z-A");
        model.LibrarySort = "Last Played";
        Assert(model.VisibleGames.Select(g => g.Id).SequenceEqual(new[] { "1", "3", "2" }), "Last Played puts missing dates last");
        var refreshed = model.Games.Select(g => g with { Name = g.Name + " refreshed" }).ToArray();
        model.SelectedLibraryGame = model.Games.Single(g => g.Id == "3");
        typeof(DesktopModel).GetProperty("Games")!.SetValue(model, refreshed);
        Assert(model.SelectedLibraryGame?.Id == "3" && model.SelectedLibraryGame.Name.EndsWith("refreshed"), "Refresh preserves selected title using the new record");
        model.SelectedLibraryGame = null;
        typeof(DesktopModel).GetProperty("Games")!.SetValue(model, refreshed);
        Assert(model.SelectedLibraryGame?.Id == "1", "No selection chooses first game in active sort, not A-Z backing array");
        model.PlatformFilter = "Xbox One/Series";
        Assert(model.VisibleGames.Count() == 2, "Sort retains platform filter");
        typeof(DesktopModel).GetField("session", flags)!.SetValue(model, new ConnectedXboxSession("synthetic", "123", ""));
        typeof(DesktopModel).GetProperty("QueueActive")!.SetValue(model, true);
        Assert(model.CanStartPresence && model.CanLookupSpoofTitle && model.CanFillTotals && !model.CanQuery && !model.CanDisconnect, "Queue allows spoofing but still protects disconnect");
        var row = new AutoUnlockerViewModel.AutoUnlockQueueDisplay { Status = "Unlocked", CanEditDelay = true };
        model.XboxQueue.QueueItems.Add(row);
        model.XboxQueue.IsRunning = true;
        Assert(row.RowBackground == "#245C38" && !row.CanEditDelay, "Completed rows green and delay editing disabled during run");
        model.XboxQueue.ExternalPresenceActive = () => true;
        // No API is configured: this succeeds only if the queue preserves external presence.
        var task = (Task)typeof(AutoUnlockerViewModel).GetMethod("UpdatePresenceAsync", flags)!.Invoke(model.XboxQueue, new object[] { true })!;
        task.GetAwaiter().GetResult();
        model.XboxQueue.IsRunning = false;
        Assert(row.CanEditDelay, "Delay editing restored when stopped");
        Console.WriteLine("PASS: library ordering, concurrent presence gates, queue row state and device-bound token exchange. Synthetic requests only.");
    }
}
