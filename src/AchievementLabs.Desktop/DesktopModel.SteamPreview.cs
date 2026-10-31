using AchievementLabs.Models;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    internal void LoadSteamPreview()
    {
        lastSteamAppRestored = true;
        steamGames = [
            new() { AppId = 620, Name = "Portal 2" },
            new() { AppId = 400, Name = "Portal" },
            new() { AppId = 999, Name = "A title with a long name to verify library action sizing and wrapping" }
        ];
        steamGames[0].ApplyProgress(3, 3);
        steamGames[1].ApplyProgress(1, 15);
        steamGames[2].ApplyProgress(0, 12, false);
        loadedSteamAppId = 620;
        SteamAppId = "620";
        SteamAchievements = [
            new() { Id = "FIRST", Name = "First steps", Description = "Complete the first test chamber.", IsUnlocked = true, DesiredUnlocked = true, GlobalPercent = 70, UnlockTime = new DateTime(2026, 10, 20, 12, 0, 0) },
            new() { Id = "RARE", Name = "A rare discovery", Description = "Find the hidden room and complete its challenge.", IsUnlocked = true, DesiredUnlocked = true, GlobalPercent = 2.5 },
            new() { Id = "FINAL", Name = "The final challenge", Description = "Finish every chamber.", IsUnlocked = true, DesiredUnlocked = true }
        ];
        SelectedSteamAchievement = SteamAchievements[0];
        var art = new Uri(Path.GetFullPath("src/AchievementLabs.App/Assets/achievement-labs-icon.png")).AbsoluteUri;
        foreach (var game in steamGames) game.ImageUrl = art;
        foreach (var row in SteamAchievements) { row.IconUrl = art; row.LockedIconUrl = art; }
        SteamStatus = "Offline Steam UI fixture — no account calls.";
        RefreshSteamPresentation();
    }
    internal void AddSteamLayoutFixtures()
    {
        steamGames = [.. steamGames, .. Enumerable.Range(0, 20).Select(i => new SteamGameItem { AppId = 10000 + i, Name = "Uninstalled Steam title " + i + " " + new string('W', 160), ImageUrl = steamGames[0].ImageUrl })];
        RefreshSteamPresentation();
    }
    internal void LoadXboxLayoutPreview()
    {
        Games = [new("990000001", "An Xbox title " + new string('W', 160), "XboxOne", 1234, 2345, 1000), new("990000002", "Another Xbox title " + new string('W', 160), "XboxOne", 17, 50, 300)];
        Navigate("Library");
    }

    internal void CheckSteamPresentation()
    {
        if (!steamGames[0].IsComplete || steamGames[1].IsComplete || steamGames[2].IsComplete || steamGames[2].ProgressKnown) throw new Exception("Steam completion state");
        SteamLibrarySort = "Most completed";
        if (SteamGames.First().AppId != 620 || SteamGames.Last().AppId != 999) throw new Exception("Steam progress sort");
        SteamSearch = "400";
        if (SteamGames.Single().Name != "Portal") throw new Exception("Steam ID search");
        SteamSearch = "";
        SteamAchievementSort = "Rarest first";
        if (VisibleSteamAchievements.First().Id != "RARE" || VisibleSteamAchievements.Last().Id != "FINAL") throw new Exception("Steam rarity sorting / missing values");
        SteamAchievements[0].IsUnlocked = false;
        SteamAchievementFilter = "Locked";
        if (VisibleSteamAchievements.Single().Id != "FIRST" || SteamGameComplete) throw new Exception("Steam locked filter");
        SteamAchievementFilter = "Unlocked";
        if (VisibleSteamAchievements.Count() != 2) throw new Exception("Steam unlocked filter");
        SteamAchievementSearch = "hidden room";
        if (VisibleSteamAchievements.Single().Id != "RARE") throw new Exception("Steam description search");
        SteamAchievementSearch = "";
        SteamAchievementFilter = "All achievements";
        SteamAchievements[0].IsUnlocked = true;
        SteamAchievementSort = "Default";
        steamGames[0].ApplyProgress(0, 0);
        if (steamGames[0].IsComplete) throw new Exception("Zero achievements must not earn completion");
        steamGames[0].ApplyProgress(3, 3);
        SelectedSteamAchievement = SteamAchievements[0];
        RefreshSteamPresentation();
    }
}
