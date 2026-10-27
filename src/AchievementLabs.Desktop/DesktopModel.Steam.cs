using AchievementLabs.Models;
using AchievementLabs.Services.Steam;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private readonly SteamAchievementService steam = new();
    private SteamGameItem[] steamGames = [];
    private SteamAchievementItem[] steamAchievements = [];
    private SteamGameItem? selectedSteamGame;
    private string steamSearch = "", steamAppId = "", steamStatus = "Refresh installed games to begin.", steamProfileName = "Steam not detected", steamLevel = "Unknown";
    private string? steamAvatarUrl;
    public bool IsSteam => page is "SteamLibrary" or "SteamAchievements" or "SteamQueue";
    public bool IsSteamLibrary => page == "SteamLibrary";
    public bool IsSteamAchievements => page == "SteamAchievements";
    public string SteamProfileName { get => steamProfileName; private set { steamProfileName = value; Changed(); Changed(nameof(HomeSteamSummary)); } }
    public string SteamLevel { get => steamLevel; private set { steamLevel = value; Changed(); Changed(nameof(HomeSteamSummary)); } }
    public string? SteamAvatarUrl { get => steamAvatarUrl; private set { steamAvatarUrl = value; Changed(); } }
    public string SteamSearch { get => steamSearch; set { steamSearch = value ?? ""; Changed(); Changed(nameof(SteamGames)); } }
    public IEnumerable<SteamGameItem> SteamGames => steamGames.Where(g => g.DisplayName.Contains(SteamSearch, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString() == SteamSearch);
    public SteamGameItem? SelectedSteamGame { get => selectedSteamGame; set { selectedSteamGame = value; Changed(); if (value != null) SteamAppId = value.AppId.ToString(); } }
    private string steamAchievementSearch = "";
    public string SteamAchievementSearch { get => steamAchievementSearch; set { steamAchievementSearch = value ?? ""; Changed(); Changed(nameof(VisibleSteamAchievements)); } }
    public IEnumerable<SteamAchievementItem> VisibleSteamAchievements => steamAchievements.Where(a => (a.Name ?? "").Contains(SteamAchievementSearch, StringComparison.OrdinalIgnoreCase) || (a.Id ?? "").Contains(SteamAchievementSearch, StringComparison.OrdinalIgnoreCase));
    public SteamAchievementItem[] SteamAchievements { get => steamAchievements; private set { steamAchievements = value; Changed(); Changed(nameof(VisibleSteamAchievements)); } }
    public string SteamAppId { get => steamAppId; set { steamAppId = value ?? ""; Changed(); } }
    public string SteamStatus { get => steamStatus; private set { steamStatus = value; Changed(); Changed(nameof(HomeSteamSummary)); } }
    public void RefreshSteamProfile()
    {
        try { ApplySteamProfile(steam.GetProfileSummary()); }
        catch { SteamProfileName = "Steam unavailable"; SteamLevel = "Unknown"; SteamAvatarUrl = null; }
    }
    public async Task RefreshHomeProfilesAsync()
    {
        Navigate("Home");
        try { ApplySteamProfile(await Task.Run(steam.GetProfileSummary, lifetime.Token)); }
        catch { SteamProfileName = "Steam unavailable"; SteamLevel = "Unknown"; SteamAvatarUrl = null; }
    }
    private void ApplySteamProfile(SteamProfileSummary profile)
    {
        SteamProfileName = profile.PersonaName;
        SteamLevel = profile.SteamLevel;
        SteamAvatarUrl = profile.AvatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? profile.AvatarUrl : null;
        SteamStatus = $"{profile.GameCount} games · Steam {(profile.IsSteamRunning ? "running" : "not running")}";
    }
    public async Task RefreshSteamAsync()
    {
        if (!CanInteract) return;
        Navigate("SteamLibrary"); Busy(true);
        try { steamGames = (await Task.Run(steam.GetInstalledGames, lifetime.Token)).ToArray(); Changed(nameof(SteamGames)); SteamStatus = $"{steamGames.Length} games · Steam {(steam.IsSteamRunning() ? "running" : "not running")}"; }
        catch { SteamStatus = "Could not read the Steam library."; }
        finally { Busy(false); }
    }
    private long loadedSteamAppId;
    public async Task LoadSteamAchievementsAsync() => await SteamOperationAsync(async id =>
    {
        SteamAchievements = []; loadedSteamAppId = 0;
        var loaded = await Task.Run(() => steam.LoadAchievementsAsync(id, lifetime.Token), lifetime.Token);
        SteamAchievements = loaded.ToArray(); loadedSteamAppId = id; SteamStatus = $"Loaded {loaded.Count} achievements for app {id}."; Navigate("SteamAchievements");
    });
    public async Task StoreSteamAchievementsAsync() => await SteamOperationAsync(async id =>
    {
        if (id != loadedSteamAppId) { SteamStatus = "Load achievements for this app ID before saving changes."; return; }
        var snapshot = SteamAchievements.ToArray();
        var count = await Task.Run(() => steam.StoreAchievementsAsync(id, snapshot, lifetime.Token), lifetime.Token);
        SteamStatus = $"Stored {count} changes. Reload to verify.";
    });
    public void InvertSteam() { if (CanInteract) foreach (var item in SteamAchievements) item.DesiredUnlocked = !item.DesiredUnlocked; }
    public void RevertSteam() { if (CanInteract) foreach (var item in SteamAchievements) item.DesiredUnlocked = item.IsUnlocked; }
    public async Task ResetSteamStatsAsync() => await SteamOperationAsync(async id => { await steam.ResetAsync(id, false, lifetime.Token); SteamStatus = "Stats reset request completed."; });
    public async Task RefreshSteamMetadataAsync()
    {
        if (!CanInteract) return; Busy(true);
        try { await steam.RefreshSteamAppNameCacheAsync(lifetime.Token); await steam.RefreshSteamStoreNamesAsync(steamGames.Select(g => g.AppId), lifetime.Token); SteamStatus = steam.GetSteamMetadataStatus(); }
        catch { SteamStatus = "Could not refresh Steam metadata."; }
        finally { Busy(false); }
        await RefreshSteamAsync();
    }
    public void SetSteamDesired(bool unlocked) { if (CanInteract) foreach (var item in SteamAchievements) item.DesiredUnlocked = unlocked; }
    public async Task StartSteamPresenceAsync() => await SteamOperationAsync(async id => { var result = await steam.StartSpoofSessionAsync(id, lifetime.Token); SteamStatus = result.Status; });
    public void StopSteamPresence() { steam.StopSpoofSession(); SteamStatus = steam.GetSpoofSessionInfo().Status; }
    private async Task SteamOperationAsync(Func<long, Task> operation)
    {
        if (!CanInteract) return;
        if (!long.TryParse(SteamAppId, out var id) || id <= 0) { SteamStatus = "Enter a valid Steam app ID."; return; }
        Busy(true);
        try { await operation(id); }
        catch (OperationCanceledException) { SteamStatus = "Steam operation cancelled."; }
        catch { SteamStatus = "Steam operation failed. Ensure Steam is running and the selected app is available."; }
        finally { Busy(false); }
    }
}
