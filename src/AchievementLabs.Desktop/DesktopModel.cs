using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public record Achievement(string Id, string Name, string Description, int Score, bool Unlocked, bool ProgressKnown = true, bool ScoreKnown = true, string? ImageUrl = null)
{
    public double? GlobalUnlockPercentage { get; init; }
    public string GlobalUnlockLabel => GlobalUnlockPercentage is { } value ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—";
    public string Status => !ProgressKnown ? "Not available" : Unlocked ? "Unlocked" : "Locked";
    public string Symbol => Unlocked ? "✓" : "◇";
    public string ScoreLabel => ScoreKnown ? $"{Score} G" : "Not available";
    public string CompactScoreLabel => ScoreKnown ? ScoreLabel : "—";
    public string BadgeForeground => Unlocked ? "#8BCBB0" : "#B9BEC8";
    public string BadgeBackground => Unlocked ? "#253B34" : "#2A303B";
}
public record Game(string Id, string Name, string Platform, int Completed, int Total, int Score, bool ProgressKnown = true, string? ImageUrl = null, DateTime? LastPlayed = null, bool NoDefinitionsReturned = false)
{
    public bool BaseComplete { get; init; }
    public bool AddOnsComplete { get; init; }
    public bool FullyComplete { get; init; }
    public bool MythicVisible { get; init; }
    public string MythicColour { get; init; } = "#70C98A";
    public string CompletionBackground { get; init; } = "Transparent";
    public string PlatformLabel => AchievementLabs.MultiSelect.GfwlTitles.Label(Id, Platform);
    public string CompletionTooltip => FullyComplete ? "100% completed: all achievements, including DLC and title updates" : "Base game completed (local indicator from verified achievement progress)";
    public string ShortName => Name;
    public string Monogram => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0]));
    public string Color => "#70C98A";
    public string Cover => "#171A1C";
    public string Description => $"Title ID {Id}";
    public double Percent => Total == 0 ? 0 : 100.0 * Completed / Total;
    public string ProgressLabel => NoDefinitionsReturned && Total == 0 ? "No Xbox achievements returned" : !ProgressKnown ? $"{Total} achievement definitions" : Total > 0 ? $"{Completed} / {Total} achievements" : Completed > 0 ? $"{Completed} unlocked · total unavailable" : "Achievement total unavailable";
    public string ScoreLabel => ProgressKnown ? $"{Score:N0} G earned" : "Account progress not included";
}
public sealed partial class DesktopModel : Observable, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private ConnectedXboxSession? session;
    private XboxApiClient? client;
    private Achievement[] achievements = [];
    private Game[] games = [];
    private Game? selectedGame;
    private Game? selectedLibraryGame;
    private Achievement? selectedAchievement;
    private string page = "Home", search = "", librarySearch = "", filter = "All", notice = "Open the Xbox PC app and Achievement Labs will attach to its signed-in account.";
    private string profileName = "Not connected", xboxGamerscore = "Unknown";
    private string? profileImageUrl;
    private bool busy;
    private int selectionVersion;
    private readonly Stack<string> pageHistory = [];
    private IEnumerable<Game> CombinedXboxGames => Games.Concat(Xbox360Games
        .Where(local => !Games.Any(game => game.Id == Convert.ToUInt32(local.TitleId, 16).ToString()))
        .Select(local => new Game(Convert.ToUInt32(local.TitleId, 16).ToString(), local.Name,
            "Xbox360", local.UnlockedCount, local.AchievementCount, local.GamerscoreUnlocked)));
    public Game[] Games
    {
        get => games;
        private set
        {
            var selectedId = SelectedLibraryGame?.Id;
            games = value.Select(g => WithLibraryPresentation(WithSharedTotal(WithKnownTotal(g, knownLibraryTotals)))).ToArray();
            SaveCompletionCache();
            var visible = VisibleGames.ToArray();
            Changed(); Changed(nameof(CanExportVerifiedTotals)); Changed(nameof(VisibleGames)); Changed(nameof(GameCount)); Changed(nameof(HomeXboxSummary));
            SelectedLibraryGame = visible.FirstOrDefault(g => g.Id == selectedId) ?? visible.FirstOrDefault();
        }
    }
    public IEnumerable<Game> VisibleGames
    {
        get
        {
            var filtered = CombinedXboxGames.Where(g => MatchesPlatform(g, PlatformFilter) && (g.Name.Contains(LibrarySearch, StringComparison.OrdinalIgnoreCase) || g.Id == LibrarySearch.Trim()));
            return LibrarySort switch
            {
                "Z-A" => filtered.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
                "Last Played" => filtered.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id),
                _ => filtered.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id)
            };
        }
    }
    public string LibrarySearch { get => librarySearch; set { librarySearch = value ?? ""; Changed(); Changed(nameof(VisibleGames)); } }
    public string GameCount => CombinedXboxGames.Count().ToString();
    private Achievement[] FilteredAchievementRows() => achievements.Where(a =>
        (filter == "All" || a.ProgressKnown && (filter == "Unlocked") == a.Unlocked) &&
        (a.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Id == Search)).ToArray();
    public Achievement[] VisibleAchievements => (Achievement[])AchievementLabs.MultiSelect.AchievementView.Transform(FilteredAchievementRows(), this);
    public Achievement[] BatchAchievements => (Achievement[])AchievementLabs.MultiSelect.AchievementView.Transform(FilteredAchievementRows(), this, false);
    public Game? SelectedGame { get => selectedGame; private set { selectedGame = value == null ? null : WithLibraryPresentation(value); Changed(); } }
    public Game? SelectedLibraryGame { get => selectedLibraryGame; set { selectedLibraryGame = value; Changed(); Changed(nameof(HasLibrarySelection)); } }
    public bool HasLibrarySelection => SelectedLibraryGame != null;
    public Achievement? SelectedAchievement { get => selectedAchievement; set { selectedAchievement = value; Changed(); Changed(nameof(HasSelection)); NotifyActions(); } }
    public string Search { get => search; set { search = value ?? ""; Changed(); Refresh(); } }
    public string PageTitle => page switch { "About" => "About Achievement Labs", "Home" => "Home", "Tools" => "Tools and information", "Queues" => "Xbox auto unlock queues", "SteamQueue" => "Steam Auto Unlocker", "Legacy" => "Legacy tools", "Gfwl" => "Games for Windows LIVE", "Xbox360Profiles" => "Xbox 360 profiles", "Xbox360ProfileEditor" => "Xbox 360 achievement editor", "SteamLibrary" => "Steam library", "SteamAchievements" => "Steam achievements", "Spoofer" => "Xbox title spoofer", "Epic" => "Epic Games", "Ubisoft" => "Ubisoft Connect", "Stats" => "Stats editor", "Settings" => "Settings", "Diagnostics" => "Event catalog", "Library" => "Xbox library", "Mirror" => "Achievement Mirror", _ => "Xbox achievements" };
    public bool IsHome => page == "Home";
    public bool IsSpoofer => page == "Spoofer";
    public bool IsAchievements => page == "Achievements";
    public bool IsLibrary => page == "Library";
    public bool NoResults => VisibleAchievements.Length == 0;
    public bool HasSelection => SelectedAchievement != null;
    public bool CanConnect => !QueueActive && !busy && session == null;
    public bool CanInteract => !QueueActive && !busy;
    public string ProfileName { get => PrivacyMode ? "Private profile" : profileName; private set { profileName = value; Changed(); Changed(nameof(HomeXboxSummary)); } }
    public string? ProfileImageUrl { get => PrivacyMode ? null : profileImageUrl; private set { profileImageUrl = value; Changed(); } }
    public string XboxGamerscore { get => PrivacyMode ? "Hidden" : xboxGamerscore; private set { xboxGamerscore = value; Changed(); Changed(nameof(HomeXboxSummary)); } }
    private string connectionMethod = "";
    public string ConnectionLabel => session == null ? "Xbox not connected" : $"Xbox connected · {connectionMethod}";
    public string ResultLabel => $"{VisibleAchievements.Length} shown · {achievements.Count(a => a.Unlocked)} unlocked / {achievements.Count(a => !a.Unlocked)} locked · {filter}";
    public string Notice { get => notice; set { notice = value; Changed(); } }
    public string HomeXboxSummary => session == null ? "Not connected" : $"{ProfileName} · {XboxGamerscore} G · {Games.Length} titles";
    public string HomeSteamSummary => $"{SteamProfileName} · Level {SteamLevel} · {SteamStatus}";
    public bool CanGoBack => pageHistory.Count > 0;
    public void Navigate(string destination) {
        if (destination == "Legacy") return;
        if (destination == page) return;
        if (page == "Achievements" && destination != "Achievements" && autoPresenceTask != null) StopPresence();
        pageHistory.Push(page); SetPage(destination);
    }
    public void GoBack()
    {
        if (pageHistory.Count == 0) return;
        if (page == "Achievements" && autoPresenceTask != null) StopPresence();
        SetPage(pageHistory.Pop());
    }
    private void SetPage(string destination) { page = destination; Changed(nameof(CanGoBack)); Changed(nameof(IsAbout)); Changed(nameof(PageTitle)); Changed(nameof(IsHome)); Changed(nameof(IsSpoofer)); Changed(nameof(IsAchievements)); Changed(nameof(IsLibrary)); Changed(nameof(IsSettings)); Changed(nameof(IsDiagnostics)); Changed(nameof(IsStats)); Changed(nameof(IsPlatform)); Changed(nameof(IsEpic)); Changed(nameof(IsUbisoft)); Changed(nameof(IsSteam)); Changed(nameof(IsSteamLibrary)); Changed(nameof(IsSteamAchievements)); Changed(nameof(IsLegacy)); Changed(nameof(IsGfwl)); Changed(nameof(IsXbox360Profiles)); Changed(nameof(IsXbox360ProfileEditor)); Changed(nameof(IsQueues)); Changed(nameof(IsSteamQueue)); Changed(nameof(IsTools)); Changed(nameof(IsMirror)); }
    private void ApplyXboxProfile(ProfileUser? user)
    {
        ProfileName = user?.Settings.FirstOrDefault(s => s.Id == "Gamertag")?.Value ?? "Xbox account";
        ProfileImageUrl = user?.Settings.FirstOrDefault(s => s.Id == "GameDisplayPicRaw")?.Value;
        XboxGamerscore = user?.Settings.FirstOrDefault(s => s.Id == "Gamerscore")?.Value ?? "Unknown";
    }
    public void Filter(string value) { filter = value; Refresh(); }
    private void Refresh()
    {
        var matches = VisibleAchievements;
        SelectedAchievement = matches.Contains(SelectedAchievement) ? SelectedAchievement : matches.FirstOrDefault();
        Changed(nameof(VisibleAchievements)); Changed(nameof(NoResults)); Changed(nameof(ResultLabel)); Changed(nameof(CanExport));
    }
    private void Busy(bool value) { busy = value; Changed(nameof(CanAcquireAccountEventToken)); Changed(nameof(CanConnect)); Changed(nameof(CanInteract)); Changed(nameof(CanDisconnect)); Changed(nameof(CanQuery)); Changed(nameof(CanLookupSpoofTitle)); Changed(nameof(CanFillTotals)); Changed(nameof(CanStartPresence)); NotifyActions(); }
    public Task ConnectAsync() => AttachXboxPcAppAsync();

    private async Task ActivateXboxSessionAsync(ConnectedXboxSession connected, XboxApiClient candidate, string method)
    {
        var result = await Task.Run(async () => (Profile: await candidate.GetBasicProfileAsync(), Titles: await candidate.GetGamesListAsync(connected.Xuid)), lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        if (result.Titles == null) throw new InvalidDataException("Xbox title history was not returned.");
        client = candidate;
        session = connected;
        await RefreshSharedTotalsAsync(lifetime.Token);
        LoadLibraryTotals();
        LoadCompletionCache();
        connectionMethod = method;
        BeginTokenMaintenance();
        ApplyXboxProfile(result.Profile?.ProfileUsers.FirstOrDefault());
        Games = result.Titles.Titles.Where(t => t.TitleId != null).Select(t => new Game(t.TitleId!, t.Name ?? t.TitleId!, ClassifyXboxPlatform(t.TitleId!, t.Devices), t.Achievement?.CurrentAchievements ?? 0, t.Achievement?.TotalAchievements ?? 0, t.Achievement?.CurrentGamerscore ?? 0, true, ResolveTitleImage(t.DisplayImage, t.Images), t.TitleHistory?.LastTimePlayed)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Changed(nameof(ConnectionLabel)); Changed(nameof(CanDisconnect)); Changed(nameof(HomeXboxSummary));
        await ImportCompletionExportsAsync(false);
    }
    public async Task SelectGameAsync(Game game)
    {
        if (HasDevice(game, "GFWL") || IsKnownGfwlTitle(game.Id)) { OpenGfwlTitle(game); return; }
        if (busy) return;
        var version = ++selectionVersion;
        ResetActions();
        if (busy) return;
        ResetGamePlaytime();
        SelectedGame = game; achievements = []; Search = ""; Filter("All"); Navigate("Achievements");
        if (client == null || session == null) { GamePlaytimeText = "Xbox-recorded playtime: Unavailable"; return; }
        if (AutoSpoof && !UsesLegacyEndpoint(game))
        {
            if (autoPresenceTask != null) { StopPresence(); await autoPresenceTask; }
            PresenceTitleId = game.Id; autoPresenceTask = StartPresenceAsync();
        }
        Notice = $"Loading {game.Name}…";
        await requests.WaitAsync(lifetime.Token);
        try
        {
            if (version != selectionVersion || lifetime.IsCancellationRequested || client == null || session == null) return;
            var api = client; var xuid = session.Xuid;
            Achievement[] loaded;
            var resolvedGame = game;
            if (HasDevice(game, "Win8") || HasDevice(game, "Windows8") || IsKnownWindows8Title(game.Id))
            {
                using var http = new HttpClient();
                loaded = await Windows8Achievements.ReadAsync(http, session.Authorization, xuid, ResolveWindows8TitleId(game.Id), lifetime.Token);
                resolvedGame = game with { Platform = "Windows8" };
            }
            else if (UsesLegacyEndpoint(game))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", session.Authorization);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-GB");
                var reader = new AchievementLabs.MultiSelect.AchievementExport(http, xuid);
                var rows = await reader.Achievements(new(game.Id, game.Name, game.Platform.Replace(" / ", ", "), game.Score), lifetime.Token);
                loaded = rows.Select(a => new Achievement(a.Id, a.Name, a.Description,
                    int.TryParse(a.Score, out var score) ? score : 0, a.Status == "Unlocked", a.Status is "Unlocked" or "Locked", ImageUrl: a.ImageUrl) { GlobalUnlockPercentage = a.GlobalUnlockPercentage }).ToArray();
            }
            else
            {
                AchievementsResponse? response = null;
                Exception? primaryError = null;
                try { response = await Task.Run(() => api.GetAchievementsForTitleAsync(xuid, game.Id), lifetime.Token); }
                catch (Exception error) when (error is not OperationCanceledException) { primaryError = error; }
                loaded = response == null ? [] : Map(response);
                if (loaded.Length == 0)
                {
                    try
                    {
                        using var http = new HttpClient();
                        var windows8 = await Windows8Achievements.ReadAsync(http, session.Authorization, xuid, ResolveWindows8TitleId(game.Id), lifetime.Token);
                        if (windows8.Length > 0)
                        {
                            loaded = windows8;
                            resolvedGame = game with { Platform = "Windows8" };
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
                    {
                        if (primaryError != null) throw primaryError;
                    }
                }
                if (version != selectionVersion || lifetime.IsCancellationRequested) return;
                if (loaded.Length == 0 && primaryError != null) throw primaryError;
                if (response != null && resolvedGame == game) await LoadActionMetadataAsync(response, game.Id, version);
            }
            if (version != selectionVersion || lifetime.IsCancellationRequested) return;
            bool progressComplete = CanReplaceAchievementProgress(game, loaded);
            if (!progressComplete) loaded = loaded.Select(a => a.Unlocked ? a : a with { ProgressKnown = false }).ToArray();
            achievements = loaded;
            if (progressComplete) RememberCompletion(resolvedGame, loaded.Select(a => new AchievementLabs.MultiSelect.CompletionMarkers.Row(a.Id, a.Name, a.Unlocked, a.ProgressKnown)).ToArray());
            var updated = WithLibraryPresentation(progressComplete
                ? resolvedGame with { Completed = loaded.Count(a => a.Unlocked), Total = loaded.Length, Score = loaded.Where(a => a.Unlocked).Sum(a => a.Score), ProgressKnown = loaded.All(a => a.ProgressKnown) }
                : resolvedGame with { Total = loaded.Length > 0 ? loaded.Length : game.Total });
            RememberLibraryTotal(updated.Id, updated.Total);
            SelectedGame = updated;
            Games = Games.Select(g => g.Id == updated.Id ? updated : g).ToArray();
            Refresh(); Notice = progressComplete ? $"Loaded {loaded.Length} achievements for {game.Name}." : "Xbox returned incomplete achievement progress. Known library counts were retained; unconfirmed rows show Not available. Refresh to retry.";
            _ = LoadGamePlaytimeAsync(updated, version);
        }
        catch (OperationCanceledException) { }
        catch { if (version == selectionVersion) Notice = "Achievements could not be loaded. Select the title again to retry."; }
        finally { requests.Release(); }
    }
    public async Task OpenExportAsync(string path)
    {
        if (busy) return;
        var version = ++selectionVersion;
        ResetActions();
        var json = Newtonsoft.Json.Linq.JToken.Parse(await File.ReadAllTextAsync(path, lifetime.Token));
        var response = json is Newtonsoft.Json.Linq.JArray ? new AchievementsResponse { achievements = json.ToObject<List<OneCoreAchievementResponse>>() ?? [] } : json.ToObject<AchievementsResponse>() ?? throw new InvalidDataException();
        if (response.achievements.Count == 0) throw new InvalidDataException("No achievements in this export.");
        if (version != selectionVersion || lifetime.IsCancellationRequested) return;
        achievements = Map(response);
        var title = response.achievements[0].titleAssociations.FirstOrDefault();
        SelectedGame = new Game(title?.id ?? Path.GetFileNameWithoutExtension(path), title?.name ?? Path.GetFileNameWithoutExtension(path), string.Join(" / ", response.achievements.SelectMany(a => a.platforms).Distinct(StringComparer.OrdinalIgnoreCase)), achievements.Count(a => a.Unlocked), achievements.Length, achievements.Where(a => a.Unlocked).Sum(a => a.Score), achievements.All(a => a.ProgressKnown));
        GamePlaytimeText = "Xbox-recorded playtime: Unavailable for local export";
        Search = ""; Filter("All"); Navigate("Achievements"); Notice = $"Opened {achievements.Length} achievements from a local export.";
    }
    private static Achievement[] Map(AchievementsResponse response) => response.achievements.Where(a => !string.Equals(a.achievementType, "Challenge", StringComparison.OrdinalIgnoreCase)).Select(a => new Achievement(a.id, a.name, (a.progressState == "Achieved" ? a.description : a.lockedDescription) ?? a.description ?? "", int.TryParse(a.rewards.FirstOrDefault(r => r.type == "Gamerscore")?.value, out var score) ? score : 0, a.progressState == "Achieved", a.progressState != "Null", a.rewards.Any(r => r.type == "Gamerscore"), a.mediaAssets.FirstOrDefault(m => m.type == "Icon")?.url ?? a.mediaAssets.FirstOrDefault()?.url) { GlobalUnlockPercentage = AchievementLabs.MultiSelect.AchievementRarity.Parse(a.rarity?.currentPercentage) }).ToArray();
    private static string? ResolveTitleImage(string? displayImage, object? images)
    {
        if (!string.IsNullOrWhiteSpace(displayImage)) return displayImage;
        if (images is not JToken token) token = images == null ? null : JToken.FromObject(images);
        return token?.SelectTokens("$..*").OfType<JValue>()
            .Select(value => value.Type == JTokenType.String ? value.Value<string>() : null)
            .FirstOrDefault(value => value != null && (value.StartsWith("http", StringComparison.OrdinalIgnoreCase) || value.StartsWith("//", StringComparison.Ordinal)));
    }
    public void Dispose() { mirror?.Dispose(); completionScanCancellation?.Cancel(); Windows8.Dispose(); gfwlSession?.Dispose(); eventCatalog.Dispose(); apiServer?.Dispose(); StopQueues(); steam.StopSpoofSession(); browserLogin?.Dispose(); lifetime.Cancel(); updater.Dispose(); /* Requests may still own the client; dispose after completion. */ _ = DisposeClientAsync(); }
    private async Task DisposeClientAsync() { await requests.WaitAsync(); client?.Dispose(); requests.Release(); }
}
