using System.Globalization;

namespace AchievementLabs.Desktop.Platforms;

public sealed record PlatformAchievementRow(string Id, string Name, string Description, bool? Unlocked = null,
    string ImageUrl = "", double? GlobalPercent = null, string UnlockTime = "", string Source = "")
{
    public string Status => Unlocked switch { true => "Unlocked", false => "Locked", _ => "Unknown" };
    public string GlobalLabel => GlobalPercent?.ToString("0.##", CultureInfo.CurrentCulture) is { } p ? p + "%" : "Unavailable";
    public string Rarity => GlobalPercent is { } p ? p <= 5 ? "Rare" : "Common" : "Unavailable";
}

public sealed record PlatformGameRow(string Platform, string Id, string Name, string InstallPath = "", string ImageUrl = "",
    string LaunchUri = "", string Source = "", PlatformAchievementRow[]? Rows = null, bool CatalogComplete = false,
    string AccountId = "", string ServiceId = "", bool AchievementsLoaded = false, string AchievementStatus = "", string AchievementCatalogKey = "", string AchievementProductId = "")
{
    public PlatformAchievementRow[] Achievements => Rows ?? [];
    public int Earned => Achievements.Count(a => a.Unlocked == true);
    public int Total => Achievements.Length;
    public bool ProgressKnown => CatalogComplete && Total > 0 && Achievements.All(a => a.Unlocked.HasValue);
    public double Progress => ProgressKnown ? 100d * Earned / Total : 0;
    public bool IsComplete => ProgressKnown && Earned == Total;
    public string ProgressLabel => ProgressKnown ? $"{Earned} / {Total} · {Progress:0.#}%" : Total > 0 ? $"{Earned} earned · {Total} loaded · partial" : "Achievements unavailable";
    public string Background => IsComplete ? "#183A27" : "Transparent";
    public string Monogram => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string InstallLabel => Directory.Exists(InstallPath) ? "Installed" : "Not installed";
    public bool CanLaunch => LaunchUri.Length > 0;
    public string Capability => "Achievement writes and standalone spoofing are unavailable in this test build. Launching through the client starts the actual game.";
}

public sealed record PlatformCatalogResult(PlatformGameRow[] Games, string Status, string[] Accounts);
internal sealed record PlatformAchievementsResult(PlatformAchievementRow[] Rows, bool Complete, string Status);
