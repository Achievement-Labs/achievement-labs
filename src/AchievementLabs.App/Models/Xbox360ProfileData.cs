namespace AchievementLabs.Models;

public sealed record Xbox360ProfileGame(
    string TitleId, string Name, int AchievementCount, int UnlockedCount,
    int GamerscoreTotal, int GamerscoreUnlocked, DateTimeOffset? LastPlayed = null);

public sealed record Xbox360ProfileAchievement(
    uint Id, string Name, string LockedDescription, string UnlockedDescription,
    uint Gamerscore, uint Flags, DateTimeOffset? UnlockedAt)
{
    public string Status => (Flags & 0x20000) == 0 ? "Locked"
        : (Flags & 0x10000) != 0 ? "Unlocked Online" : "Unlocked Offline";
}
