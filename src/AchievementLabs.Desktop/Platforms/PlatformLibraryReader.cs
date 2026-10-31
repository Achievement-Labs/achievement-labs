using AchievementLabs.Services.Epic;
using AchievementLabs.Services.Ubisoft;

namespace AchievementLabs.Desktop.Platforms;

internal static class PlatformLibraryReader
{
    public static PlatformCatalogResult Read(string platform, string account, EpicAchievementService epic, UbisoftAchievementService ubisoft, CancellationToken token)
    {
        if (platform == "GOG") return GogCatalogReader.Read(GogCatalogReader.DefaultDatabase, account, token);
        if (platform == "EA") return new(EaCatalogReader.Read(token), "EA installation records. Import an achievement snapshot to display definitions and player states. EA Play entitlement and subscription libraries are not synced in this build.", []);
        var games = new List<PlatformGameRow>();
        if (platform == "Epic")
        {
            foreach (var g in epic.GetInstalledGames())
            {
                token.ThrowIfCancellationRequested();
                var rows = epic.LoadAchievementPlaceholders(g).Where(a => a.IsDefinition).Select(a => new PlatformAchievementRow(a.Id, a.Name, a.Description, a.Unlocked, a.ImageUrl, a.GlobalPercent, Source: "Epic captured data")).ToArray();
                var launch = g.AppName.Length > 0 ? "com.epicgames.launcher://apps/" + Uri.EscapeDataString(g.NamespaceId.Length > 0 && g.CatalogItemId.Length > 0 ? $"{g.NamespaceId}:{g.CatalogItemId}:{g.AppName}" : g.AppName) + "?action=launch&silent=true" : "";
                games.Add(new("Epic", g.PrimaryId, g.Title, g.InstallPath, Art(g.ImageUrl), launch, g.Source, rows));
            }
            return new(games.ToArray(), "Local Epic library and captured achievements. Capture completeness and account identity are unverified; imported snapshots can supply an explicit account and complete catalog.", []);
        }
        foreach (var g in ubisoft.GetInstalledGames().Concat(ubisoft.LoadCapturedGames()).GroupBy(g => g.GameId).Select(g => g.First()))
        {
            token.ThrowIfCancellationRequested();
            var rows = ubisoft.LoadAchievementPlaceholders(g).Select(a => new PlatformAchievementRow(a.Id, a.Name, a.Description, a.Unlocked, a.ImageUrl, a.GlobalPercent, Source: "Ubisoft captured data")).ToArray();
            games.Add(new("Ubisoft", g.GameId, g.Title, g.InstallPath, Art(g.ImageUrl), $"uplay://launch/{Uri.EscapeDataString(g.GameId)}/0", g.Source, rows));
        }
        return new(games.ToArray(), "Local Ubisoft library and captured achievements. Existing stats, challenges, spool installer and overlay tools remain under Platform tools.", []);
    }
    private static string Art(string url) => url.StartsWith("pack:", StringComparison.OrdinalIgnoreCase) ? "" : url;
}
