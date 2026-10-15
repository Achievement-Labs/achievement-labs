using System.Text.Json;
using Avalonia.Media;
using AchievementLabs.Core;
using AchievementLabs.MultiSelect;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    public sealed record CompletionProgress(CompletionMarkers.Row[] Rows, DateTimeOffset CheckedAt, bool MythicEarned = false);
    public sealed record CompletionDocument(string Account, Dictionary<string, CompletionProgress> Titles);
    private readonly Dictionary<string, CompletionProgress> completionProgress = new();
    private CancellationTokenSource? completionScanCancellation;
    private bool completionImportRunning, completionDirty;
    private string completionStatus = "Completion indicators use verified groups and account-specific progress.";
    public string CompletionStatus { get => completionStatus; private set { completionStatus = value; Changed(); } }
    public bool CompletionScanRunning => completionScanCancellation != null;
    public bool CanScanCompletion => session != null && !busy && !CompletionScanRunning && !completionImportRunning;
    public bool CanImportCompletion => session != null && !CompletionScanRunning && !completionImportRunning;
    private static bool OfflineChecks => AppContext.TryGetSwitch("AchievementLabs.OfflineChecks", out var checks) && checks;
    private string? CompletionAccount => session == null ? null : AchievementExport.AccountKey(session.Xuid);
    private string? CompletionPath => CompletionAccount is { } account ? AchievementLabsPaths.LocalFile("library-completion-" + account + ".json") : null;
    public static string ValidColour(string? text, string fallback) => Color.TryParse(text, out var colour) ? colour.ToString() : fallback;
    private Game WithLibraryPresentation(Game game)
    {
        var title = SharedDlcCatalogue.Current.Find(game.Id, game.Platform);
        completionProgress.TryGetValue(game.Id, out var cached);
        var result = CompletionMarkers.Evaluate(title, cached?.Rows ?? [], game.Total, game.Completed, game.ProgressKnown);
        bool mythic = result.BaseComplete || cached?.MythicEarned == true;
        if (title?.IsHub == true) mythic = false;
        if (mythic && cached?.MythicEarned != true && CompletionAccount != null)
        {
            completionProgress[game.Id] = (cached ?? new([], DateTimeOffset.MinValue)) with { MythicEarned = true };
            completionDirty = true;
        }
        return game with {
            BaseComplete = mythic, AddOnsComplete = result.AddOnsComplete, FullyComplete = result.FullyComplete,
            MythicVisible = ShowMythicIcon && mythic,
            MythicColour = ValidColour(MythicColour, "#70C98A"),
            CompletionBackground = HighlightCompletedDlcs && result.FullyComplete ? ValidColour(CompletedDlcColour, "#183A27") : "Transparent"
        };
    }
    private void RefreshLibraryPresentation()
    {
        if (SelectedGame != null) SelectedGame = WithLibraryPresentation(SelectedGame);
        if (games.Length == 0) return;
        var selectedId = SelectedLibraryGame?.Id;
        games = games.Select(WithLibraryPresentation).ToArray(); SaveCompletionCache();
        Changed(nameof(VisibleGames));
        SelectedLibraryGame = games.FirstOrDefault(g => g.Id == selectedId);
    }
    private void LoadCompletionCache()
    {
        completionProgress.Clear(); completionDirty = false;
        if (OfflineChecks) return;
        try
        {
            if (CompletionPath is not { } path || !File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<CompletionDocument>(File.ReadAllText(path));
            if (saved?.Account != CompletionAccount || saved.Titles == null) return;
            foreach (var item in saved.Titles)
                if (uint.TryParse(item.Key, out _) && item.Value?.Rows != null)
                    completionProgress[item.Key] = item.Value;
        }
        catch { CompletionStatus = "Saved completion progress could not be read; scan or import again."; }
    }
    private void SaveCompletionCache()
    {
        if (!completionDirty || OfflineChecks || CompletionPath is not { } path || CompletionAccount is not { } account) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new CompletionDocument(account, completionProgress)));
            File.Move(path + ".tmp", path, true); completionDirty = false;
        }
        catch { CompletionStatus = "Completion indicators updated, but their cache could not be saved."; }
    }
    public static bool CanReplaceAchievementProgress(Game game, Achievement[] rows)
        => rows.Length > 0 && rows.All(a => a.ProgressKnown) &&
            (!UsesLegacyEndpoint(game) || !game.ProgressKnown || rows.Count(a => a.Unlocked) >= game.Completed);
    private void RememberCompletion(Game game, CompletionMarkers.Row[] rows)
    {
        var old = completionProgress.GetValueOrDefault(game.Id);
        var result = CompletionMarkers.Evaluate(SharedDlcCatalogue.Current.Find(game.Id, game.Platform),
            rows, rows.Length, rows.Count(r => r.Known && r.Unlocked), rows.All(r => r.Known));
        completionProgress[game.Id] = new(rows, DateTimeOffset.UtcNow, old?.MythicEarned == true || result.BaseComplete);
        completionDirty = true; SaveCompletionCache();
    }
    private static string? ExportBaseFolder()
    {
        try { var path = AchievementLabsPaths.LocalFile("export-folder.txt"); return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
    public async Task ImportCompletionExportsAsync(bool showResult = true)
    {
        if (!CanImportCompletion || session == null || OfflineChecks) return;
        string xuid = session.Xuid, account = AchievementExport.AccountKey(xuid);
        string? folder = ExportBaseFolder();
        if (folder == null) { if (showResult) CompletionStatus = "No saved bulk export found. Export achievements first, or scan missing progress."; return; }
        string titles = Path.Combine(folder, "AchievementLabs-export-" + account, "titles");
        if (!Directory.Exists(titles)) { if (showResult) CompletionStatus = "No bulk export found for the connected account."; return; }
        completionImportRunning = true; Changed(nameof(CanImportCompletion)); Changed(nameof(CanScanCompletion));
        try
        {
            var imported = await Task.Run(() =>
            {
                var entries = new List<AchievementExport.Saved>();
                foreach (var path in Directory.EnumerateFiles(titles, "*.json"))
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var saved = JsonSerializer.Deserialize<AchievementExport.Saved>(File.ReadAllText(path));
                        if (saved is { Version: 1, Game: not null, Rows: not null } && saved.Account == account &&
                            saved.Game.Id == Path.GetFileNameWithoutExtension(path) &&
                            saved.Rows.All(r => r != null && !string.IsNullOrWhiteSpace(r.Id)) &&
                            saved.Rows.Select(r => r.Id).Distinct().Count() == saved.Rows.Length)
                            entries.Add(saved);
                    }
                    catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
                }
                return entries;
            }, lifetime.Token);
            if (session?.Xuid != xuid) return;
            int importedCount = 0;
            foreach (var saved in imported)
            {
                var current = completionProgress.GetValueOrDefault(saved.Game.Id);
                if (current != null && current.CheckedAt >= saved.ScannedAt) continue;
                bool LegacyPlaceholder(AchievementExport.Row r) => AchievementExport.Legacy(saved.Game) &&
                    r.Status == "Unlocked" && !r.LegacyProgressVerified && (!DateTimeOffset.TryParse(r.UnlockedAt,out var when) || when.Year < 2005);
                var progress = saved.Rows.Select(r => new CompletionMarkers.Row(r.Id, r.Name,
                    r.Status == "Unlocked" && !LegacyPlaceholder(r),
                    (r.Status is "Unlocked" or "Locked") && !LegacyPlaceholder(r))).ToArray();
                var title = SharedDlcCatalogue.Current.Find(saved.Game.Id, saved.Game.Platform.Replace(", ", " / "));
                var result = CompletionMarkers.Evaluate(title, progress, progress.Length,
                    progress.Count(r => r.Known && r.Unlocked), progress.All(r => r.Known));
                completionProgress[saved.Game.Id] = new(progress, saved.ScannedAt, current?.MythicEarned == true || result.BaseComplete);
                importedCount++; completionDirty = true;
            }
            RefreshLibraryPresentation(); SaveCompletionCache();
            CompletionStatus = $"Imported completion progress for {importedCount} titles from this account's saved export.";
        }
        catch (OperationCanceledException) { }
        catch { CompletionStatus = "Could not import completion progress. Existing indicators were retained."; }
        finally { completionImportRunning = false; Changed(nameof(CanImportCompletion)); Changed(nameof(CanScanCompletion)); }
    }
    public void StopCompletionScan() => completionScanCancellation?.Cancel();
    public async Task ScanMissingCompletionAsync()
    {
        if (!CanScanCompletion || session == null) return;
        var connected = session;
        var pending = Games.Where(g => SharedDlcCatalogue.Current.Find(g.Id, g.Platform) is { } title &&
            !(g.ProgressKnown && g.Total == title.Packs.Sum(p => p.Achievements.Length) && g.Completed == g.Total) &&
            (!completionProgress.TryGetValue(g.Id, out var saved) || saved.Rows.Length != g.Total ||
                saved.Rows.Count(r => r.Known && r.Unlocked) != g.Completed)).ToArray();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        completionScanCancellation = cancel; NotifyCompletionScan();
        int done = 0, failed = 0;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", connected.Authorization);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-GB");
            var engine = new AchievementExport(http, connected.Xuid);
            foreach (var game in pending)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (session?.Xuid != connected.Xuid) return;
                CompletionStatus = $"Completion scan {done}/{pending.Length} · {game.Name} · 1 title at a time";
                try
                {
                    var rows = await engine.Achievements(new(game.Id, game.Name, game.Platform.Replace(" / ", ", "), game.Score), cancel.Token);
                    if (session?.Xuid != connected.Xuid) return;
                    if (rows.Length == 0) { failed++; }
                    else
                    {
                        RememberCompletion(game, rows.Select(r => new CompletionMarkers.Row(r.Id, r.Name,
                            r.Status == "Unlocked", r.Status is "Unlocked" or "Locked")).ToArray());
                        RefreshLibraryPresentation();
                    }
                }
                catch (UnauthorizedAccessException) { CompletionStatus = "Xbox denied the read-only completion scan. Reconnect before retrying."; return; }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                catch { failed++; }
                done++; await Task.Delay(1000, cancel.Token);
            }
            CompletionStatus = $"Completion scan finished: {done - failed} updated, {failed} unavailable.";
        }
        catch (OperationCanceledException) { CompletionStatus = $"Completion scan stopped; {done - failed} results saved."; }
        finally { completionScanCancellation = null; SaveCompletionCache(); NotifyCompletionScan(); }
    }
    private void NotifyCompletionScan()
    {
        Changed(nameof(CompletionScanRunning)); Changed(nameof(CanScanCompletion)); Changed(nameof(CanImportCompletion)); Changed(nameof(CanDisconnect));
    }
}
