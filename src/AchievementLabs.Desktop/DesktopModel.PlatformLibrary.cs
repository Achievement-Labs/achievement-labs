using AchievementLabs.Desktop.Platforms;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly Dictionary<string, PlatformCatalogResult> platformCatalogs = new();
    private readonly Dictionary<string, ConnectedPlatformAccount> connectedPlatformAccounts = new();
    private readonly Dictionary<string, string> selectedPlatformGames = new();
    private CancellationTokenSource? platformRefreshCancellation;
    public bool PlatformLibraryRefreshing => platformRefreshCancellation != null || platformAchievementCancellation != null;
    public void CancelPlatformRefresh() { platformRefreshCancellation?.Cancel(); platformAchievementCancellation?.Cancel(); }
    private string platformView = "Library", platformLibrarySearch = "", platformLibrarySort = "Name A–Z", platformAchievementSearch = "", platformAchievementFilter = "All", platformAchievementSort = "Name A–Z", platformAccount = "";
    private PlatformGameRow? currentPlatformGame;
    private PlatformAchievementRow? currentPlatformAchievement;
    public string ActivePlatform => page switch { "Ubisoft" or "UbisoftAchievements" => "Ubisoft", "EA" or "EAAchievements" => "EA", "GOG" or "GOGAchievements" => "GOG", _ => "Epic" };
    public bool IsEa => page is "EA" or "EAAchievements";
    public bool IsGog => page is "GOG" or "GOGAchievements";
    public bool ShowXboxHeaderActions => !IsPlatform && !IsSteam;
    public bool IsEpicLibrary => IsEpic && PlatformLibraryVisible;
    public bool IsEpicAchievements => IsEpic && PlatformAchievementsVisible;
    public bool IsUbisoftLibrary => IsUbisoft && PlatformLibraryVisible;
    public bool IsUbisoftAchievements => IsUbisoft && PlatformAchievementsVisible;
    public bool IsEaLibrary => IsEa && PlatformLibraryVisible;
    public bool IsEaAchievements => IsEa && PlatformAchievementsVisible;
    public bool IsGogLibrary => IsGog && PlatformLibraryVisible;
    public bool IsGogAchievements => IsGog && PlatformAchievementsVisible;
    public bool HasPlatformTools => ActivePlatform is "Epic" or "Ubisoft";
    public bool PlatformWorkspaceVisible => IsPlatform && platformView != "Tools";
    public bool PlatformLibraryVisible => IsPlatform && platformView == "Library";
    public bool PlatformAchievementsVisible => IsPlatform && platformView == "Achievements";
    public bool PlatformToolsVisible => IsPlatform && platformView == "Tools" && HasPlatformTools;
    public bool PlatformLibraryEmpty => CurrentCatalog.Games.Length == 0 || !PlatformLibraryGames.Any();
    public string PlatformBrand => ActivePlatform switch { "EA" => "EA PLAY / EA APP", "GOG" => "GOG GALAXY", "Epic" => "EPIC GAMES", _ => "UBISOFT CONNECT" };
    private PlatformCatalogResult CurrentCatalog => platformCatalogs.GetValueOrDefault(ActivePlatform) ?? new([], "Sign in to load every title in your account, including games that are not installed.", []);
    public bool PlatformAccountConnected => connectedPlatformAccounts.ContainsKey(ActivePlatform);
    public string ConnectedPlatformAccountLabel => connectedPlatformAccounts.TryGetValue(ActivePlatform, out var account) ? "Signed in as " + account.DisplayName + (account.SessionSource.Length > 0 ? " · attached to " + account.SessionSource : " · browser sign-in") : "Not signed in";
    public string PlatformLibraryStatus => CurrentCatalog.Status;
    public string PlatformLibrarySummary => $"{CurrentCatalog.Games.Length} titles · {CurrentCatalog.Games.Where(g => g.ProgressKnown).Sum(g => g.Earned)} / {CurrentCatalog.Games.Where(g => g.ProgressKnown).Sum(g => g.Total)} known achievements · {CurrentCatalog.Games.Count(g => g.IsComplete)} completed";
    public string[] PlatformAccounts => ["Choose account", .. CurrentCatalog.Accounts];
    public bool HasPlatformAccounts => CurrentCatalog.Accounts.Length > 1;
    public string PlatformAccount { get => platformAccount.Length == 0 ? "Choose account" : platformAccount; set { platformAccount = value == "Choose account" ? "" : value; Changed(); } }
    public string[] PlatformLibrarySortOptions { get; } = ["Name A–Z", "Name Z–A", "Most completed", "Achievements earned", "ID"];
    public string PlatformLibrarySort { get => platformLibrarySort; set { platformLibrarySort = value; Changed(); Changed(nameof(PlatformLibraryGames)); } }
    public string PlatformLibrarySearch { get => platformLibrarySearch; set { platformLibrarySearch = value ?? ""; Changed(); Changed(nameof(PlatformLibraryGames)); Changed(nameof(PlatformLibraryEmpty)); } }
    public IEnumerable<PlatformGameRow> PlatformLibraryGames
    {
        get
        {
            var rows = CurrentCatalog.Games.Where(g => g.Name.Contains(PlatformLibrarySearch, StringComparison.OrdinalIgnoreCase) || g.Id.Contains(PlatformLibrarySearch, StringComparison.OrdinalIgnoreCase));
            return PlatformLibrarySort switch { "Name Z–A" => rows.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase), "Most completed" => rows.OrderByDescending(g => g.ProgressKnown).ThenByDescending(g => g.Progress).ThenBy(g => g.Name), "Achievements earned" => rows.OrderByDescending(g => g.Earned).ThenBy(g => g.Name), "ID" => rows.OrderBy(g => g.Id, StringComparer.OrdinalIgnoreCase), _ => rows.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase) };
        }
    }
    public PlatformGameRow? CurrentPlatformGame { get => currentPlatformGame; set { if (ReferenceEquals(currentPlatformGame, value)) return; if (value == null && currentPlatformGame != null && CurrentCatalog.Games.Any(g => g.Id == currentPlatformGame.Id && g.AccountId == currentPlatformGame.AccountId)) return; currentPlatformGame = value; Changed(nameof(CurrentPlatformGameId)); if (value != null) selectedPlatformGames[value.Platform] = value.Id; currentPlatformAchievement = null; Changed(); Changed(nameof(CurrentPlatformAchievement)); Changed(nameof(PlatformAchievementRows)); Changed(nameof(PlatformAchievementSummary)); Changed(nameof(PlatformAchievementEmpty)); QueuePlatformAchievementLoad(); } }
    public PlatformAchievementRow? CurrentPlatformAchievement { get => currentPlatformAchievement; set { currentPlatformAchievement = value; Changed(); } }
    public string[] PlatformAchievementFilters { get; } = ["All", "Locked", "Unlocked", "Unknown"];
    public string[] PlatformAchievementSortOptions { get; } = ["Name A–Z", "Name Z–A", "Rarest first", "Most common", "Status"];
    public IEnumerable<PlatformGameRow> PlatformAchievementGames => CurrentCatalog.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
    public string CurrentPlatformGameId { get => CurrentPlatformGame?.Id ?? ""; set { if (string.IsNullOrEmpty(value)) return; var selected = CurrentCatalog.Games.FirstOrDefault(g => g.Id == value); if (selected != null) CurrentPlatformGame = selected; } }
    public string PlatformAchievementSearch { get => platformAchievementSearch; set { platformAchievementSearch = value ?? ""; Changed(); RefreshPlatformRows(); } }
    public string PlatformAchievementFilter { get => platformAchievementFilter; set { platformAchievementFilter = value; Changed(); RefreshPlatformRows(); } }
    public string PlatformAchievementSort { get => platformAchievementSort; set { platformAchievementSort = value; Changed(); RefreshPlatformRows(); } }
    public IEnumerable<PlatformAchievementRow> PlatformAchievementRows
    {
        get
        {
            var rows = (CurrentPlatformGame?.Achievements ?? []).Where(a => (a.Name.Contains(PlatformAchievementSearch, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(PlatformAchievementSearch, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(PlatformAchievementSearch, StringComparison.OrdinalIgnoreCase)) && (PlatformAchievementFilter == "All" || a.Status == PlatformAchievementFilter));
            return PlatformAchievementSort switch { "Name Z–A" => rows.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase), "Rarest first" => rows.OrderBy(a => a.GlobalPercent ?? double.MaxValue), "Most common" => rows.OrderByDescending(a => a.GlobalPercent ?? -1), "Status" => rows.OrderBy(a => a.Status).ThenBy(a => a.Name), _ => rows.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase) };
        }
    }
    public bool PlatformAchievementEmpty => !PlatformAchievementsLoading && !PlatformAchievementRows.Any();
    public string PlatformAchievementSummary => $"{PlatformAchievementRows.Count()} shown · {CurrentPlatformGame?.Source ?? "No title selected"} · account: {(string.IsNullOrWhiteSpace(CurrentPlatformGame?.AccountId) ? "not verified" : CurrentPlatformGame.AccountId)}";
    private void RefreshPlatformRows() { Changed(nameof(PlatformAchievementRows)); Changed(nameof(PlatformAchievementEmpty)); Changed(nameof(PlatformAchievementSummary)); Changed(nameof(PlatformAchievementStatus)); Changed(nameof(PlatformAchievementEmptyMessage)); }
    private void SetPlatformPageView() { if (IsPlatform) platformView = page.EndsWith("Achievements", StringComparison.Ordinal) ? "Achievements" : "Library"; }
    internal void NotifyPlatformWorkspace()
    {
        if (IsPlatform && currentPlatformGame?.Platform != ActivePlatform) { currentPlatformGame = CurrentCatalog.Games.FirstOrDefault(g => g.Id == selectedPlatformGames.GetValueOrDefault(ActivePlatform)) ?? CurrentCatalog.Games.FirstOrDefault(); currentPlatformAchievement = null; }
        if (!HasPlatformTools && platformView == "Tools") platformView = "Library";
        Changed(nameof(PlatformLibraryEmpty)); Changed(nameof(PlatformAchievementGames)); Changed(nameof(CurrentPlatformGameId));
        Changed(nameof(ConnectedPlatformAccountLabel)); Changed(nameof(PlatformAccountConnected));
        Changed(nameof(ShowXboxHeaderActions));
        foreach (var name in new[] { nameof(IsEpicLibrary), nameof(IsEpicAchievements), nameof(IsUbisoftLibrary), nameof(IsUbisoftAchievements), nameof(IsEaLibrary), nameof(IsEaAchievements), nameof(IsGogLibrary), nameof(IsGogAchievements) }) Changed(name);
        foreach (var name in new[] { nameof(IsEa), nameof(IsGog), nameof(ActivePlatform), nameof(HasPlatformTools), nameof(PlatformWorkspaceVisible), nameof(PlatformLibraryVisible), nameof(PlatformAchievementsVisible), nameof(PlatformToolsVisible), nameof(PlatformBrand), nameof(PlatformLibraryStatus), nameof(PlatformLibrarySummary), nameof(PlatformLibraryGames), nameof(PlatformAccounts), nameof(HasPlatformAccounts), nameof(CurrentPlatformGame), nameof(CurrentPlatformAchievement), nameof(PlatformAccount) }) Changed(name);
        RefreshPlatformRows();
    }
    public async Task OpenPlatformAsync(string platform, bool achievements = false)
    {
        if (!HasWindowsLocalTools) { Notice = "These platform libraries require Windows."; return; }
        if (!CanInteract || platform is not ("EA" or "GOG" or "Epic" or "Ubisoft")) return;
        platformView = achievements ? "Achievements" : "Library";
        platformAccount = "";
        Navigate(achievements ? platform + "Achievements" : platform);
        if (platformCatalogs.ContainsKey(platform)) { NotifyPlatformWorkspace(); if (achievements) await LoadPlatformAchievementsAsync(); return; }
        await RefreshPlatformLibraryAsync();
        if (achievements) await LoadPlatformAchievementsAsync();
    }
    public async Task RefreshPlatformLibraryAsync(bool reuseVerifiedLauncher = false)
    {
        if (!CanInteract || !IsPlatform) return;
        platformAchievementCancellation?.Cancel();
        var platform = ActivePlatform;
        var previous = CurrentPlatformGame?.Id;
        var account = platformAccount;
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        platformRefreshCancellation = refresh; Changed(nameof(PlatformLibraryRefreshing));
        var token = refresh.Token;
        Busy(true);
        try
        {
            if (!connectedPlatformAccounts.TryGetValue(platform, out var session)) session = ConnectedPlatformAccount.Load(platform);
            if (session == null)
            {
                platformCatalogs[platform] = new([], "Sign in to retrieve this account's full library. Installation records are used only to mark owned titles as installed.", []);
                CurrentPlatformGame = null; NotifyPlatformWorkspace(); return;
            }
            connectedPlatformAccounts[platform] = session;
            using var client = new AccountLibraryClient();
            if (session.SessionSource.Length > 0 && !reuseVerifiedLauncher)
            {
                var launcher = await LauncherSession.AttachAsync(platform, token);
                if (launcher.AccountId != session.AccountId) throw new InvalidOperationException("The launcher is signed into a different account. Use Attach launcher session to switch accounts.");
                session = launcher;
            }
            else if (session.SessionSource.Length == 0) session = await client.RefreshAsync(session, token);
            connectedPlatformAccounts[platform] = session; session.Save();
            var result = await client.ReadAsync(session, token, message => Avalonia.Threading.Dispatcher.UIThread.Post(() => Notice = message));
            var achievementCache = AccountLibraryCache.Load(session);
            if (achievementCache != null)
            {
                var previousRows = achievementCache.Games.Where(g => g.AchievementsLoaded).ToDictionary(g => g.Id);
                result = result with { Games = result.Games.Select(g => previousRows.TryGetValue(g.Id, out var old) ? g with { Rows = old.Rows, CatalogComplete = old.CatalogComplete, AchievementsLoaded = true, AchievementStatus = old.AchievementStatus + " Saved account data; refresh achievements to update." } : g).ToArray() };
            }
            // Local files may enrich an owned title, but cannot add an unowned title to the account library.
            try
            {
                var local = await Task.Run(() => PlatformLibraryReader.Read(platform, session.AccountId, epic, ubisoft, token), token);
                result = AccountLibraryClient.MergeInstallations(result, local.Games, session.AccountId);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Local installation metadata must not block the account's owned library. */ }
            AccountLibraryCache.Save(session, result);
            platformCatalogs[platform] = result;
            if (ActivePlatform != platform) return;
            if (result.Accounts.Length == 1) platformAccount = result.Accounts[0];
            CurrentPlatformGame = result.Games.FirstOrDefault(g => g.Id == previous) ?? result.Games.FirstOrDefault();
            NotifyPlatformWorkspace();
            Notice = $"Loaded {result.Games.Length} {PlatformBrand} titles.";
        }
        catch (OperationCanceledException) { Notice = "Account library refresh cancelled."; }
        catch (Exception error)
        {
            var session = connectedPlatformAccounts.GetValueOrDefault(platform);
            var cached = session == null ? null : AccountLibraryCache.Load(session);
            if (cached != null) { platformCatalogs[platform] = cached; CurrentPlatformGame = cached.Games.FirstOrDefault(g => g.Id == previous) ?? cached.Games.FirstOrDefault(); NotifyPlatformWorkspace(); }
            Notice = "Account library refresh failed: " + error.Message;
        }
        finally { platformRefreshCancellation = null; Changed(nameof(PlatformLibraryRefreshing)); Busy(false); }
    }
    public async Task<bool> ConnectPlatformAccountAsync(CancellationToken cancellationToken)
    {
        if (!CanInteract || !IsPlatform) return false;
        var platform = ActivePlatform;
        var connected = false;
        Busy(true);
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await using var login = new PlatformBrowserLogin(platform);
            var session = await login.SignInAsync(cancel.Token);
            session.Save(); connectedPlatformAccounts[platform] = session;
            connected = true;
            platformCatalogs.Remove(platform);
            if (ActivePlatform == platform) { CurrentPlatformGame = null; NotifyPlatformWorkspace(); }
            Notice = "Signed in as " + session.DisplayName + ".";
        }
        catch (OperationCanceledException) { Notice = "Platform sign-in cancelled."; }
        catch (Exception) { Notice = "Sign-in could not be completed. Retry and finish the login in the sign-in browser."; }
        finally { Busy(false); }
        return connected && ActivePlatform == platform;
    }
    public async Task AttachPlatformSessionAsync()
    {
        if (!CanInteract || !IsPlatform) return;
        var platform = ActivePlatform;
        var connected = false;
        using var attachment = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        attachment.CancelAfter(TimeSpan.FromMinutes(3));
        platformRefreshCancellation = attachment; Changed(nameof(PlatformLibraryRefreshing));
        Busy(true);
        try
        {
            Notice = "Reading and verifying the running " + RunningLauncherSession.Brand(platform) + " session…";
            var session = await LauncherSession.AttachAsync(platform, attachment.Token);
            session.Save(); connectedPlatformAccounts[platform] = session;
            platformCatalogs.Remove(platform);
            if (ActivePlatform == platform) { CurrentPlatformGame = null; NotifyPlatformWorkspace(); }
            connected = true;
            Notice = "Attached to " + session.SessionSource + " as " + session.DisplayName + ".";
        }
        catch (OperationCanceledException) { Notice = "Launcher attachment cancelled."; }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { Notice = error.Message; }
        catch { Notice = "Could not verify the launcher session. Finish signing in to the launcher and retry, or use browser sign-in."; }
        finally { platformRefreshCancellation = null; Changed(nameof(PlatformLibraryRefreshing)); Busy(false); }
        if (connected && ActivePlatform == platform) await RefreshPlatformLibraryAsync(true);
    }
    public void DisconnectPlatformAccount()
    {
        if (!CanInteract || !IsPlatform) return;
        ConnectedPlatformAccount.Forget(ActivePlatform); connectedPlatformAccounts.Remove(ActivePlatform); platformCatalogs.Remove(ActivePlatform);
        CurrentPlatformGame = null; NotifyPlatformWorkspace(); Notice = "Platform account disconnected.";
    }
    public void ShowPlatformLibrary() { platformAchievementCancellation?.Cancel(); platformView = "Library"; Navigate(ActivePlatform); NotifyPlatformWorkspace(); }
    public void ShowPlatformAchievements(PlatformGameRow? game = null)
    {
        if (game != null && game.Platform == ActivePlatform) CurrentPlatformGame = game;
        platformView = "Achievements";
        Navigate(ActivePlatform + "Achievements");
        PlatformAchievementSearch = ""; PlatformAchievementFilter = "All";
        NotifyPlatformWorkspace();
        if (!OfflinePlatformChecks || PlatformAchievementReaderOverride != null) _ = LoadPlatformAchievementsAsync();
    }
    public async Task ShowPlatformToolsAsync()
    {
        if (!HasPlatformTools || !CanInteract) return;
        var platform = ActivePlatform;
        await OpenPlatformToolsAsync(platform);
        if (ActivePlatform == platform) { platformView = "Tools"; NotifyPlatformWorkspace(); }
    }
    public async Task ImportPlatformSnapshotAsync(string path)
    {
        if (!CanInteract) return;
        var platform = ActivePlatform;
        try
        {
            var id = PlatformSnapshot.Import(path, platform, PlatformSnapshot.DefaultFolder);
            await RefreshPlatformLibraryAsync();
            if (ActivePlatform == platform) { CurrentPlatformGame = CurrentCatalog.Games.FirstOrDefault(g => g.Id == id); ShowPlatformAchievements(); }
            Notice = "Snapshot imported locally. No platform achievements were changed.";
        }
        catch (Exception error) { Notice = "Snapshot import failed: " + error.Message; }
    }
}
