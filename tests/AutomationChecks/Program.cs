using AchievementLabs.Core;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static BuildQueueItem Job(string title, string xuid = "123") => new(Guid.NewGuid(), xuid, title, "Game " + title, "456", 1, DateTimeOffset.UtcNow);
static string Token(string hash, long expiry) => "x:XBL3.0 x=" + hash + ";e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + expiry + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

if (args.Length == 2 && args[0] == "--migrate-stats")
{
    Console.WriteLine($"Moved {StatCatalogStore.MigrateLegacy(Path.GetFullPath(args[1]))} stat title block(s) into Stats.json.");
    return;
}
var root = Path.Combine(Path.GetFullPath(args.Length > 0 ? args[0] : "test-output"), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var now = DateTimeOffset.UtcNow;
var fresh = Token("999", now.AddHours(1).ToUnixTimeSeconds());
var older = Token("999", now.AddMinutes(10).ToUnixTimeSeconds());
var expired = Token("999", now.AddMinutes(-1).ToUnixTimeSeconds());
Check(EventTokenValidator.TryValidate("x:XBL3.0 x=999;opaque", out var unknown, out _) && unknown == null, "Opaque expiry should stay unknown.");
Check(XboxTokenMaintenancePolicy.ShouldRefresh(now, now.AddMinutes(-15), null), "Periodic refresh missed.");
Check(XboxTokenMaintenancePolicy.ShouldRefresh(now, now, now.AddMinutes(4)), "Expiry refresh margin missed.");
Check(!XboxTokenMaintenancePolicy.ShouldRefresh(now, now.AddMinutes(-1), now.AddHours(1)), "Refresh ran unnecessarily.");

// Append while running, then append after completion while the consumer is idle.
var queue = new BuildQueueCoordinator(Path.Combine(root, "append", "manifest.json"));
var first = Job("1"); var second = Job("2"); var third = Job("3");
var enteredFirst = Signal(); var releaseFirst = Signal(); var doneSecond = Signal(); var doneThird = Signal();
var ran = new List<string>();
queue.Enqueue(first);
Check(!queue.Enqueue(Job("1")), "Pending duplicate accepted.");
var run = queue.RunAsync("123", async (job, token) =>
{
    ran.Add(job.TitleId);
    if (job.Id == first.Id) { enteredFirst.TrySetResult(); await releaseFirst.Task.WaitAsync(token); }
    if (job.Id == second.Id) doneSecond.TrySetResult();
    if (job.Id == third.Id) doneThird.TrySetResult();
    return BuildQueueItemStatus.Completed;
});
await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(!queue.Remove(first.Id), "Removed the running game.");
Check(!queue.Enqueue(Job("1")), "Running duplicate accepted.");
queue.Enqueue(second); releaseFirst.TrySetResult();
await doneSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(queue.IsRunning, "Queue ended after draining.");
queue.Enqueue(third);
await doneThird.Task.WaitAsync(TimeSpan.FromSeconds(5));
queue.Stop(); await run.WaitAsync(TimeSpan.FromSeconds(5));
Check(ran.SequenceEqual(["1", "2", "3"]), "Append order failed.");
Check(queue.Items.All(i => i.Status == BuildQueueItemStatus.Completed), "Completed jobs not checkpointed.");
Check(new BuildQueueCoordinator(Path.Combine(root, "append", "manifest.json")).Items.Count == 3, "Manifest round trip failed.");
Check(!File.ReadAllText(Path.Combine(root, "append", "manifest.json")).Contains("XBL3"), "Credential entered the queue manifest.");

// Cancellation while waiting for credentials preserves the job, with explicit resume.
var pausedQueue = new BuildQueueCoordinator(Path.Combine(root, "pause", "manifest.json"));
var paused = Job("4"); pausedQueue.Enqueue(paused);
var tokenWait = Signal();
var pausedRun = pausedQueue.RunAsync("123", async (job, token) =>
{
    pausedQueue.SetWaitingForToken(job.Id, true); tokenWait.TrySetResult();
    await Task.Delay(Timeout.Infinite, token); return BuildQueueItemStatus.Completed;
});
await tokenWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
var recovered = new BuildQueueCoordinator(Path.Combine(root, "pause", "manifest.json"));
Check(recovered.Items.Single().Status == BuildQueueItemStatus.Paused, "Restart did not pause an interrupted job.");
pausedQueue.Stop(); await pausedRun.WaitAsync(TimeSpan.FromSeconds(5));
Check(pausedQueue.Items.Single().Status == BuildQueueItemStatus.Paused, "Cancellation lost the job checkpoint.");
Check(pausedQueue.Retry(paused.Id), "Explicit resume failed.");
var resumed = Signal();
var resumedRun = pausedQueue.RunAsync("123", (job, token) => { resumed.TrySetResult(); return Task.FromResult(BuildQueueItemStatus.Completed); });
await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)); pausedQueue.Stop(); await resumedRun.WaitAsync(TimeSpan.FromSeconds(5));

