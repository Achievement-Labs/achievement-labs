using AchievementLabs.Core;
using AchievementLabs.Models;
using System.Globalization;

namespace AchievementLabs.Desktop;

public sealed record Xbox360LibraryEntry(Xbox360ProfileGame ProfileGame, string? ImageUrl)
{
    public string TitleId => ProfileGame.TitleId;
    public string Name => ProfileGame.Name;
    public string Progress => $"{ProfileGame.UnlockedCount} / {ProfileGame.AchievementCount} achievements";
    public string Score => $"{ProfileGame.GamerscoreUnlocked} / {ProfileGame.GamerscoreTotal} G";
    public bool Complete => ProfileGame.AchievementCount > 0 && ProfileGame.UnlockedCount == ProfileGame.AchievementCount;
    public double Completion => ProfileGame.AchievementCount > 0 ? 100d * ProfileGame.UnlockedCount / ProfileGame.AchievementCount : 0;
    public string LastPlayed => ProfileGame.LastPlayed?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Last played unavailable";
}

public sealed partial class DesktopModel
{
    private string xbox360LibrarySearch = "", xbox360LibrarySort = "Recently played", xbox360LibraryFilter = "All";
    private int xbox360LibraryLimit = 36;
    public string Xbox360LibrarySearch { get => xbox360LibrarySearch; set { xbox360LibrarySearch = value ?? ""; RefreshXbox360Library(); } }
    public string Xbox360LibrarySort { get => xbox360LibrarySort; set { xbox360LibrarySort = value; RefreshXbox360Library(); } }
    public string Xbox360LibraryFilter { get => xbox360LibraryFilter; set { xbox360LibraryFilter = value; RefreshXbox360Library(); } }
    public string[] Xbox360LibrarySorts { get; } = ["Recently played", "A–Z", "Completion"];
    public string[] Xbox360LibraryFilters { get; } = ["All", "Incomplete", "Completed"];
    private IEnumerable<Xbox360LibraryEntry> Xbox360LibraryEntries => Xbox360Games.Select(g => new Xbox360LibraryEntry(g,
        Games.FirstOrDefault(x => uint.TryParse(x.Id, out var id) && id.ToString("X8") == g.TitleId)?.ImageUrl));
    private IEnumerable<Xbox360LibraryEntry> FilteredXbox360Library
    {
        get
        {
            var items = Xbox360LibraryEntries.Where(g =>
                (TitleSearchText.Matches(g.Name, Xbox360LibrarySearch) || g.TitleId.Contains(Xbox360LibrarySearch, StringComparison.OrdinalIgnoreCase) ||
                    uint.Parse(g.TitleId, NumberStyles.HexNumber).ToString().Contains(Xbox360LibrarySearch)) &&
                (Xbox360LibraryFilter == "All" || Xbox360LibraryFilter == "Completed" && g.Complete || Xbox360LibraryFilter == "Incomplete" && !g.Complete));
            return Xbox360LibrarySort switch { "A–Z" => items.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
                "Completion" => items.OrderByDescending(g => g.Completion).ThenBy(g => g.Name),
                _ => items.OrderByDescending(g => g.ProfileGame.LastPlayed).ThenBy(g => g.Name) };
        }
    }
    public Xbox360LibraryEntry[] VisibleXbox360Library => FilteredXbox360Library.Take(xbox360LibraryLimit).ToArray();
    public string Xbox360LibrarySummary => $"{Xbox360Games.Length} games · {Xbox360Games.Count(g => g.AchievementCount > 0 && g.UnlockedCount == g.AchievementCount)} completed · {Xbox360Games.Sum(g => (long)g.UnlockedCount):N0} achievements unlocked";
    public string Xbox360LibraryCount => $"{FilteredXbox360Library.Count()} matching games";
    public bool Xbox360LibraryHasMore => FilteredXbox360Library.Count() > xbox360LibraryLimit;
    public bool Xbox360LibraryEmpty => Xbox360Games.Length == 0;
    public void MoreXbox360Games() { xbox360LibraryLimit += 36; Changed(nameof(VisibleXbox360Library)); Changed(nameof(Xbox360LibraryHasMore)); }
    private void RefreshXbox360Library()
    {
        xbox360LibraryLimit = 36;
        foreach (var property in new[] { nameof(Xbox360LibrarySearch), nameof(Xbox360LibrarySort), nameof(Xbox360LibraryFilter), nameof(VisibleXbox360Library), nameof(Xbox360LibrarySummary), nameof(Xbox360LibraryCount), nameof(Xbox360LibraryHasMore), nameof(Xbox360LibraryEmpty) }) Changed(property);
    }
    public async Task OpenXbox360LibraryGameAsync(Xbox360LibraryEntry entry)
    {
        if (busy) return;
        if (pendingXbox360Edits.Count != 0) { Notice = "Save or reload the pending profile edits before switching games."; return; }
        Xbox360EditorTitleId = entry.TitleId; Xbox360EditorTitleName = entry.Name; Xbox360ArtworkUrl = entry.ImageUrl;
        PackagePath = loadedXbox360Profile;
        Xbox360Achievements = [];
        Navigate("Xbox360ProfileEditor");
        await LoadXbox360EditorAchievementsAsync();
    }
}
