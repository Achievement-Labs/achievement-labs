using System.Net;
using System.Reflection;
using AchievementLabs.Core;
using AchievementLabs.Desktop;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.Models;
using AchievementLabs.Mirror;
using Newtonsoft.Json;

static class Windows8QueueTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class LegacyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("titleId=1297292157") &&
                  request.Headers.GetValues("x-xbl-contract-version").Single() == "1", "Legacy read route or headers changed.");
            var json = request.RequestUri.Host == "progress.xboxlive.com"
                ? """{"achievements":[{"id":7,"name":"First","unlocked":false,"gamerscore":10},{"id":8,"name":"Second","unlocked":false}]}"""
                : """{"achievements":[{"id":7,"name":"First","timeUnlocked":"2024-01-02T20:04:05.123Z"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class ReferenceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get, "Reference check attempted a write.");
            var uri = request.RequestUri!;
            var catalog = uri.Host == "progress.xboxlive.com";
            Check(uri.AbsolutePath.Contains(catalog ? "xuid(123)" : "xuid(456)", StringComparison.Ordinal),
                "Windows 8 catalog and reference history used the wrong accounts.");
            var json = catalog
                ? """{"achievements":[{"id":7,"name":"Catalog name","unlocked":false,"gamerscore":10}]}"""
                : """{"achievements":[{"id":7,"timeUnlocked":"2024-01-02T20:04:05.123Z"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static AchievementsResponse Rows(string state) => new() { achievements =
        [new OneCoreAchievementResponse { id = "7", name = "First", progressState = state,
            progression = new AchievementProgression { timeUnlocked = state == "Achieved" ? "2024-01-02T20:04:05.123Z" : null } }] };

    public static void Run()
    {
        Check(DesktopModel.IsKnownWindows8Title("1297290233"), "4 Elements II SE was not classified as Windows 8.");
        using (var http = new HttpClient(new LegacyHandler()))
        {
            var reader = typeof(DesktopModel).Assembly.GetType("AchievementLabs.Desktop.Windows8Achievements")!;
            var method = reader.GetMethod("ReadQueueAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var task = (Task<AchievementsResponse>)method.Invoke(null, [http, "synthetic-auth", "123", "1297292157", CancellationToken.None])!;
            var rows = task.GetAwaiter().GetResult().achievements;
            Check(rows.Count == 2 && rows.Single(a => a.id == "7").progressState == "Achieved" &&
                  rows.Single(a => a.id == "7").progression?.timeUnlocked == "2024-01-02T20:04:05.123Z" &&
                  rows.Single(a => a.id == "8").progressState != "Achieved", "Legacy catalog and earned history were not merged for queues and Mirror.");
            var verify = reader.GetMethod("VerifyAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var check = (Task<AchievementVerificationResult>)verify.Invoke(null, [http, "synthetic-auth", "123", "1297292157", "7", CancellationToken.None, (Func<TimeSpan, CancellationToken, Task>)((_, _) => Task.CompletedTask)])!;
            Check(check.GetAwaiter().GetResult().Confirmed, "Manual Windows 8 unlock verification did not use legacy account progress.");
        }
        using (var http = new HttpClient(new ReferenceHandler()))
        {
            var reader = typeof(DesktopModel).Assembly.GetType("AchievementLabs.Desktop.Windows8Achievements")!;
            var readReference = reader.GetMethod("ReadReferenceQueueAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var task = (Task<AchievementsResponse>)readReference.Invoke(null,
                [http, "synthetic-auth", "123", "456", "1297292157", CancellationToken.None])!;
            var row = task.GetAwaiter().GetResult().achievements.Single();
            Check(row.id == "7" && row.name == "Catalog name" && row.progressState == "Achieved" &&
                  row.progression?.timeUnlocked == "2024-01-02T20:04:05.123Z",
                "Reference history timestamp without an unlocked flag was lost.");
        }

        var root = Path.Combine(Path.GetTempPath(), "AchievementLabs-OfflineChecks", Environment.ProcessId.ToString(), "win8-queue");
        Directory.CreateDirectory(root);
        var account = new NativeAccountContext { XUIDOnly = "123", XAUTH = "synthetic-auth" };
        var plannedStamp = new DateTimeOffset(2024, 1, 2, 20, 4, 5, 123, TimeSpan.Zero);
        var state = new AutoUnlockState { OwnerXuid = "123", TitleId = "1297292157", GameName = "Synthetic Windows 8",
            IsWindows8 = true, Windows8StartDate = "2024-01-02", Windows8StartTime = "15:04:05.123",
            Windows8BaseTimestampUtc = plannedStamp,
            Queue = [new AutoUnlockQueueEntry { AchievementId = "7", AchievementName = "First", Windows8TimestampUtc = plannedStamp }] };
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonConvert.SerializeObject(state));
        var sends = 0; var tokenChecks = 0; var reads = 0; var presenceCalls = 0;
        var runner = new AutoUnlockerViewModel(new NativeNotices(_ => { }), account) { StatePath = path,
            SavedGameQueuesRoot = Path.Combine(root, "games"), VerificationDelayAsync = (_, _) => Task.CompletedTask,
            ExternalPresenceActive = () => { presenceCalls++; return false; },
            ReadQueueAchievementsAsync = (_, _) => Task.FromResult<AchievementsResponse?>(Rows(++reads >= 3 ? "Achieved" : "NotStarted")),
            EnsureWindows8TokenAsync = (_, _) => { tokenChecks++; return Task.CompletedTask; },
            SendWindows8AchievementAsync = (_, _, timestamp, _) => { Check(timestamp == plannedStamp, "Queue lost its saved millisecond timestamp."); sends++; return Task.CompletedTask; } };
        var result = runner.RunPreparedAsync(default).GetAwaiter().GetResult();
        Check(result == BuildQueueItemStatus.Completed && sends == 1 && tokenChecks == 1 && presenceCalls == 0 && runner.PreparedState!.Queue[0].Completed,
            "Windows 8 queue did not submit once and verify without title presence.");

        state.Queue[0].Completed = false; state.Queue[0].EventSubmissionPendingVerification = true; state.CurrentIndex = 0;
        File.WriteAllText(path, JsonConvert.SerializeObject(state));
        reads = 0;
        var resumed = new AutoUnlockerViewModel(new NativeNotices(_ => { }), account) { StatePath = path,
            SavedGameQueuesRoot = Path.Combine(root, "games"), VerificationDelayAsync = (_, _) => Task.CompletedTask,
            ReadQueueAchievementsAsync = (_, _) => Task.FromResult<AchievementsResponse?>(Rows(++reads >= 2 ? "Achieved" : "NotStarted")),
            SendWindows8AchievementAsync = (_, _, _, _) => throw new Exception("Pending request was resent.") };
        result = resumed.RunPreparedAsync(default).GetAwaiter().GetResult();
        Check(result == BuildQueueItemStatus.Completed && resumed.PreparedState!.Queue[0].Completed && sends == 1,
            "Pending Windows 8 request was not resumed through read-only verification.");

        state.Queue[0].Completed = false; state.Queue[0].EventSubmissionPendingVerification = false; state.CurrentIndex = 0;
        File.WriteAllText(path, JsonConvert.SerializeObject(state));
        var missingToken = new AutoUnlockerViewModel(new NativeNotices(_ => { }), account) { StatePath = path,
            SavedGameQueuesRoot = Path.Combine(root, "games"),
            ReadQueueAchievementsAsync = (_, _) => Task.FromResult<AchievementsResponse?>(Rows("NotStarted")),
            EnsureWindows8TokenAsync = (_, _) => throw new InvalidOperationException("The Windows 8 token is missing or expired."),
            SendWindows8AchievementAsync = (_, _, _, _) => throw new Exception("Sent without a Windows 8 token.") };
        try { missingToken.RunPreparedAsync(default).GetAwaiter().GetResult(); throw new Exception("Build queue continued without a Windows 8 token."); }
        catch (BuildQueueHandoffException error) { Check(error.Message.Contains("token is missing or expired"), "Missing token handoff lost its cause."); }
        var presence = typeof(AchievementMirrorViewModel).GetMethod("UsesXboxPresence", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(!(bool)presence.Invoke(null, [new MirrorPair { XboxTitleId = "1297292157", Direction = MirrorDirection.SteamToXbox }])! &&
              (bool)presence.Invoke(null, [new MirrorPair { XboxTitleId = "1653280174", Direction = MirrorDirection.SteamToXbox }])!,
            "Mirror should skip title spoofing only for Windows 8 titles.");
        var sameTitle = typeof(AchievementMirrorViewModel).GetMethod("SameXboxTitle", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check((bool)sameTitle.Invoke(null, ["1941986744", "1297292157"])!, "Mirror did not match the modern and Windows 8 title IDs for the same edition.");
        Console.WriteLine("PASS: Windows 8 legacy queue reads, submission, verification, and resume. Synthetic data; zero Xbox requests.");
    }
}