// Account isolation and idle cancellation without any game execution.
var isolated = new BuildQueueCoordinator(Path.Combine(root, "account", "manifest.json"));
isolated.Enqueue(Job("5", "different-account"));
var called = false;
var idleRun = isolated.RunAsync("123", (job, token) => { called = true; return Task.FromResult(BuildQueueItemStatus.Completed); });
isolated.Stop(); await idleRun.WaitAsync(TimeSpan.FromSeconds(5));
Check(!called && isolated.Items.Single().Status == BuildQueueItemStatus.Pending, "A queue ran another account's game.");

var failures = new BuildQueueCoordinator(Path.Combine(root, "failure", "manifest.json"));
var failedJob = Job("6"); failures.Enqueue(failedJob); failures.Enqueue(Job("7"));
var afterFailure = Signal(); var attempts = 0;
var failureRun = failures.RunAsync("123", (job, token) =>
{
    if (job.Id == failedJob.Id) { attempts++; throw new HttpRequestException("Simulated response ambiguity."); }
    afterFailure.TrySetResult(); return Task.FromResult(BuildQueueItemStatus.Completed);
});
await afterFailure.Task.WaitAsync(TimeSpan.FromSeconds(5)); failures.Stop(); await failureRun.WaitAsync(TimeSpan.FromSeconds(5));
Check(attempts == 1 && failures.Items.First().Status == BuildQueueItemStatus.Failed, "A failed submission was retried automatically.");

var brokenPath = Path.Combine(root, "broken.json"); File.WriteAllText(brokenPath, "{broken");
try { _ = new BuildQueueCoordinator(brokenPath); throw new Exception("Corrupt manifest was silently replaced."); } catch (JsonException) { }
Check(File.ReadAllText(brokenPath) == "{broken", "Corrupt manifest was modified.");

// Presence cleanup is inside execute: the gap begins only after cleanup returns.
var handoff = new BuildQueueCoordinator(Path.Combine(root, "handoff", "manifest.json"));
handoff.Enqueue(Job("8")); handoff.Enqueue(Job("9"));
var handoffDone = Signal(); var order = new List<string>(); long endedA = 0;
var buffer = TimeSpan.FromMilliseconds(150);
handoff.BufferWaiting += _ => order.Add("buffer");
var handoffRun = handoff.RunAsync("123", (job, token) =>
{
    if (job.TitleId == "8") { order.Add("stop A"); endedA = System.Diagnostics.Stopwatch.GetTimestamp(); }
    else
    {
        Check(System.Diagnostics.Stopwatch.GetElapsedTime(endedA) >= buffer, "Title B started before the buffer elapsed.");
        order.Add("start B"); handoffDone.TrySetResult();
    }
    return Task.FromResult(BuildQueueItemStatus.Completed);
}, betweenTitles: buffer);
await handoffDone.Task.WaitAsync(TimeSpan.FromSeconds(5)); handoff.Stop(); await handoffRun.WaitAsync(TimeSpan.FromSeconds(5));
Check(order.First() == "stop A" && order.Contains("buffer") && order.Last() == "start B", "Handoff order was wrong.");

