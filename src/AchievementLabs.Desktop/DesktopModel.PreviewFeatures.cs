using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private async Task RefreshConnectedMetadataAsync(string owner, int generation, XboxApiClient attachedClient)
    {
        if (OfflineChecks) return;
        try
        {
            do { await Task.Delay(100, lifetime.Token); } while (busy);
            if (session?.Xuid != owner || sessionGeneration != generation || client != attachedClient) return;
            await RefreshSharedTotalsAsync(lifetime.Token);
            if (session?.Xuid != owner || sessionGeneration != generation || client != attachedClient) return;
            games = games.Select(WithSharedTotal).ToArray(); RefreshLibraryPresentation();
            await ImportCompletionExportsAsync(false);
        }
        catch (OperationCanceledException) { }
        catch { }
    }
    private async Task RefreshSelectedEventSupportAsync()
    {
        if (!liveAchievements || !eventBased || SelectedGame is not { } game || definitions.Count == 0) return;
        var response = new AchievementsResponse { achievements = definitions.Values.ToList() };
        await LoadActionMetadataAsync(response, game.Id, selectionVersion);
        Refresh();
    }
    public async Task OpenSessionAchievementsAsync()
    {
        if (busy || session == null || !uint.TryParse(PresenceTitleId, out var id) || id == 0) return;
        var title = id.ToString();
        var game = Games.FirstOrDefault(g => g.Id == title) ?? (SelectedGame?.Id == title ? SelectedGame : null)
            ?? new Game(title, SpoofTitleName == "No title selected" ? title : SpoofTitleName, "Xbox", 0, 0, 0, false);
        await SelectGameCoreAsync(game, false);
    }
}
