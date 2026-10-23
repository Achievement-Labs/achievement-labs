using AchievementLabs.Mirror;

/// <summary>Achievement Mirror: matching, no-backfill, verification, stop-on-failure and parsing. No network.</summary>
public static class MirrorTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static MirrorAchievement A(string id, string name, bool unlocked = false, int minute = 0) =>
        new(id, name, "", unlocked, unlocked ? new DateTimeOffset(2026, 10, 23, 20, minute, 0, TimeSpan.Zero) : null);

    sealed class FakeTarget : IMirrorTarget
    {
        public readonly HashSet<string> Unlocked = [];
        public readonly List<string> Sent = [];
        public int FailuresLeft;
        public bool IgnoreUnlocks;
        public Task<IReadOnlyList<MirrorAchievement>> ReadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MirrorAchievement>>(Unlocked.Select(id => A(id, id, true)).ToArray());
        public Task UnlockAsync(string id, CancellationToken ct)
        {
            Sent.Add(id);
            if (FailuresLeft > 0) { FailuresLeft--; throw new HttpRequestException("HTTP 503"); }
            if (!IgnoreUnlocks) Unlocked.Add(id);
            return Task.CompletedTask;
        }
    }

    static (MirrorPair Pair, FakeTarget Target, MirrorRunner Runner) Setup(bool appUnlocks = false)
    {
        var steam = new[] { A("ACH_1", "First Steps"), A("ACH_2", "Second Wind"), A("ACH_3", "Third Act"), A("ACH_4", "Steam Only") };
        var xbox = new[] { A("1", "First Steps"), A("2", "Second Wind"), A("3", "Third Act") };
        var pair = new MirrorPair { SteamAppId = 3669870, XboxTitleId = "1653280174", XboxName = "Test", MirrorAppUnlocks = appUnlocks,
            Matches = MirrorMatcher.Build(MirrorDirection.SteamToXbox, steam, xbox) };
        pair.FindBySource("ACH_4")!.State = MirrorMatchState.Ignored;
        var target = new FakeTarget();
        return (pair, target, new MirrorRunner(pair, target, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]));
    }

    public static void Run()
    {
        Matching();
        Runtime().GetAwaiter().GetResult();
        Parsing();
        Store();
        Console.WriteLine("PASS: Achievement Mirror matching (exact, ambiguous, close, kept decisions, both directions), no-backfill baseline, verify-after-unlock, already-unlocked skip, retries, stop-on-failure with ordered pending, resume send/discard, app-unlock toggle, ignored and unmatched rows, Steam Web API and Xbox presence parsing, pair storage. No network.");
    }

    static void Matching()
    {
        var steam = new[] { A("a", "Graffiti Artist"), A("b", "Dupe"), A("c", "Lights Out!"), A("d", "Completely Different"), A("e", "Hidden 7") };
        var xbox = new[] { A("1", "graffiti artist"), A("2", "Dupe"), A("3", "Dupe"), A("4", "Lights Out"), A("5", "Something Else") };
        var rows = MirrorMatcher.Build(MirrorDirection.SteamToXbox, steam, xbox);
        Assert(rows.Count == 5 && rows.All(r => r.SteamId.Length > 0), "One row per Steam achievement");
        Assert(rows[0].State == MirrorMatchState.Matched && rows[0].XboxId == "1", "Case/punctuation-insensitive exact match is automatic");
        Assert(rows[1].State == MirrorMatchState.Suggested, "Two Xbox achievements with the same name are never auto-matched");
        Assert(rows[2].State == MirrorMatchState.Matched && rows[2].XboxId == "4", "Punctuation difference still matches");
        Assert(rows[3].State == MirrorMatchState.Unmatched && rows[3].XboxId == null && !rows[3].CanMirror, "No candidate stays unmatched");
        var close = MirrorMatcher.Build(MirrorDirection.SteamToXbox, [A("x", "Master of Disguise")], [A("9", "Master of Disguises")]);
        Assert(close[0].State == MirrorMatchState.Suggested && !close[0].CanMirror && !close[0].IsResolved, "Close names need confirmation and can't mirror until confirmed");

        rows[3].State = MirrorMatchState.Ignored;
        MirrorMatcher.SetTarget(rows[4], MirrorDirection.SteamToXbox, xbox[4]); rows[4].State = MirrorMatchState.Confirmed;
        var again = MirrorMatcher.Build(MirrorDirection.SteamToXbox, steam, xbox, rows);
        Assert(again[3].State == MirrorMatchState.Ignored && again[4].State == MirrorMatchState.Confirmed && again[4].XboxId == "5", "Rescan keeps the user's decisions");
        var gone = MirrorMatcher.Build(MirrorDirection.SteamToXbox, steam, xbox.Take(4).ToArray(), again);
        Assert(gone[4].State == MirrorMatchState.Unmatched, "A confirmed target that disappeared must be reviewed again");

        var reverse = MirrorMatcher.Build(MirrorDirection.XboxToSteam, steam, xbox);
        Assert(reverse.Count == 5 && reverse.All(r => r.XboxId != null) && reverse[0].SteamId == "a", "Xbox → Steam rows are keyed by Xbox achievement");
        var pair = new MirrorPair { Direction = MirrorDirection.XboxToSteam, Matches = reverse };
        Assert(!pair.ReadyToMonitor && pair.FindBySource("1")?.TargetId(MirrorDirection.XboxToSteam) == "a", "Unreviewed rows block monitoring");
        Assert(MirrorMatcher.Similarity("abc", "abc") == 1 && MirrorMatcher.Similarity("abc", "") == 0, "Similarity bounds");
    }

    static async Task Runtime()
    {
        // Not ready → refuse to start.
        var (blocked, _, blockedRunner) = Setup();
        blocked.Matches[0].State = MirrorMatchState.Unmatched;
        try { blockedRunner.Start([]); throw new Exception("Expected refusal"); } catch (InvalidOperationException) { }

        // No backfill, verified unlocks, oldest first.
        var (pair, target, runner) = Setup();
        runner.Start([A("ACH_1", "First Steps", true)]);
        Assert(runner.Status == MirrorStatus.Monitoring, "Monitoring");
        Assert(await runner.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test") && target.Sent.Count == 0, "Already-unlocked achievements are never copied");
        var ok = await runner.ObserveSourceAsync([A("ACH_1", "First Steps", true), A("ACH_3", "Third Act", true, 5), A("ACH_2", "Second Wind", true, 2)], "test");
        Assert(ok && target.Sent.SequenceEqual(["2", "3"]) && runner.MirroredCount == 2, "New unlocks mirrored in unlock order and verified");
        await runner.ObserveSourceAsync([A("ACH_2", "Second Wind", true, 2)], "another detector");
        Assert(target.Sent.Count == 2, "A second detector seeing the same unlock sends nothing");
        Assert(await runner.ObserveSourceAsync([A("ACH_4", "Steam Only", true, 6)], "test") && target.Sent.Count == 2, "Rows marked 'no equivalent' are skipped");

        // Already unlocked on the target → no request.
        var (_, t2, r2) = Setup();
        r2.Start([]); t2.Unlocked.Add("1");
        Assert(await r2.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test") && t2.Sent.Count == 0, "Target already unlocked: no request sent");

        // Transient failure then success.
        var (_, t3, r3) = Setup();
        r3.Start([]); t3.FailuresLeft = 2;
        Assert(await r3.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test") && t3.Sent.Count == 3 && r3.Status == MirrorStatus.Monitoring, "Retries recover from brief errors");

        // Persistent failure → stop, later unlocks pending, nothing sent.
        var (_, t4, r4) = Setup();
        string? alert = null; r4.Stopped += (_, message) => alert = message;
        r4.Start([]); t4.IgnoreUnlocks = true;
        Assert(!await r4.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test"), "Unverified unlock reports failure");
        Assert(r4.Status == MirrorStatus.Stopped && alert != null && r4.StopReason.Contains("still shows it locked"), "Stopped with a reason and an alert");
        var sentBefore = t4.Sent.Count;
        Assert(!await r4.ObserveSourceAsync([A("ACH_2", "Second Wind", true, 1), A("ACH_3", "Third Act", true, 2)], "test") && t4.Sent.Count == sentBefore, "Nothing is sent while stopped");
        Assert(r4.Pending.Select(p => p.SourceId).SequenceEqual(["ACH_1", "ACH_2", "ACH_3"]), "Failed and later unlocks are pending in order");
        t4.IgnoreUnlocks = false;
        Assert(await r4.ResumeAsync(true) && t4.Unlocked.SetEquals(["1", "2", "3"]) && r4.Pending.Count == 0, "Resume sends pending in order");

        var (_, t5, r5) = Setup();
        r5.Start([]); t5.IgnoreUnlocks = true;
        await r5.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test");
        var sent5 = t5.Sent.Count;
        Assert(await r5.ResumeAsync(false) && t5.Sent.Count == sent5 && r5.Status == MirrorStatus.Monitoring, "Resume and discard sends nothing");
        await r5.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test");
        Assert(t5.Sent.Count == sent5, "Discarded unlocks are not re-detected");

        // Unmatched row at runtime (e.g. new DLC after scanning) stops instead of guessing.
        var (_, t6, r6) = Setup();
        r6.Start([]);
        Assert(!await r6.ObserveSourceAsync([A("NEW_DLC", "Brand New", true)], "test") && r6.Status == MirrorStatus.Stopped && t6.Sent.Count == 0, "Unknown achievement stops the pair");

        // App unlock toggle off: skipped, and the detector doesn't send it later.
        var (_, t7, r7) = Setup(appUnlocks: false);
        r7.Start([]);
        Assert(await r7.ReportAppUnlockAsync("ACH_1", "First Steps") && t7.Sent.Count == 0, "Toggle off: app unlock not mirrored");
        await r7.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test");
        Assert(t7.Sent.Count == 0, "Toggle off: detector skips the app's own unlock");

        // Toggle on: immediate, no double send.
        var (_, t8, r8) = Setup(appUnlocks: true);
        r8.Start([]);
        Assert(await r8.ReportAppUnlockAsync("ACH_2", "Second Wind") && t8.Sent.SequenceEqual(["2"]), "Toggle on: sent immediately");
        await r8.ObserveSourceAsync([A("ACH_2", "Second Wind", true)], "test");
        Assert(t8.Sent.Count == 1, "Toggle on: detector doesn't send it again");
        t8.IgnoreUnlocks = true;
        Assert(!await r8.ReportAppUnlockAsync("ACH_3", "Third Act"), "App unlock that can't be mirrored returns false so Auto Unlock stops");
        Assert(!await r8.ReportAppUnlockAsync("ACH_1", "First Steps") && t8.Sent.Count == 4, "While stopped, app unlocks are pending and return false");

        // User stop: nothing more is sent.
        var (_, t9, r9) = Setup();
        r9.Start([]); r9.Stop();
        Assert(await r9.ObserveSourceAsync([A("ACH_1", "First Steps", true)], "test") && t9.Sent.Count == 0 && r9.Status == MirrorStatus.Idle, "Stopped by user: nothing sent");
        Assert(await r9.ReportAppUnlockAsync("ACH_1", "First Steps"), "Not mirroring: app unlocks aren't blocked");
    }

    static void Parsing()
    {
        var ok = SteamWebApi.Parse("""{"playerstats":{"steamID":"76561198000000000","gameName":"G","achievements":[{"apiname":"A1","achieved":1,"unlocktime":1761249600,"name":"One","description":"d"},{"apiname":"A2","achieved":0,"unlocktime":0,"name":"Two"}],"success":true}}""");
        Assert(ok.Count == 2 && ok[0].Unlocked && ok[0].UnlockedAt == DateTimeOffset.FromUnixTimeSeconds(1761249600) && ok[0].Name == "One" && !ok[1].Unlocked && ok[1].UnlockedAt == null, "Steam Web API parsed");
        try { SteamWebApi.Parse("""{"playerstats":{"error":"Profile is not public","success":false}}"""); throw new Exception("Expected error"); }
        catch (SteamWebApiException error) { Assert(error.Message.Contains("Game details to Public"), "Private profile explained"); }
        Assert(SteamWebApi.Parse("""{"playerstats":{"success":true}}""").Count == 0, "Game without achievements");
        Assert(SteamWebApi.IsSteamId64("76561198012345678") && !SteamWebApi.IsSteamId64("123") && !SteamWebApi.IsSteamId64(""), "SteamID64 validation");
        Assert(SteamWebApi.AccountId("76561198012345678") == 52079950, "Account number for local stats file");
        Assert(!SteamWebApi.BuildUrl("k&y", "1", 2).Contains("k&y") && SteamWebApi.BuildUrl("k", "1", 3669870).Contains("appid=3669870"), "URL escapes the key");

        var presence = """{"xuid":"1","state":"Online","devices":[{"type":"PC","titles":[{"id":"750323071","name":"Home","state":"Active"},{"id":"1653280174","name":"CONTROL Resonant","placement":"Full","state":"Active"}]}]}""";
        Assert(XboxPresence.IsInTitle(presence, "1653280174") && !XboxPresence.IsInTitle(presence, "1828326430"), "Presence finds the active title");
        Assert(!XboxPresence.IsInTitle("""{"devices":[{"titles":[{"id":"1653280174","state":"Inactive"}]}]}""", "1653280174"), "Inactive title doesn't count");
        Assert(!XboxPresence.IsInTitle("""{"xuid":"1","state":"Offline"}""", "1653280174"), "Offline has no devices");
    }

    static void Store()
    {
        var path = Path.Combine(Path.GetTempPath(), "al-mirror-test-" + Guid.NewGuid().ToString("N"), "pairs.json");
        try
        {
            var (pair, _, _) = Setup(appUnlocks: true);
            pair.Direction = MirrorDirection.XboxToSteam; pair.SteamList = [new("ACH_1", "First Steps")];
            AchievementLabs.Core.MirrorStore.SavePairs([pair], path);
            var loaded = AchievementLabs.Core.MirrorStore.LoadPairs(path).Single();
            Assert(loaded.Id == pair.Id && loaded.Direction == MirrorDirection.XboxToSteam && loaded.MirrorAppUnlocks && loaded.Matches.Count == pair.Matches.Count
                && loaded.Matches[3].State == MirrorMatchState.Ignored && loaded.SteamList.Single().Name == "First Steps", "Pairs round-trip");
            Assert(File.ReadAllText(path).Contains("\"SteamToXbox\"") == false && File.ReadAllText(path).Contains("XboxToSteam"), "Enums saved as readable names");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}
