using AchievementLabs.Desktop.Platforms;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private CancellationTokenSource? platformAchievementCancellation;
    private string platformAchievementMessage = "";
    internal Func<ConnectedPlatformAccount, PlatformGameRow, CancellationToken, Task<PlatformAchievementsResult>>? PlatformAchievementReaderOverride;
    public bool PlatformAchievementsLoading => platformAchievementCancellation != null;
    public string PlatformAchievementStatus => PlatformAchievementsLoading ? "Loading achievement definitions and player progress…" : platformAchievementMessage.Length > 0 ? platformAchievementMessage : CurrentPlatformGame?.AchievementStatus is { Length: > 0 } status ? status : "Open a game to retrieve its achievements.";
    public string PlatformAchievementEmptyMessage => CurrentPlatformGame?.Total > 0 ? "No achievements match the current filters." : PlatformAchievementStatus;
    private bool OfflinePlatformChecks => AppContext.TryGetSwitch("AchievementLabs.OfflineChecks", out var offline) && offline;
    private void QueuePlatformAchievementLoad()
    {
        platformAchievementCancellation?.Cancel();
        platformAchievementMessage = "";
        Changed(nameof(PlatformAchievementStatus));
        if (PlatformAchievementsVisible && (!OfflinePlatformChecks || PlatformAchievementReaderOverride != null)) _ = LoadPlatformAchievementsAsync();
    }
    public async Task LoadPlatformAchievementsAsync(bool force = false)
    {
        if (!IsPlatform || CurrentPlatformGame is not { } game || (OfflinePlatformChecks && PlatformAchievementReaderOverride == null)) return;
        if (!force && game.AchievementsLoaded && game.CatalogComplete) { platformAchievementMessage = ""; Changed(nameof(PlatformAchievementStatus)); return; }
        platformAchievementCancellation?.Cancel();
        var platform = game.Platform;
        var authenticated = connectedPlatformAccounts.TryGetValue(platform, out var account) && account.AccountId == game.AccountId;
        if (!authenticated && platform != "Ubisoft")
        {
            platformAchievementMessage = "Connect this game's account from its Library, then refresh achievements.";
            Changed(nameof(PlatformAchievementStatus)); return;
        }
        account = authenticated ? account! : new ConnectedPlatformAccount(platform, game.AccountId, "", "");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(90));
        platformAchievementCancellation = operation;
        platformAchievementMessage = "";
        NotifyPlatformAchievementLoading();
        try
        {
            using var client = new AccountLibraryClient();
            PlatformAchievementsResult result;
            try { result = PlatformAchievementReaderOverride != null ? await PlatformAchievementReaderOverride(account, game, operation.Token) : await client.ReadAchievementsAsync(account, game, operation.Token); }
            catch (HttpRequestException error) when (PlatformAchievementReaderOverride == null && error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                var renewed = account.SessionSource.Length > 0 ? await LauncherSession.AttachAsync(platform, operation.Token) : await client.RefreshAsync(account with { ExpiresAt = DateTimeOffset.MinValue }, operation.Token);
                if (renewed.AccountId != account.AccountId) throw new InvalidOperationException("The launcher account changed. Reattach the intended account from Library.");
                if (!connectedPlatformAccounts.TryGetValue(platform, out var active) || active.AccountId != account.AccountId) return;
                connectedPlatformAccounts[platform] = renewed;
                result = await client.ReadAchievementsAsync(renewed, game, operation.Token);
            }
            operation.Token.ThrowIfCancellationRequested();
            if (!platformCatalogs.TryGetValue(platform, out var catalog)) return;
            connectedPlatformAccounts.TryGetValue(platform, out var currentAccount);
            if (authenticated && currentAccount?.AccountId != account.AccountId) return;
            if (!authenticated && currentAccount != null) return;
            var original = catalog.Games.FirstOrDefault(g => g.Id == game.Id && g.AccountId == account.AccountId);
            if (original == null) return;
            var updated = original with { Rows = result.Rows, CatalogComplete = result.Complete, AchievementsLoaded = true, AchievementStatus = result.Status };
            var refreshed = catalog with { Games = catalog.Games.Select(g => g.Id == game.Id ? updated : g).ToArray() };
            platformCatalogs[platform] = refreshed;
            if (!OfflinePlatformChecks && currentAccount != null)
            {
                try { AccountLibraryCache.Save(currentAccount, refreshed); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { platformAchievementMessage = result.Status + " Could not save the offline cache."; }
            }
            if (ActivePlatform == platform && CurrentPlatformGame?.Id == game.Id)
            {
                currentPlatformGame = updated; currentPlatformAchievement = updated.Achievements.FirstOrDefault();
                Changed(nameof(PlatformLibraryGames)); Changed(nameof(PlatformAchievementGames)); Changed(nameof(CurrentPlatformGame)); Changed(nameof(CurrentPlatformGameId)); Changed(nameof(CurrentPlatformAchievement));
                Changed(nameof(PlatformLibraryGames)); Changed(nameof(PlatformLibrarySummary)); RefreshPlatformRows();
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(platformAchievementCancellation, operation)) platformAchievementMessage = "Achievement loading was cancelled or timed out. Refresh achievements to retry.";
        }
        catch (Exception error)
        {
            if (ReferenceEquals(platformAchievementCancellation, operation)) platformAchievementMessage = "Achievements could not be loaded: " + error.Message + " Use Refresh achievements to retry.";
        }
        finally
        {
            if (ReferenceEquals(platformAchievementCancellation, operation))
            {
                platformAchievementCancellation = null;
                NotifyPlatformAchievementLoading();
            }
        }
    }
    private void NotifyPlatformAchievementLoading()
    {
        Changed(nameof(PlatformAchievementsLoading)); Changed(nameof(PlatformAchievementStatus)); Changed(nameof(PlatformAchievementEmpty)); Changed(nameof(PlatformAchievementEmptyMessage)); Changed(nameof(PlatformLibraryRefreshing));
    }
}
