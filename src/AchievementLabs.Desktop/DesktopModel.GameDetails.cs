using AchievementLabs.MultiSelect;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly Dictionary<string, (decimal? Minutes, DateTimeOffset Checked)> gamePlaytimeCache = new();
    private CancellationTokenSource? gamePlaytimeRequest;
    private void ResetGamePlaytime() { gamePlaytimeRequest?.Cancel(); GamePlaytimeText = "Xbox-recorded playtime: Loading…"; }
    private string gamePlaytimeText = "Xbox-recorded playtime: —";
    public string GamePlaytimeText { get => gamePlaytimeText; private set { gamePlaytimeText = value; Changed(); } }
    public bool CanOpenGameSpoofer => SelectedGame != null && session != null && !busy && !PresenceRunning;
    public void OpenGameSpoofer()
    {
        if (!CanOpenGameSpoofer || SelectedGame == null) return;
        PresenceTitleId = SelectedGame.Id; OpenSpoofer();
    }
    private async Task LoadGamePlaytimeAsync(Game game, int version)
    {
        var connected = session;
        if (connected == null) return;
        var key = connected.Xuid + "/" + game.Id;
        bool Current() => version == selectionVersion && ReferenceEquals(connected, session) && SelectedGame?.Id == game.Id;
        if (gamePlaytimeCache.TryGetValue(key, out var cached) && cached.Checked > DateTimeOffset.UtcNow.AddMinutes(-5))
        {
            if (Current()) GamePlaytimeText = "Xbox-recorded playtime: " + (cached.Minutes.HasValue ? XboxPlaytime.Format(cached.Minutes.Value) : "Unavailable");
            return;
        }
        gamePlaytimeRequest?.Cancel();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        gamePlaytimeRequest = cancel;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var minutes = await XboxPlaytime.Read(http, connected.Xuid, game.Id, cancel.Token, connected.Authorization);
            if (!Current()) return;
            gamePlaytimeCache[key] = (minutes, DateTimeOffset.UtcNow);
            GamePlaytimeText = "Xbox-recorded playtime: " + (minutes.HasValue ? XboxPlaytime.Format(minutes.Value) : "Unavailable");
        }
        catch (OperationCanceledException) { if (Current()) GamePlaytimeText = "Xbox-recorded playtime: Unavailable"; }
        catch { if (Current()) { gamePlaytimeCache[key] = (null, DateTimeOffset.UtcNow); GamePlaytimeText = "Xbox-recorded playtime: Unavailable"; } }
        finally { if (ReferenceEquals(gamePlaytimeRequest,cancel)) gamePlaytimeRequest = null; }
    }
}
