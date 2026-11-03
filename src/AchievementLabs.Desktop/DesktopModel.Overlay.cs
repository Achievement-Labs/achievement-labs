using System.Text.Json;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.Mirror;
using AchievementLabs.Models;
using AchievementLabs.MultiSelect;

namespace AchievementLabs.Desktop;

public enum OverlayPlatform { Xbox, Steam }

public sealed record OverlayGame(OverlayPlatform Platform, string Id, string Name);

// In-game overlay glue: the overlay drives the same model as the main window, so unlocks,
// Mirror and presence stay in sync with the rest of the app.
public sealed partial class DesktopModel
{
    public Achievement[] OverlayAchievements => achievements;
    public bool OverlayXboxConnected => session != null;

    public async Task<OverlayGame?> DetectRunningGameAsync(CancellationToken cancellationToken = default)
    {
        var steamApp = ((IMirrorHost)this).SteamRunningAppId;
        if (steamApp > 0)
        {
            var name = steamGames.FirstOrDefault(g => g.AppId == steamApp)?.DisplayName ?? $"App {steamApp}";
            return new OverlayGame(OverlayPlatform.Steam, steamApp.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
        }
        if (session == null) return null;
        try
        {
            await EnsureAuthorizationFreshAsync(cancellationToken);
            var active = session ?? throw new InvalidOperationException();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, XboxPresence.Url(active.Xuid));
            request.Headers.TryAddWithoutValidation("Authorization", active.Authorization);
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "3");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var titleId = ActiveGameTitle(await response.Content.ReadAsStringAsync(cancellationToken));
            if (titleId == null) return null;
            var game = CombinedXboxGames.FirstOrDefault(g => g.Id == titleId);
            return new OverlayGame(OverlayPlatform.Xbox, titleId, game?.Name ?? $"Title {titleId}");
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // Picks the user's active full-screen title (falling back to any active one), skipping whatever the title spoofer is showing.
    private string? ActiveGameTitle(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return null;
        string? fallback = null;
        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) continue;
            foreach (var title in titles.EnumerateArray())
            {
                var id = title.TryGetProperty("id", out var i) ? i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.GetString() : null;
                if (id == null || (PresenceRunning && id == runningPresenceTitleId)) continue;
                var state = title.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state != null && !string.Equals(state, "Active", StringComparison.OrdinalIgnoreCase)) continue;
                var placement = title.TryGetProperty("placement", out var p) ? p.GetString() : null;
                if (string.Equals(placement, "Full", StringComparison.OrdinalIgnoreCase)) return id;
                fallback ??= id;
            }
        }
        return fallback;
    }

    public IEnumerable<OverlayGame> SearchOverlayLibrary(string text, OverlayPlatform? platform = null)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        var xbox = CombinedXboxGames.Where(g => AchievementLabs.Core.TitleSearchText.Matches(g.Name, text) || g.Id == text)
            .Select(g => new OverlayGame(OverlayPlatform.Xbox, g.Id, g.Name));
        var steam = steamGames.Where(g => g.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || g.AppId.ToString() == text)
            .Select(g => new OverlayGame(OverlayPlatform.Steam, g.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), g.DisplayName));
        return (platform == OverlayPlatform.Steam ? [] : xbox).Concat(platform == OverlayPlatform.Xbox ? [] : steam).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Take(40);
    }

    public async Task LoadOverlayGameAsync(OverlayGame game)
    {
        if (game.Platform == OverlayPlatform.Steam)
        {
            SteamAppId = game.Id;
            await LoadSteamAchievementsAsync();
            return;
        }
        var known = CombinedXboxGames.FirstOrDefault(g => g.Id == game.Id) ?? new Game(game.Id, game.Name, "PC", 0, 0, 0);
        await SelectGameAsync(known);
    }

    public bool OverlayXboxGameLoaded(OverlayGame game) => SelectedGame?.Id == game.Id && liveAchievements;
    public bool OverlaySteamGameLoaded(OverlayGame game) => long.TryParse(game.Id, out var id) && id == loadedSteamAppId;

    // Same eligibility rules as the main window's multi-select unlock.
    public bool CanOverlayUnlockXbox(Achievement row) => CanInteract && BatchPicker.IneligibleReason(this, row) == null;

    public async Task UnlockOverlayXboxAsync(IEnumerable<Achievement> rows)
    {
        if (!CanInteract) return;
        var ids = rows.Where(CanOverlayUnlockXbox).Select(r => r.Id).Distinct().ToArray();
        if (ids.Length == 0) { ActionSummary = "Select achievements that can be unlocked."; return; }
        await SubmitAchievementsAsync(ids);
    }

    public Task UnlockOverlaySteamAsync(IEnumerable<SteamAchievementItem> rows) => UnlockSteamSelectionAsync(rows);

    // Presence tile: show the overlay's current game on Xbox or idle it on Steam.
    public async Task StartOverlayPresenceAsync(OverlayGame game)
    {
        if (game.Platform == OverlayPlatform.Steam) { SteamAppId = game.Id; await StartSteamPresenceAsync(); return; }
        if (!CanStartPresence) return;
        PresenceTitleId = game.Id;
        _ = StartPresenceAsync();
    }

    public void StopOverlayPresence(OverlayGame game)
    {
        if (game.Platform == OverlayPlatform.Steam) StopSteamPresence();
        else StopPresence();
    }

    public bool SteamPresenceActive => steam.GetSpoofSessionInfo().IsActive;
}
