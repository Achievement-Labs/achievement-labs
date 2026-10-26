using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;
using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed record ActionReport(string Achievement, string Result);
public sealed partial class DesktopModel
{
    private Dictionary<string, OneCoreAchievementResponse> definitions = [];
    private HashSet<string> mappedIds = [];
    private bool liveAchievements, eventBased, eventTitleSupported, titleTesting, titlePartial, unlockAllEnabled;
    private static readonly (HashSet<string> Testing, HashSet<string> ComingSoon, HashSet<string> Partial) localCatalog = LoadLocalCatalog();
    private static (HashSet<string>, HashSet<string>, HashSet<string>) LoadLocalCatalog()
    {
        using var stream = typeof(DesktopModel).Assembly.GetManifestResourceStream("AchievementLabs.PublicCatalog.json");
        if (stream == null) return ([], [], []);
        using var catalog = System.Text.Json.JsonDocument.Parse(stream);
        var rows = catalog.RootElement.EnumerateArray().ToArray();
        return (
            rows.Where(x => x.GetProperty("status").GetString() == "testing").Select(x => x.GetProperty("titleId").GetString()!).ToHashSet(),
            rows.Where(x => x.GetProperty("status").GetString() == "coming-soon").Select(x => x.GetProperty("titleId").GetString()!).ToHashSet(),
            rows.Where(x => x.GetProperty("status").GetString() == "partial").Select(x => x.GetProperty("titleId").GetString()!).ToHashSet()
        );
    }
    private bool titleComingSoon;
    public bool TitleComingSoon => titleComingSoon;
    public bool TitleTesting => titleTesting && !titlePartial;
    public bool TitlePartial => titlePartial;
    public bool TitleNotSupported => eventBased && !eventTitleSupported && !titleComingSoon;
    private string actionSummary = "Load achievements from your account to enable actions.";
    public ObservableCollection<ActionReport> ActionReports { get; } = [];
    public bool UnlockAllEnabled { get => unlockAllEnabled; set { unlockAllEnabled = value; Changed(); NotifyActions(); } }
    public string ActionSummary { get => actionSummary; private set { actionSummary = value; Changed(); } }
    public bool CanUnlockSelected => AchievementLabs.MultiSelect.BatchPicker.CanUnlockSelected(this);
    public bool CanUnlockAll => CanQuery && liveAchievements && !eventBased && UnlockAllEnabled && SelectedGame != null && !UsesLegacyEndpoint(SelectedGame) && achievements.Any(a => !a.Unlocked);
    private void NotifyActions() { Changed(nameof(TitleComingSoon)); Changed(nameof(TitleTesting)); Changed(nameof(TitlePartial)); Changed(nameof(TitleNotSupported)); Changed(nameof(CanUnlockSelected)); Changed(nameof(CanUnlockAll)); }
    private void ResetActions() { titleComingSoon = false; titleTesting = false; titlePartial = false; eventBased = false; eventTitleSupported = false; liveAchievements = false; definitions = []; mappedIds = []; ActionSummary = "Load achievements from your account to enable actions."; NotifyActions(); }
    private async Task LoadActionMetadataAsync(AchievementsResponse response, string titleId, int version)
    {
        var nextDefinitions = response.achievements.ToDictionary(a => a.id);
        var nextEventBased = Workflows.AutoUnlockerViewModel.UsesEvents(response.achievements);
        var nextMappedIds = new HashSet<string>();
        string? error = null;
        var nextSupported = false;
        var nextTesting = nextEventBased && localCatalog.Testing.Contains(titleId);
        var nextComingSoon = nextEventBased && localCatalog.ComingSoon.Contains(titleId);
        var nextPartial = nextEventBased && localCatalog.Partial.Contains(titleId);
        if (nextEventBased)
        {
            try
            {
                var catalog = await eventCatalog.GetCatalogAsync(lifetime.Token);
                nextPartial = catalog.PartialTitleIds?.Contains(titleId) == true;
                nextTesting = catalog.TestingTitleIds?.Contains(titleId) == true || nextPartial;
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
        titleComingSoon = nextComingSoon; titleTesting = nextTesting; titlePartial = nextPartial; definitions = nextDefinitions; eventBased = nextEventBased; eventTitleSupported = nextSupported; mappedIds = nextMappedIds; liveAchievements = true;
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
        if(client==null || session==null || SelectedGame==null)return;
        var game=SelectedGame;var source=definitions;var useEvents=eventBased;
        Busy(true);ActionReports.Clear();ActionSummary="Sending selected achievements together…";
        try {
            await EnsureAuthorizationFreshAsync(lifetime.Token);
            if(useEvents) { try { await EnsureEventTokenAsync(lifetime.Token); } catch(InvalidOperationException error) { ActionSummary=error.Message;if(EventTokenRequired!=null)await EventTokenRequired();return; } }
            await requests.WaitAsync(lifetime.Token);
            try {
                var account=session;if(account==null)return;
                async Task<(string Name,string Message,bool Rejected)> Send(string id) {
                    var definition=source[id];using var api=new XboxApiClient(account.Authorization);
                    try {
                        if(session?.Xuid!=account.Xuid)throw new OperationCanceledException();
                        if(useEvents) {
                            var payloads=await eventCatalog.GetPayloadsAsync(game.Id,id,account.Xuid,lifetime.Token);
                            foreach(var payload in payloads.Payloads) {
                                lifetime.Token.ThrowIfCancellationRequested();var result=await api.UnlockEventBasedAchievementWithDiagnostics(account.EventsToken,payload);
                                if(result.StatusCode is 401 or 403)return(definition.name,"Authorization rejected; refresh the event token.",true);
                                if(result.StatusCode<200 || result.StatusCode>=300)throw new HttpRequestException($"HTTP {result.StatusCode} {result.ReasonPhrase}");
                            }
                        } else {
                            if(string.IsNullOrWhiteSpace(definition.serviceConfigId) || definition.serviceConfigId==Guid.Empty.ToString())throw new InvalidDataException("Missing service configuration.");
                            await api.UnlockTitleBasedAchievementAsync(definition.serviceConfigId,game.Id,account.Xuid,id,FakeSignature);
                        }
                        return(definition.name,"Request accepted; refresh Xbox progress to verify.",false);
                    } catch(OperationCanceledException) { throw; }
                    catch(Exception error) { return(definition.name,"Request not confirmed · "+error.Message+" · refresh before retrying",false); }
                }
                foreach(var batch in ids.Distinct().Chunk(3)) {
                    var results=await Task.WhenAll(batch.Select(Send));
                    foreach(var result in results)ActionReports.Add(new(result.Name,result.Message));
                    if(results.Any(x=>x.Rejected)) { MarkEventTokenRejected();if(EventTokenRequired!=null)await EventTokenRequired();break; }
                }
            } finally { requests.Release(); }
            ActionSummary=$"Finished {ActionReports.Count} selected actions. Refresh to verify Xbox progress.";
        } catch(OperationCanceledException) { ActionSummary="Stopped. Previously sent requests may have completed; refresh before retrying."; }
        catch(Exception error) { ActionSummary=error.Message; }
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