var bufferStop = new BuildQueueCoordinator(Path.Combine(root, "buffer-stop", "manifest.json"));
bufferStop.Enqueue(Job("10")); bufferStop.Enqueue(Job("11"));
var inBuffer = Signal(); var nextStarted = false;
bufferStop.BufferWaiting += _ => inBuffer.TrySetResult();
var bufferRun = bufferStop.RunAsync("123", (job, token) =>
{
    if (job.TitleId == "11") nextStarted = true;
    return Task.FromResult(BuildQueueItemStatus.Completed);
}, betweenTitles: TimeSpan.FromSeconds(30));
await inBuffer.Task.WaitAsync(TimeSpan.FromSeconds(5)); bufferStop.Stop(); await bufferRun.WaitAsync(TimeSpan.FromSeconds(5));
Check(!nextStarted && bufferStop.Items.Last().Status == BuildQueueItemStatus.Pending, "Stopping during the gap started or consumed the next title.");

var cleanupFailure = new BuildQueueCoordinator(Path.Combine(root, "cleanup-failure", "manifest.json"));
cleanupFailure.Enqueue(Job("12")); cleanupFailure.Enqueue(Job("13")); var executed = 0;
try
{
    await cleanupFailure.RunAsync("123", (job, token) =>
    {
        executed++; throw new BuildQueueHandoffException("Simulated stop-presence rejection.");
    });
    throw new Exception("Presence cleanup failure did not stop the worker.");
}
catch (BuildQueueHandoffException) { }
Check(executed == 1 && !cleanupFailure.IsRunning && cleanupFailure.Items.Last().Status == BuildQueueItemStatus.Pending,
    "Presence cleanup failure started the next title.");
var migrationRoot = Path.Combine(root, "stats-migration"); Directory.CreateDirectory(migrationRoot);
var dataFile = Path.Combine(migrationRoot, "Data.json");
var originalData = "{\"1\":{\"Stats\":{\"Kills\":{\"Type\":\"Integer\"}},\"AchievementA\":{\"Keep\":true}},\"SupportedTitleIDs\":[\"1\"],\"2\":{\"AchievementB\":{}}}";
File.WriteAllText(dataFile, originalData);
Check(StatCatalogStore.MigrateLegacy(migrationRoot) == 1, "Legacy stat migration failed.");
var migratedData = JObject.Parse(File.ReadAllText(dataFile)); var migratedStats = JObject.Parse(File.ReadAllText(Path.Combine(migrationRoot, "Stats.json")));
Check(migratedData["1"]?["Stats"] == null && migratedData["1"]?["AchievementA"]?["Keep"]?.Value<bool>() == true
    && migratedData["2"]?["AchievementB"] != null && migratedData.Properties().Last().Name == "SupportedTitleIDs"
    && migratedStats["1"]?["Stats"]?["Kills"]?["Type"]?.ToString() == "Integer", "Migration lost mappings or changed ordering.");
Check(File.ReadAllText(Directory.GetFiles(migrationRoot, "Data.json.*.bak").Single()) == originalData, "Migration backup differs from the original.");
Check(StatCatalogStore.MigrateLegacy(migrationRoot) == 0, "Migration was not idempotent.");
File.WriteAllText(dataFile, originalData);
var conflictingStats = "{\"1\":{\"Stats\":{\"Kills\":{\"Type\":\"String\"}}}}";
File.WriteAllText(Path.Combine(migrationRoot, "Stats.json"), conflictingStats);
try { StatCatalogStore.MigrateLegacy(migrationRoot); throw new Exception("Conflicting stat recipes were overwritten."); } catch (InvalidDataException) { }
Check(File.ReadAllText(dataFile) == originalData && File.ReadAllText(Path.Combine(migrationRoot, "Stats.json")) == conflictingStats,
    "Conflict detection modified a catalog.");
Console.WriteLine("PASS: automation lifecycle, token freshness, presence handoff, and Stats.json migration/order/backups/conflict preservation.");
