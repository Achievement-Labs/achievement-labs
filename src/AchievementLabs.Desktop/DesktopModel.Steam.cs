using AchievementLabs.Models;
using AchievementLabs.Services.Steam;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private readonly SteamAchievementService steam = new();
    private SteamGameItem[] steamGames = [];
    private string steamLibraryOwner = "";
    private SteamAchievementItem[] steamAchievements = [];
    private SteamGameItem? selectedSteamGame;
    private string steamSearch = "", steamAppId = ReadLastSteamApp() is var lastApp and > 0 ? lastApp.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", steamStatus = "Refresh installed games to begin.", steamProfileName = "Steam not detected", steamLevel = "Unknown";
    private string? steamAvatarUrl;
    public bool IsSteam => page is "SteamLibrary" or "SteamAchievements" or "SteamQueue";
    public bool IsSteamLibrary => page == "SteamLibrary";
    public bool IsSteamAchievements => page == "SteamAchievements";
    public string SteamProfileName { get => steamProfileName; private set { steamProfileName = value; Changed(); Changed(nameof(HomeSteamSummary)); } }
    public string SteamLevel { get => steamLevel; private set { steamLevel = value; Changed(); Changed(nameof(HomeSteamSummary)); } }
    public string? SteamAvatarUrl { get => steamAvatarUrl; private set { steamAvatarUrl = value; Changed(); } }
    public string SteamSearch { get => steamSearch; set { steamSearch = value ?? ""; Changed(); Changed(nameof(SteamGames)); } }
    public string[] SteamLibrarySortOptions { get; } = ["Name A–Z", "Name Z–A", "Most completed", "Least completed", "Achievements earned", "App ID"];
    private string steamLibrarySort = "Name A–Z";
    public string SteamLibrarySort { get => steamLibrarySort; set { steamLibrarySort = value; Changed(); Changed(nameof(SteamGames)); } }
    public IEnumerable<SteamGameItem> SteamGames {
        get {
            var rows = steamGames.Where(g => g.DisplayName.Contains(SteamSearch, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString() == SteamSearch);
            return SteamLibrarySort switch {
                "Name Z–A" => rows.OrderByDescending(g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
                "Most completed" => rows.OrderByDescending(g => g.ProgressKnown).ThenByDescending(g => g.Progress).ThenBy(g => g.DisplayName),
                "Least completed" => rows.OrderByDescending(g => g.ProgressKnown).ThenBy(g => g.Progress).ThenBy(g => g.DisplayName),
                "Achievements earned" => rows.OrderByDescending(g => g.ProgressKnown).ThenByDescending(g => g.EarnedAchievements).ThenBy(g => g.DisplayName),
                "App ID" => rows.OrderBy(g => g.AppId),
                _ => rows.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
    public string SteamLibrarySummary => $"{steamGames.Length} titles · {steamGames.Where(g => g.ProgressKnown).Sum(g => g.EarnedAchievements)} / {steamGames.Where(g => g.ProgressKnown).Sum(g => g.TotalAchievements)} achievements · {steamGames.Count(g => g.IsComplete)} completed · {steamGames.Count(g => !g.ProgressKnown)} totals unavailable";
    public SteamGameItem? SteamAchievementGame => steamGames.FirstOrDefault(g => g.AppId == loadedSteamAppId);
    public string SteamGameName => SteamAchievementGame?.DisplayName ?? (loadedSteamAppId > 0 ? $"App {loadedSteamAppId}" : "Steam achievements");
    public string SteamGameImage => SteamAchievementGame?.ImageUrl ?? (loadedSteamAppId > 0 ? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{loadedSteamAppId}/header.jpg" : "");
    public string SteamGameProgress => $"{SteamAchievements.Count(a => a.IsUnlocked)} / {SteamAchievements.Length} achievements";
    public bool SteamGameComplete => SteamAchievements.Length > 0 && SteamAchievements.All(a => a.IsUnlocked);
    private void RefreshSteamPresentation() {
        foreach (var name in new[] { nameof(SteamGames), nameof(SteamLibrarySummary), nameof(SteamAchievementGame), nameof(SteamGameName), nameof(SteamGameImage), nameof(SteamGameProgress), nameof(SteamGameComplete) }) Changed(name);
    }
    public SteamGameItem? SelectedSteamGame { get => selectedSteamGame; set { selectedSteamGame = value; Changed(); if (value != null) SteamAppId = value.AppId.ToString(); } }
    private string steamAchievementSearch = "";
    public string SteamAchievementSearch { get => steamAchievementSearch; set { steamAchievementSearch = value ?? ""; Changed(); Changed(nameof(VisibleSteamAchievements)); } }
    public string[] SteamAchievementFilters { get; } = ["All achievements", "Locked", "Unlocked"];
    public string[] SteamAchievementSortOptions { get; } = ["Default", "Name A–Z", "Name Z–A", "Rarest first", "Most common", "Unlock time", "Status"];
    private string steamAchievementFilter = "All achievements", steamAchievementSort = "Default";
    public string SteamAchievementFilter { get => steamAchievementFilter; set { steamAchievementFilter = value; Changed(); Changed(nameof(VisibleSteamAchievements)); } }
    public string SteamAchievementSort { get => steamAchievementSort; set { steamAchievementSort = value; Changed(); Changed(nameof(VisibleSteamAchievements)); } }
    public IEnumerable<SteamAchievementItem> VisibleSteamAchievements {
        get {
            var rows = steamAchievements.Where(a => (a.Name.Contains(SteamAchievementSearch, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(SteamAchievementSearch, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(SteamAchievementSearch, StringComparison.OrdinalIgnoreCase)) && (SteamAchievementFilter == "All achievements" || a.IsUnlocked == (SteamAchievementFilter == "Unlocked")));
            return SteamAchievementSort switch {
                "Name A–Z" => rows.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
                "Name Z–A" => rows.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase),
                "Rarest first" => rows.OrderBy(a => a.GlobalPercent ?? double.MaxValue),
                "Most common" => rows.OrderByDescending(a => a.GlobalPercent ?? -1),
                "Unlock time" => rows.OrderByDescending(a => a.UnlockTime),
                "Status" => rows.OrderBy(a => a.IsUnlocked).ThenBy(a => a.Name),
                _ => rows
            };
        }
    }
    private SteamAchievementItem? selectedSteamAchievement;
    public SteamAchievementItem? SelectedSteamAchievement { get => selectedSteamAchievement; set { selectedSteamAchievement = value; Changed(); } }
    public SteamAchievementItem[] SteamAchievements { get => steamAchievements; private set { steamAchievements = value; Changed(); Changed(nameof(VisibleSteamAchievements)); } }
    public string SteamAppId { get => steamAppId; set { steamAppId = value ?? ""; Changed(); } }
    public string SteamStatus { get => steamStatus; private set { steamStatus = value; Changed(); Changed(nameof(HomeSteamSummary)); } }
    public void RefreshSteamProfile()
    {
        try { ApplySteamProfile(steam.GetProfileSummary()); }
        catch { SteamProfileName = "Steam unavailable"; SteamLevel = "Unknown"; SteamAvatarUrl = null; }
    }
    public async Task RefreshSteamProfileOnStartupAsync()
    {
        try { ApplySteamProfile(await Task.Run(steam.GetProfileSummary, lifetime.Token)); }
        catch { /* Keep the placeholder; clicking Home retries. */ }
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
        try {
            var owner = steam.GetLocalSteamId();
            if (owner != steamLibraryOwner) { steamGames = []; SelectedSteamGame = null; SteamAchievements = []; loadedSteamAppId = 0; RefreshSteamPresentation(); }
            steamGames = (await steam.GetAccountLibraryAsync(AchievementLabs.Core.MirrorStore.ReadKey(), lifetime.Token)).ToArray();
            if (steam.GetLocalSteamId() != owner) throw new InvalidOperationException("Steam account changed during refresh. Refresh again.");
            steamLibraryOwner = owner;
            RefreshSteamPresentation();
            SteamStatus = "Reading achievement totals and resolving missing titles…";
            var missing = steamGames.Where(g => string.IsNullOrWhiteSpace(g.Name) || g.Name == $"App {g.AppId}" || g.Name == g.AppId.ToString()).ToArray();
            try {
                var names = await steam.RefreshSteamStoreNamesAsync(missing.Select(g => g.AppId), lifetime.Token);
                foreach (var game in missing) if (names.TryGetValue(game.AppId, out var name)) game.Name = name;
            } catch (OperationCanceledException) { throw; } catch { /* Keep cached names offline. */ }
            foreach (var game in steamGames) {
                lifetime.Token.ThrowIfCancellationRequested();
                try {
                    var progress = await Task.Run(() => steam.GetCachedLibraryProgress(game.AppId, owner), lifetime.Token);
                    game.ApplyProgress(progress.Earned, progress.Total, progress.Known);
                } catch (OperationCanceledException) { throw; } catch { game.ApplyProgress(0, 0, false); }
            }
            RefreshSteamPresentation();
            if (steam.GetLocalSteamId() != owner) { steamGames = []; RefreshSteamPresentation(); throw new InvalidOperationException("Steam account changed during refresh. Refresh again."); }
            SteamStatus = SteamLibrarySummary;
        }
        catch (Exception error) { SteamStatus = error is OperationCanceledException ? "Steam library refresh cancelled." : error.Message; }
        finally { Busy(false); }
    }
    public async Task FillSteamTotalsAsync()
    {
        if (!CanInteract) return;
        Busy(true);
        try
        {
            foreach (var game in steamGames.Where(g => !g.ProgressKnown))
            {
                lifetime.Token.ThrowIfCancellationRequested();
                SteamStatus = $"Reading achievement totals: {game.DisplayName}";
                try
                {
                    var progress = await steam.GetCommunityAchievementProgressAsync(game.AppId, lifetime.Token);
                    game.ApplyProgress(progress.Unlocked, progress.Total);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Private or unavailable profiles stay unknown. */ }
            }
            RefreshSteamPresentation();
            SteamStatus = SteamLibrarySummary;
        }
        catch (OperationCanceledException) { SteamStatus = "Steam totals scan cancelled."; }
        finally { Busy(false); }
    }
    private long loadedSteamAppId;
    private static readonly string lastSteamAppPath = AchievementLabs.Core.AchievementLabsPaths.LocalFile("steam-last-app.txt");
    private bool lastSteamAppRestored;

    /// <summary>App ID of the last game whose Steam achievements loaded, kept across restarts.</summary>
    private static long ReadLastSteamApp()
    {
        try { return File.Exists(lastSteamAppPath) && long.TryParse(File.ReadAllText(lastSteamAppPath).Trim(), out var id) && id > 0 ? id : 0; }
        catch { return 0; }
    }

    private static void SaveLastSteamApp(long id)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(lastSteamAppPath)!); File.WriteAllText(lastSteamAppPath, id.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch { /* Convenience only. */ }
    }

    public Task OpenSteamAchievementsAsync() { Navigate("SteamAchievements"); return Task.CompletedTask; }

    /// <summary>
    /// The first time the Steam achievements page is shown after a restart (by any route), reload the last game.
    /// Waits for startup work to finish instead of skipping, since loading is refused while the app is busy.
    /// </summary>
    private async Task RestoreLastSteamGameAsync()
    {
        if (lastSteamAppRestored || SteamAchievements.Length > 0) return;
        lastSteamAppRestored = true;
        var last = ReadLastSteamApp();
        if (last == 0) return;
        var text = last.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (SteamAppId.Length == 0) SteamAppId = text;
        if (SteamAppId != text) return;
        for (var i = 0; i < 120 && !CanInteract && !lifetime.IsCancellationRequested; i++) await Task.Delay(250);
        if (page == "SteamAchievements" && SteamAchievements.Length == 0 && SteamAppId == text) await LoadSteamAchievementsAsync();
    }
    public async Task LoadSteamAchievementsAsync() => await SteamOperationAsync(async id =>
    {
        SelectedSteamAchievement = null; SteamAchievements = []; loadedSteamAppId = 0;
        var loaded = await steam.LoadAccountAchievementsAsync(id, AchievementLabs.Core.MirrorStore.ReadKey(), lifetime.Token);
        SteamAchievements = loaded.ToArray(); loadedSteamAppId = id;
        try { await steam.LoadGlobalPercentagesAsync(id, SteamAchievements, lifetime.Token); }
        catch (OperationCanceledException) { throw; } catch { /* Rarity remains explicitly unavailable offline. */ }
        var game = steamGames.FirstOrDefault(g => g.AppId == id);
        if (game == null)
        {
            game = new SteamGameItem { AppId = id, ImageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg" };
            steamGames = [.. steamGames, game];
        }
        if (string.IsNullOrWhiteSpace(game.Name) || game.Name == $"App {id}" || game.Name == id.ToString())
        {
            try
            {
                var names = await steam.RefreshSteamStoreNamesAsync([id], lifetime.Token);
                if (names.TryGetValue(id, out var name)) game.Name = name;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* App ID remains usable offline. */ }
        }
        game?.ApplyProgress(loaded.Count(a => a.IsUnlocked), loaded.Count);
        RefreshSteamPresentation();
        Changed(nameof(VisibleSteamAchievements));
        SteamStatus = $"Loaded {loaded.Count} achievements for app {id}."; Navigate("SteamAchievements");
        if (loaded.Count > 0) { SaveLastSteamApp(id); lastSteamAppRestored = true; }
    });
    public async Task UnlockSteamSelectionAsync(IEnumerable<SteamAchievementItem> selected)
    {
        if (!CanInteract || !long.TryParse(SteamAppId, out var id) || id != loadedSteamAppId) return;
        var rows = selected.Where(a => SteamAchievements.Contains(a) && !a.IsUnlocked).Distinct().ToArray();
        if (rows.Length == 0) { SteamStatus = "Select locked achievements to unlock."; return; }
        RevertSteam();
        foreach (var row in rows) row.DesiredUnlocked = true;
        await StoreSteamAchievementsAsync();
        var result = SteamStatus;
        if (!steamStoreSucceeded) return;
        await LoadSteamAchievementsAsync();
        SteamStatus = result;
    }
    private bool steamStoreSucceeded;
    public async Task StoreSteamAchievementsAsync() => await SteamOperationAsync(async id =>
    {
        steamStoreSucceeded = false;
        if (id != loadedSteamAppId) { SteamStatus = "Load achievements for this app ID before saving changes."; return; }
        var snapshot = SteamAchievements.ToArray();
        var unlocking = snapshot.Where(a => a.IsModified && a.DesiredUnlocked).Select(a => (a.Id, a.Name)).ToArray();
        foreach (var (apiName, name) in unlocking) MirrorExpectSteam(id, apiName, name);
        int count;
        try { count = await Task.Run(() => steam.StoreAchievementsAsync(id, snapshot, lifetime.Token), lifetime.Token); }
        catch { foreach (var (apiName, _) in unlocking) MirrorCancelSteam(id, apiName); throw; }
        SteamStatus = $"Stored {count} changes. Reload to verify.";
        steamStoreSucceeded = true;
        foreach (var (apiName, name) in unlocking)
            if (!await MirrorSteamAppUnlockAsync(id, apiName, name, lifetime.Token)) { SteamStatus = $"Stored {count} changes, but Achievement Mirror couldn't copy {name} to Xbox. See Achievement Mirror."; break; }
    });
    public void InvertSteam() { if (CanInteract) foreach (var item in SteamAchievements) item.DesiredUnlocked = !item.DesiredUnlocked; }
    public void RevertSteam() { if (CanInteract) foreach (var item in SteamAchievements) item.DesiredUnlocked = item.IsUnlocked; }
    public async Task ResetSteamStatsAsync() => await SteamOperationAsync(async id => { await steam.ResetAsync(id, false, lifetime.Token); SteamStatus = "Stats reset request completed."; });
    public async Task RefreshSteamMetadataAsync()
    {
        if (!CanInteract) return; Busy(true);
        try {
            try { await steam.RefreshSteamAppNameCacheAsync(lifetime.Token); }
            catch (OperationCanceledException) { throw; }
            catch { /* Store lookups can still work if the bulk app list is unavailable. */ }
            await steam.RefreshSteamStoreNamesAsync(steamGames.Select(g => g.AppId), lifetime.Token);
            SteamStatus = steam.GetSteamMetadataStatus();
        }
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
        catch (Exception error) { SteamStatus = "Steam operation failed. Ensure Steam is running and the selected app is available. (" + error.Message + ")"; }
        finally { Busy(false); }
    }
}
