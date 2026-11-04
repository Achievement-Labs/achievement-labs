using System.Net;
using System.Text.Json;
using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly SharedUnlockMethods sharedUnlockMethods = SharedUnlockMethods.Bundled();
    private readonly SemaphoreSlim sharedMetadataRefresh = new(1, 1);
    private Dictionary<string, bool>? libraryUnlockMethods;
    private Dictionary<string, LibraryEventSupport>? libraryEventSupport;
    private CancellationTokenSource? librarySupportCheck;
    private readonly HashSet<string> supportAttempted = new();
    private bool supportUserStopped, autoCheckLibrarySupport = LoadSupportAuto();
    private DateTimeOffset supportRetryAt;
    private string supportCheckStatus = "Support checks read achievement metadata and remember the result locally.";
    private Dictionary<string, bool> LibraryUnlockMethods
    {
        get
        {
            if (libraryUnlockMethods != null) return libraryUnlockMethods;
            libraryUnlockMethods = new();
            if (!OfflineChecks)
                try
                {
                    var path = AchievementLabsPaths.LocalFile("library-unlock-methods.json");
                    if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024)
                        libraryUnlockMethods = SharedUnlockMethods.Parse("{\"schemaVersion\":1,\"methods\":" + File.ReadAllText(path) + "}");
                }
                catch { }
            return libraryUnlockMethods;
        }
    }
    public bool AutoCheckLibrarySupport
    {
        get => autoCheckLibrarySupport;
        set
        {
            autoCheckLibrarySupport = value; Changed();
            if (!OfflineChecks)
                try { var path = AchievementLabsPaths.LocalFile("library-support-auto.txt"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value ? "true" : "false"); }
                catch { SupportCheckStatus = "Support check preference could not be saved."; }
            if (!value) StopLibrarySupportCheck();
            else { supportUserStopped = false; QueueLibrarySupportCheck(); }
        }
    }
    public bool SupportCheckRunning => librarySupportCheck != null;
    public string SupportCheckStatus { get => supportCheckStatus; private set { supportCheckStatus = value; Changed(); } }
    public async Task ExportConfirmedSupportAsync(string path)
    {
        await File.WriteAllTextAsync(path, SharedUnlockMethods.Export(LibraryUnlockMethods), lifetime.Token);
        Notice = $"Exported {LibraryUnlockMethods.Count} confirmed unlock methods.";
    }
    private LibrarySupportBadge SupportFor(Game game)
    {
        if (UsesLegacyEndpoint(game)) return LibrarySupportBadge.Resolve(true, null, false, false, false, 0, game.Total);
        try
        {
            libraryEventSupport ??= eventCatalog.GetLibrarySupport();
            libraryEventSupport.TryGetValue(game.Id, out var entry);
            bool? method = LibraryUnlockMethods.TryGetValue(game.Id, out var local) ? local : entry != null ? null : sharedUnlockMethods.Find(game.Id);
            var badge = LibrarySupportBadge.Resolve(false, method, entry != null, entry?.Available == true, entry?.FullySupported == true, entry?.Mapped ?? 0, game.Total);
            return entry?.Note is { } note ? badge with { Detail = badge.Detail + " " + note } : badge;
        }
        catch { return LibrarySupportBadge.Unknown; }
    }
    private void CacheLibraryUnlockMethod(string title, bool events)
    {
        LibraryUnlockMethods[title] = events;
        if (OfflineChecks) return;
        try
        {
            var path = AchievementLabsPaths.LocalFile("library-unlock-methods.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(LibraryUnlockMethods));
            File.Move(path + ".tmp", path, true);
        }
        catch { SupportCheckStatus = "Support results updated, but their local cache could not be saved."; }
    }
    private void RememberLibraryUnlockMethod(string title, bool events) { CacheLibraryUnlockMethod(title, events); RefreshLibraryPresentation(); }
    private void RefreshLibrarySupport() { libraryEventSupport = null; RefreshLibraryPresentation(); }
    private static bool LoadSupportAuto()
    {
        if (OfflineChecks) return false;
        try { var path = AchievementLabsPaths.LocalFile("library-support-auto.txt"); return !File.Exists(path) || File.ReadAllText(path) != "false"; }
        catch { return true; }
    }
    private void QueueLibrarySupportCheck()
    {
        if (AutoCheckLibrarySupport && !supportUserStopped && !OfflineChecks && DateTimeOffset.UtcNow >= supportRetryAt)
            _ = CheckLibrarySupportAsync(true);
    }
    public void StopLibrarySupportCheck() { supportUserStopped = true; librarySupportCheck?.Cancel(); }
    public async Task CheckLibrarySupportAsync(bool automatic = false)
    {
        if (OfflineChecks || session == null || SupportCheckRunning || (automatic && (!AutoCheckLibrarySupport || supportUserStopped))) return;
        if (DateTimeOffset.UtcNow < supportRetryAt) { SupportCheckStatus = "Xbox asked for a pause. Try the support check later."; return; }
        if (!automatic) { supportUserStopped = false; supportAttempted.Clear(); }
        var owner = session.Xuid; var generation = sessionGeneration;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        librarySupportCheck = stop; Changed(nameof(SupportCheckRunning));
        int checkedCount = 0, identified = 0;
        try
        {
            while (busy && !QueueActive) { SupportCheckStatus = "Waiting for the current operation before checking support…"; await Task.Delay(1000, stop.Token); }
            if (QueueActive || eventRefreshRunning) { SupportCheckStatus = "Support check paused while other work is active."; return; }
            var candidates = Games.Where(g => !UsesLegacyEndpoint(g) && !LibraryUnlockMethods.ContainsKey(g.Id) && !supportAttempted.Contains(g.Id) && SupportFor(g).Kind == "unknown").ToArray();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
            foreach (var game in candidates)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (session?.Xuid != owner || sessionGeneration != generation) throw new OperationCanceledException();
                if (QueueActive || busy || eventRefreshRunning) { SupportCheckStatus = "Support check paused for the current operation."; return; }
                SupportCheckStatus = $"Checking game support · {checkedCount}/{candidates.Length} · {identified} identified";
                await EnsureAuthorizationFreshAsync(stop.Token);
                await requests.WaitAsync(stop.Token);
                bool? method;
                try
                {
                    if (session?.Xuid != owner || sessionGeneration != generation) throw new OperationCanceledException();
                    method = await LibrarySupportDiscovery.ReadAsync(http, session.Authorization, owner, game.Id, stop.Token);
                }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) { method = null; }
                catch (Exception error) when (error is InvalidDataException or Newtonsoft.Json.JsonException or ArgumentException or System.Text.DecoderFallbackException) { method = null; }
                finally { requests.Release(); }
                stop.Token.ThrowIfCancellationRequested();
                if (session?.Xuid != owner || sessionGeneration != generation) throw new OperationCanceledException();
                supportAttempted.Add(game.Id); checkedCount++;
                if (method is { } events) { CacheLibraryUnlockMethod(game.Id, events); identified++; }
                if (checkedCount % 5 == 0) RefreshLibraryPresentation();
                if (checkedCount < candidates.Length) await Task.Delay(2000, stop.Token);
            }
            SupportCheckStatus = $"Support check complete · {checkedCount} checked · {identified} identified" + (checkedCount > identified ? " · unavailable responses left unchecked" : "");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || session?.Xuid != owner || sessionGeneration != generation) { SupportCheckStatus = $"Support check stopped · {identified} results saved."; }
        catch (OperationCanceledException) { supportRetryAt = DateTimeOffset.UtcNow.AddMinutes(5); SupportCheckStatus = "Support check timed out; saved results retained."; }
        catch (HttpRequestException error)
        {
            if (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) supportUserStopped = true;
            else supportRetryAt = DateTimeOffset.UtcNow.AddMinutes(10);
            SupportCheckStatus = error.StatusCode == HttpStatusCode.TooManyRequests ? "Xbox rate limit reached; wait before retrying." : "Support check could not finish; saved results retained. Check your Xbox connection.";
        }
        catch { SupportCheckStatus = "Support check could not finish; saved results retained."; }
        finally { librarySupportCheck = null; Changed(nameof(SupportCheckRunning)); RefreshLibraryPresentation(); }
    }
}
