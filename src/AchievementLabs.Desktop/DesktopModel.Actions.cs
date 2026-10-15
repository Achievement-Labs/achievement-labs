using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;
using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed record ActionReport(string Achievement, string Result);
public sealed partial class DesktopModel
{
    private Dictionary<string, OneCoreAchievementResponse> definitions = [];
    private HashSet<string> mappedIds = [];
    private bool liveAchievements, eventBased, eventTitleSupported, titleTesting, unlockAllEnabled;
    private static readonly (HashSet<string> Testing, HashSet<string> ComingSoon) localCatalog = LoadLocalCatalog();
    private static (HashSet<string>, HashSet<string>) LoadLocalCatalog()
    {
        using var stream = typeof(DesktopModel).Assembly.GetManifestResourceStream("AchievementLabs.PublicCatalog.json");
        if (stream == null) return ([], []);
        using var catalog = System.Text.Json.JsonDocument.Parse(stream);
        var rows = catalog.RootElement.EnumerateArray().ToArray();
        return (
            rows.Where(x => x.GetProperty("status").GetString() == "testing").Select(x => x.GetProperty("titleId").GetString()!).ToHashSet(),
            rows.Where(x => x.GetProperty("status").GetString() == "coming-soon").Select(x => x.GetProperty("titleId").GetString()!).ToHashSet()
        );
    }
    private bool titleComingSoon;
    public bool TitleComingSoon => titleComingSoon;
    public bool TitleTesting => titleTesting;
    public bool TitleNotSupported => eventBased && !eventTitleSupported && !titleComingSoon;
    private string actionSummary = "Load achievements from your account to enable actions.";
    public ObservableCollection<ActionReport> ActionReports { get; } = [];
    public bool UnlockAllEnabled { get => unlockAllEnabled; set { unlockAllEnabled = value; Changed(); NotifyActions(); } }
    public string ActionSummary { get => actionSummary; private set { actionSummary = value; Changed(); } }
    public bool CanUnlockSelected => AchievementLabs.MultiSelect.BatchPicker.CanUnlockSelected(this);
    public bool CanUnlockAll => CanQuery && liveAchievements && !eventBased && UnlockAllEnabled && SelectedGame != null && !UsesLegacyEndpoint(SelectedGame) && achievements.Any(a => !a.Unlocked);
    private void NotifyActions() { Changed(nameof(TitleComingSoon)); Changed(nameof(TitleTesting)); Changed(nameof(TitleNotSupported)); Changed(nameof(CanUnlockSelected)); Changed(nameof(CanUnlockAll)); }
    private void ResetActions() { titleComingSoon = false; titleTesting = false; eventBased = false; eventTitleSupported = false; liveAchievements = false; definitions = []; mappedIds = []; ActionSummary = "Load achievements from your account to enable actions."; NotifyActions(); }
    private async Task LoadActionMetadataAsync(AchievementsResponse response, string titleId, int version)
    {
        var nextDefinitions = response.achievements.ToDictionary(a => a.id);
        var nextEventBased = Workflows.AutoUnlockerViewModel.UsesEvents(response.achievements);
        var nextMappedIds = new HashSet<string>();
        string? error = null;
        var nextSupported = false;
        var nextTesting = nextEventBased && localCatalog.Testing.Contains(titleId);
        var nextComingSoon = nextEventBased && localCatalog.ComingSoon.Contains(titleId);
        if (nextEventBased)
        {
            try
            {
                var catalog = await eventCatalog.GetCatalogAsync(lifetime.Token);
                nextTesting = catalog.TestingTitleIds?.Contains(titleId) == true;
                nextSupported = catalog.TitleIds.Contains(titleId) || nextTesting;
                nextComingSoon = catalog.ComingSoonTitleIds?.Contains(titleId) == true;
                if (nextSupported)
                {
                    nextMappedIds = (await eventCatalog.GetTitleAsync(titleId, lifetime.Token)).AchievementIds.ToHashSet();
                }
                else error = nextComingSoon ? "Coming Soon — this title is still being tested." : "This event-based title is not currently supported.";
            }
            catch (OperationCanceledException) { throw; }
            catch { error = nextComingSoon ? "Coming Soon — this title is still being tested." : "Event template service unavailable. Try again shortly."; }
        }
        if (version != selectionVersion || lifetime.IsCancellationRequested) return;
        titleComingSoon = nextComingSoon; titleTesting = nextTesting; definitions = nextDefinitions; eventBased = nextEventBased; eventTitleSupported = nextSupported; mappedIds = nextMappedIds; liveAchievements = true;
        ActionSummary = error ?? (eventBased ? $"Event-based title · {mappedIds.Count} mapped achievement IDs" : "Title-based achievements · ready for individual actions");
        NotifyActions();
    }    public async Task UnlockSelectedAsync()
    {
        if (!CanUnlockSelected || SelectedAchievement == null) return;
        await SubmitAchievementsAsync([SelectedAchievement.Id]);
    }
    public async Task UnlockAllAsync()
    {
        if (!CanUnlockAll) return;
        await SubmitAchievementsAsync(achievements.Where(a => !a.Unlocked).Select(a => a.Id).ToArray());
    }
    private async Task SubmitAchievementsAsync(string[] ids)
    {
        if (client == null || session == null || SelectedGame == null) return;
        var game = SelectedGame;
        var source = definitions; var useEvents = eventBased; var directory = EventsDirectory;
        Busy(true); ActionReports.Clear(); ActionSummary = "Submitting achievement actions…";
        try
        {
            await EnsureAuthorizationFreshAsync(lifetime.Token);
            if (useEvents)
            {
                try { await EnsureEventTokenAsync(lifetime.Token); }
                catch (InvalidOperationException error)
                {
                    ActionSummary = error.Message;
                    if (EventTokenRequired != null) await EventTokenRequired();
                    return;
                }
            }
            await requests.WaitAsync(lifetime.Token);
            try
            {
                if (client == null || session == null) return;
                var api = client; var account = session;
                foreach (var id in ids)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var definition = source[id];
                    try
                    {
                        var transportDetails = new List<string>();
                        if (useEvents)
                        {
                            var response = await eventCatalog.GetPayloadsAsync(game.Id, id, account.Xuid, lifetime.Token);
                            foreach (var payload in response.Payloads)
                            {
                                lifetime.Token.ThrowIfCancellationRequested();
                                var result = await Task.Run(() => api.UnlockEventBasedAchievementWithDiagnostics(account.EventsToken, payload), lifetime.Token);
                                if (result.StatusCode is 401 or 403)
                                {
                                    MarkEventTokenRejected();
                                    ActionSummary = $"Xbox rejected the event token (HTTP {result.StatusCode} {result.ReasonPhrase}). Paste a current token in Settings.";
                                    if (EventTokenRequired != null) await EventTokenRequired();
                                    return;
                                }
                                transportDetails.Add($"HTTP {result.StatusCode} · {EventCollectorResponse.Read(result.ResponseBody).Summary}");
                                if (result.StatusCode < 200 || result.StatusCode >= 300)
                                {
                                    var detail = SummarizeEventError(result.ResponseBody);
                                    throw new HttpRequestException($"HTTP {result.StatusCode} {result.ReasonPhrase}{detail}");
                                }
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(definition.serviceConfigId) || definition.serviceConfigId == Guid.Empty.ToString()) throw new InvalidDataException("Missing service configuration.");
                            await Task.Run(() => api.UnlockTitleBasedAchievementAsync(definition.serviceConfigId, game.Id, account.Xuid, id, FakeSignature), lifetime.Token);
                        }
                        ActionReports.Add(new(definition.name, useEvents
                            ? string.Join(" | ", transportDetails) + " · Event submitted; achievement unlock is not confirmed. Refresh to verify progress."
                            : "Request accepted; refresh to verify progress"));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { ActionReports.Add(new(definition.name, "Failed · " + ex.Message)); }
                }
            }
            finally { requests.Release(); }
            ActionSummary = $"Finished {ActionReports.Count} actions. Refresh achievements to verify account progress.";
        }
        catch (OperationCanceledException) { ActionSummary = "Stopped before the next request. Requests already sent may have completed."; }
        catch (InvalidOperationException error) { ActionSummary = error.Message; }
        finally { Busy(false); }
    }
    private static string SummarizeEventError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return "";
        var singleLine = string.Join(" ", responseBody.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (singleLine.Length > 240) singleLine = singleLine[..240] + "…";
        return " · " + singleLine;
    }
}
