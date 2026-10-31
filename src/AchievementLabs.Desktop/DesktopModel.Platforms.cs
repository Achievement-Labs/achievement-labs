using AchievementLabs.Models;
using AchievementLabs.Services.Epic;
using AchievementLabs.Services.Ubisoft;
namespace AchievementLabs.Desktop;
public sealed record PlatformTitle(string Id, string Name, string Context, object Source);
public sealed record PlatformDetail(string Id, string Name, string Detail, string State);
public sealed partial class DesktopModel
{
    private readonly EpicAchievementService epic = new();
    private readonly UbisoftAchievementService ubisoft = new();
    private PlatformTitle[] platformTitles = [];
    private PlatformDetail[] platformDetails = [];
    private PlatformTitle? selectedPlatformTitle;
    private string platformSearch = "", platformSection = "Achievements";
    private EpicEosConfig epicConfig = new();
    private UbisoftServiceConfig ubisoftConfig = new();
    public bool IsPlatform => page is "Epic" or "EpicAchievements" or "Ubisoft" or "UbisoftAchievements" or "EA" or "EAAchievements" or "GOG" or "GOGAchievements";
    public bool IsEpic => page is "Epic" or "EpicAchievements";
    public bool IsUbisoft => page is "Ubisoft" or "UbisoftAchievements";
    public EpicEosConfig EpicConfig { get => epicConfig; private set { epicConfig = value; Changed(); } }
    public UbisoftServiceConfig UbisoftConfig { get => ubisoftConfig; private set { ubisoftConfig = value; Changed(); } }
    public IEnumerable<PlatformTitle> PlatformTitles => platformTitles.Where(t => t.Name.Contains(PlatformSearch, StringComparison.OrdinalIgnoreCase) || t.Id == PlatformSearch);
    public PlatformTitle? SelectedPlatformTitle { get => selectedPlatformTitle; set { selectedPlatformTitle = value; Changed(); LoadPlatformDetails(); } }
    public PlatformDetail? SelectedPlatformDetail { get; set; }
    public void OpenPlatformFolder(bool captures)
    {
        try { var path = captures ? (IsEpic ? EpicConfig.CaptureFolder : UbisoftConfig.CaptureFolder) : SelectedPlatformTitle?.Source switch { EpicGameItem e => e.InstallPath, UbisoftGameItem u => u.InstallPath, _ => "" }; if (!Directory.Exists(path)) throw new DirectoryNotFoundException(); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { Notice = "The selected folder is not available."; }
    }
    public PlatformDetail[] PlatformDetails { get => platformDetails; private set { platformDetails = value; Changed(); } }
    public string PlatformSearch { get => platformSearch; set { platformSearch = value ?? ""; Changed(); Changed(nameof(PlatformTitles)); } }
    public string[] PlatformSections { get; } = ["Achievements", "Stats", "Challenges", "Cross-progression"];
    public string PlatformSection { get => platformSection; set { platformSection = value; Changed(); LoadPlatformDetails(); } }
    private async Task OpenPlatformToolsAsync(string platform)
    {
        if (busy) return;
        Navigate(platform); Busy(true); platformTitles = []; PlatformDetails = []; Changed(nameof(PlatformTitles));
        try
        {
            if (platform == "Epic")
            {
                var result = await Task.Run(() => (Config: epic.LoadConfig(), Games: epic.GetInstalledGames()), lifetime.Token);
                if (!IsEpic) return;
                EpicConfig = result.Config;
                platformTitles = result.Games.Select(g => new PlatformTitle(g.PrimaryId, g.Title, $"Title: {g.Title}\nAppName: {g.AppName}\nCatalogItemId: {g.CatalogItemId}\nArtifactId: {g.ArtifactId}\nNamespaceId: {g.NamespaceId}\nInstallPath: {g.InstallPath}\nManifestPath: {g.ManifestPath}", g)).ToArray();
            }
            else
            {
                LoadUbisoftTools();
                var result = await Task.Run(() => (Config: ubisoft.LoadConfig(), Games: ubisoft.GetInstalledGames().Concat(ubisoft.LoadCapturedGames()).GroupBy(g => g.GameId).Select(g => g.First()).ToArray()), lifetime.Token);
                if (!IsUbisoft) return;
                UbisoftConfig = result.Config;
                platformTitles = result.Games.Select(g => new PlatformTitle(g.GameId, g.Title, $"Title: {g.Title}\nGameId: {g.GameId}\nInstallPath: {g.InstallPath}\nSource: {g.Source}", g)).ToArray();
            }
            Changed(nameof(PlatformTitles)); SelectedPlatformTitle = platformTitles.FirstOrDefault(); Notice = $"Loaded {platformTitles.Length} {platform} titles.";
        }
        catch (OperationCanceledException) { }
        catch { Notice = $"Could not read {platform} installations or captured data."; }
        finally { Busy(false); }
    }
    private void LoadPlatformDetails()
    {
        try
        {
            PlatformDetails = IsEpic
                ? epic.LoadAchievementPlaceholders(SelectedPlatformTitle?.Source as EpicGameItem).Select(a => new PlatformDetail(a.Id, a.Name, a.Description, a.State)).ToArray()
                : PlatformSection switch
                {
                    "Stats" => ubisoft.LoadStatPlaceholders(SelectedPlatformTitle?.Source as UbisoftGameItem).Select(a => new PlatformDetail(a.Id, a.Name, a.Value, a.Status)).ToArray(),
                    "Challenges" => ubisoft.LoadChallengePlaceholders(SelectedPlatformTitle?.Source as UbisoftGameItem).Select(a => new PlatformDetail(a.Id, a.Name, a.Description, a.Progress + " · " + a.Status)).ToArray(),
                    "Cross-progression" => ubisoft.LoadCrossProgressionCandidates().Select(a => new PlatformDetail(a.GameId, a.Title, a.Evidence, a.Status)).ToArray(),
                    _ => ubisoft.LoadAchievementPlaceholders(SelectedPlatformTitle?.Source as UbisoftGameItem).Select(a => new PlatformDetail(a.Id, a.Name, a.Description, a.State)).ToArray()
                };
        }
        catch { PlatformDetails = []; Notice = "Could not read this title's captured data."; }
    }
    public void SavePlatformConfig()
    {
        try { if (IsEpic) epic.SaveConfig(EpicConfig); else ubisoft.SaveConfig(UbisoftConfig); Notice = "Platform configuration saved."; }
        catch { Notice = "Could not save platform configuration."; }
    }
}
