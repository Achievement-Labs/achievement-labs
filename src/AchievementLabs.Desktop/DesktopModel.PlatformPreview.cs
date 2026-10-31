using AchievementLabs.Desktop.Platforms;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    internal async Task CheckPlatformAchievementLoadingAsync()
    {
        var first = new PlatformGameRow("EA", "loading-first", "First title", AccountId: "fixture-account");
        var second = first with { Id = "loading-second", Name = "Second title" };
        platformCatalogs["EA"] = new([first, second], "Fixture", ["fixture-account"]);
        connectedPlatformAccounts["EA"] = new("EA", "fixture-account", "Fixture", "fixture-token");
        var delayed = new TaskCompletionSource<PlatformAchievementsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new PlatformAchievementRow[] { new("EARNED", "Earned", "", true), new("LOCKED", "Locked", "", false) };
        PlatformAchievementReaderOverride = (_, game, _) => game.Id == first.Id ? delayed.Task : Task.FromResult(new PlatformAchievementsResult(rows, true, "Loaded fixture achievements"));
        try
        {
            Navigate("EA"); ShowPlatformLibrary(); CurrentPlatformGame = first;
            ShowPlatformAchievements();
            if (!PlatformAchievementsLoading) throw new Exception("Open achievements did not start the reader");
            CurrentPlatformGame = second;
            await Task.Delay(20);
            delayed.SetResult(new([new("STALE", "Stale result", "", true)], true, "Stale"));
            await Task.Delay(20);
            if (CurrentPlatformGame?.Id != second.Id || PlatformAchievementRows.Count() != 2 || CurrentPlatformGame.Earned != 1 || !CurrentPlatformGame.AchievementsLoaded || PlatformAchievementsLoading) throw new Exception($"Switching games: id={CurrentPlatformGame?.Id}, rows={PlatformAchievementRows.Count()}, earned={CurrentPlatformGame?.Earned}, loaded={CurrentPlatformGame?.AchievementsLoaded}, loading={PlatformAchievementsLoading}");
            var fail = true;
            PlatformAchievementReaderOverride = (_, _, _) => { if (fail) { fail = false; throw new IOException("Fixture network failure"); } return Task.FromResult(new PlatformAchievementsResult(rows, true, "Retried")); };
            await LoadPlatformAchievementsAsync(true);
            if (!PlatformAchievementStatus.Contains("retry", StringComparison.OrdinalIgnoreCase)) throw new Exception("Achievement read failure was silent");
            await LoadPlatformAchievementsAsync(true);
            if (CurrentPlatformGame.Total != 2 || PlatformAchievementStatus != "Retried") throw new Exception("Achievement retry failed");
        }
        finally
        {
            delayed.TrySetResult(new([], false, "Cancelled")); PlatformAchievementReaderOverride = null;
            platformAchievementCancellation?.Cancel(); connectedPlatformAccounts.Remove("EA"); platformCatalogs.Remove("EA");
            ShowPlatformLibrary(); CurrentPlatformGame = null;
        }
    }
    internal void LoadPlatformPreview(string platform, string fixtures)
    {
        var s = PlatformSnapshot.Parse(File.ReadAllText(Path.Combine(fixtures, platform + ".json")));
        var art = new Uri(Path.GetFullPath("src/AchievementLabs.App/Assets/achievement-labs-icon.png")).AbsoluteUri;
        var rows = s.Achievements.Select(a => a with { ImageUrl = art }).ToArray();
        var game = new PlatformGameRow(platform, s.GameId, s.Title, ImageUrl: art, Source: "Offline test fixture", Rows: rows, CatalogComplete: true, AccountId: s.AccountId);
        var complete = game with { Id = "complete", Name = "Completed fixture game", Rows = rows.Select(a => a with { Unlocked = true }).ToArray() };
        var unknown = game with { Id = "unknown", Name = "A long title with an unavailable achievement state to check the compact library layout", Rows = rows.Select(a => a with { Unlocked = null }).ToArray() };
        platformCatalogs[platform] = new([game, complete, unknown], "Offline fixtures. No account calls, achievement writes or client launches.", []);
        Navigate(platform);
        CurrentPlatformGame = game;
        ShowPlatformLibrary();
    }
    internal void AddPlatformLayoutFixtures()
    {
        var original = CurrentCatalog.Games;
        var extra = Enumerable.Range(0, 20).Select(i => original[0] with { Id = "layout-" + i + "-" + new string('A', 120), Name = "Uninstalled long title " + i + " " + new string('W', 160) }).ToArray();
        platformCatalogs[ActivePlatform] = CurrentCatalog with { Games = [.. original, .. extra] };
        NotifyPlatformWorkspace();
    }
    internal void CheckPlatformPresentation()
    {
        var rows = CurrentCatalog.Games;
        if (!rows.Single(g => g.Id == "complete").IsComplete || rows.Single(g => g.Id == "unknown").ProgressKnown) throw new Exception("Platform completion / unknown state");
        if ((rows[0] with { Rows = [], CatalogComplete = true }).IsComplete) throw new Exception("Zero achievements incorrectly complete");
        PlatformLibrarySort = "Most completed";
        if (PlatformLibraryGames.First().Id != "complete") throw new Exception("Platform completion sort");
        PlatformLibrarySearch = "unknown";
        if (PlatformLibraryGames.Single().Id != "unknown") throw new Exception("Platform ID search");
        PlatformLibrarySearch = "";
        CurrentPlatformGame = rows[0];
        PlatformAchievementFilter = "Locked";
        if (PlatformAchievementRows.Count() != 2) throw new Exception("Platform locked filter");
        PlatformAchievementFilter = "Unlocked";
        if (PlatformAchievementRows.Single().Id != "FIRST") throw new Exception("Platform unlocked filter");
        PlatformAchievementFilter = "All";
        PlatformAchievementSort = "Rarest first";
        if (PlatformAchievementRows.First().Id != "RARE" || PlatformAchievementRows.Last().Id != "UNKNOWN") throw new Exception("Platform rarity ordering");
        PlatformAchievementSearch = "hidden room";
        if (PlatformAchievementRows.Single().Id != "RARE") throw new Exception("Platform description search");
        PlatformAchievementSearch = "";
        CurrentPlatformAchievement = CurrentPlatformGame.Achievements[0];
    }
}
