using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using AchievementLabs.Core;
using AchievementLabs.Mirror;

namespace AchievementLabs.Desktop.Workflows;

public sealed record MirrorOption(string? Id, string Name)
{
    public static readonly MirrorOption NoEquivalent = new(null, "No equivalent · don't mirror");
    public override string ToString() => Name;
}

/// <summary>One row of the match list: a source achievement and the target it will unlock.</summary>
public sealed partial class MirrorRowViewModel : ObservableObject
{
    private readonly Action changed;
    private bool loading;
    public MirrorRowViewModel(MirrorMatch match, MirrorDirection direction, IReadOnlyList<MirrorOption> options, Action changed)
    {
        Match = match; Direction = direction; Options = options; this.changed = changed;
        loading = true;
        var targetId = match.TargetId(direction);
        SelectedOption = match.State == MirrorMatchState.Ignored ? MirrorOption.NoEquivalent
            : string.IsNullOrEmpty(targetId) ? null : options.FirstOrDefault(o => o.Id == targetId);
        loading = false;
    }
    public MirrorMatch Match { get; }
    public MirrorDirection Direction { get; }
    public IReadOnlyList<MirrorOption> Options { get; }
    public string SourceName => Direction == MirrorDirection.SteamToXbox ? Match.SteamName : Match.XboxName;
    [ObservableProperty] private MirrorOption? selectedOption;
    [ObservableProperty] private string lastMirrored = "";
    public bool NeedsConfirm => Match.State == MirrorMatchState.Suggested;
    public string StatusLabel => LastMirrored.Length > 0 ? LastMirrored : Match.State switch
    {
        MirrorMatchState.Matched => "Matched",
        MirrorMatchState.Confirmed => "Confirmed",
        MirrorMatchState.Suggested => "Confirm pair",
        MirrorMatchState.Ignored => "Not mirrored",
        _ => "Needs review"
    };
    public string StatusBackground => LastMirrored.Length > 0 ? "#1E3348" : Match.State switch
    {
        MirrorMatchState.Matched or MirrorMatchState.Confirmed => "#21352E",
        MirrorMatchState.Ignored => "#2A303B",
        _ => "#40331A"
    };
    public string StatusForeground => LastMirrored.Length > 0 ? "#8EC1F0" : Match.State switch
    {
        MirrorMatchState.Matched or MirrorMatchState.Confirmed => "#91CFB1",
        MirrorMatchState.Ignored => "#B9BEC8",
        _ => "#F0C674"
    };

    partial void OnSelectedOptionChanged(MirrorOption? value)
    {
        if (loading) return;
        if (value == null) { MirrorMatcher.SetTarget(Match, Direction, null); Match.State = MirrorMatchState.Unmatched; }
        else if (value.Id == null) { MirrorMatcher.SetTarget(Match, Direction, null); Match.State = MirrorMatchState.Ignored; }
        else { MirrorMatcher.SetTarget(Match, Direction, new(value.Id, value.Name, "", false, null)); Match.State = MirrorMatchState.Confirmed; }
        Refresh(); changed();
    }
    partial void OnLastMirroredChanged(string value) => Refresh();

    [RelayCommand]
    private void Confirm()
    {
        if (Match.State != MirrorMatchState.Suggested) return;
        Match.State = MirrorMatchState.Confirmed;
        Refresh(); changed();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(NeedsConfirm)); OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBackground)); OnPropertyChanged(nameof(StatusForeground));
    }
}

/// <summary>Achievement Mirror issue found before Auto Unlock starts.</summary>
public sealed record MirrorPreflightIssue(string TitleName, string Message, bool Blocking);

/// <summary>
/// Achievement Mirror: pairs a Steam game with an Xbox title and copies new unlocks from one to the
/// other as they happen. Monitoring lives here, not in the page, so switching tabs never stops it.
/// </summary>
public sealed partial class AchievementMirrorViewModel : ObservableObject, IDisposable
{
    public const string SteamHelpText = "\"Off\" is the cautious setting, so experimenting with Steam unlocks in the app can't accidentally cause a permanent Xbox unlock. Steam unlocks made with other tools, like Steam Achievement Manager, look exactly like game unlocks to the Mirror, so those will still be mirrored while it's running.";
    public const string XboxHelpText = "Turn this on to mirror Xbox unlocks you make in Achievement Labs, including Auto Unlock queues, to Steam as each one happens, so Steam gets the same spacing. \"Off\" skips them. Xbox unlocks made in other apps or in the game itself are always mirrored while the Mirror is running.";
    private static readonly TimeSpan SteamPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan XboxPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] FileTriggerDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10)];

    private readonly IMirrorHost host;
    private readonly ConcurrentDictionary<Guid, MirrorRunner> runners = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> nextPoll = new();
    private readonly ConcurrentDictionary<Guid, string> lastPollError = new();
    private readonly CancellationTokenSource lifetime = new();
    private FileSystemWatcher? statsWatcher;
    private Task? loop;
    private DateTimeOffset nextPresenceCheck;
    private long idleStartedByMirror;
    private string apiKey = "";

    public AchievementMirrorViewModel(IMirrorHost host)
    {
        this.host = host;
        try { foreach (var pair in MirrorStore.LoadPairs()) Pairs.Add(pair); }
        catch { Status = "Saved Mirror pairs could not be read. They were left untouched on disk."; }
        try { apiKey = MirrorStore.ReadKey(); } catch { apiKey = ""; }
        SelectedPair = Pairs.FirstOrDefault();
        if (SelectedPair == null) NewPair();
    }

    public ObservableCollection<MirrorPair> Pairs { get; } = [];
    public ObservableCollection<MirrorRowViewModel> Rows { get; } = [];
    public ObservableCollection<MirrorLogEntry> LogEntries { get; } = [];

    [ObservableProperty] private MirrorPair? selectedPair;
    [ObservableProperty] private string steamAppIdText = "";
    [ObservableProperty] private string xboxTitleIdText = "";
    [ObservableProperty] private string storeLinkText = "";
    [ObservableProperty] private string steamIdText = "";
    [ObservableProperty] private string apiKeyInput = "";
    [ObservableProperty] private bool isSteamToXbox = true;
    [ObservableProperty] private bool mirrorAppUnlocks;
    [ObservableProperty] private string status = "Pair a Steam game with an Xbox title, then scan to match achievements.";
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string sourceAlreadyUnlockedText = "";

    public bool HasApiKey => apiKey.Length > 0;
    public string ApiKeyStatus => HasApiKey ? "Steam Web API key saved (encrypted for your Windows account)." : "No Steam Web API key saved yet.";
    public bool IsXboxToSteam { get => !IsSteamToXbox; set => IsSteamToXbox = !value; }
    public string SourceHeader => IsSteamToXbox ? "Steam" : "Xbox";
    public string TargetHeader => IsSteamToXbox ? "Xbox" : "Steam";
    public string AppUnlocksLabel => IsSteamToXbox ? "Also mirror Steam unlocks I make in Achievement Labs" : "Also mirror Xbox unlocks I make in Achievement Labs";
    public string AppUnlocksDetail => IsSteamToXbox ? "Sent immediately, without waiting for Steam to report them." : "Sent immediately, without waiting for Xbox to report them.";
    public string AppUnlocksHelp => IsSteamToXbox ? SteamHelpText : XboxHelpText;
    public string WarningText => (IsSteamToXbox ? "Only new unlocks are mirrored. Achievements you already earned on Steam won't be copied, since they'd all land on Xbox with the same timestamp. Xbox unlocks can't be undone."
        : "Only new unlocks are mirrored. Achievements you already earned on Xbox won't be copied, since they'd all land on Steam with the same timestamp. While your Xbox status shows you in this game, Achievement Labs idles it on Steam so playtime keeps up.")
        + (SourceAlreadyUnlockedText.Length > 0 ? " " + SourceAlreadyUnlockedText : "");

    private MirrorRunner? SelectedRunner => SelectedPair != null && runners.TryGetValue(SelectedPair.Id, out var r) ? r : null;
    public bool IsMonitoring => SelectedRunner?.Status == MirrorStatus.Monitoring;
    public bool IsStopped => SelectedRunner?.Status == MirrorStatus.Stopped;
    public bool IsRunning => SelectedRunner != null && SelectedRunner.Status != MirrorStatus.Idle;
    public bool CanEditPair => !IsRunning && !IsScanning;
    public string StopReason => SelectedRunner?.StopReason ?? "";
    public int PendingCount => SelectedRunner?.Pending.Count ?? 0;
    public string PendingText => PendingCount == 0 ? "No unlocks are waiting." : $"{PendingCount} unlock(s) detected while stopped are waiting. Sending them keeps the order, but their timestamps will be close together.";
    public string MatchSummary
    {
        get
        {
            var pair = SelectedPair;
            if (pair == null || pair.Matches.Count == 0) return "Not scanned yet";
            var rows = pair.SourceRows.ToArray();
            var mirrorable = rows.Count(r => r.CanMirror);
            var ignored = rows.Count(r => r.State == MirrorMatchState.Ignored);
            var review = rows.Count(r => !r.IsResolved);
            return $"{mirrorable} of {rows.Length} paired" + (ignored > 0 ? $" · {ignored} not mirrored" : "") + (review > 0 ? $" · {review} need review" : " · ready");
        }
    }
    public string StateLabel => SelectedRunner?.Status switch
    {
        MirrorStatus.Monitoring => "Mirroring",
        MirrorStatus.Stopped => "Stopped · needs attention",
        _ => "Not running"
    };
    public string StateBackground => SelectedRunner?.Status switch { MirrorStatus.Monitoring => "#21352E", MirrorStatus.Stopped => "#4A2323", _ => "#2A303B" };
    public string StateForeground => SelectedRunner?.Status switch { MirrorStatus.Monitoring => "#91CFB1", MirrorStatus.Stopped => "#F2A7A7", _ => "#B9BEC8" };
    public string StartStopLabel => IsRunning ? "Stop mirroring" : "Start mirroring";

    /// <summary>Short app-wide indicator, e.g. for the sidebar.</summary>
    public string GlobalSummary
    {
        get
        {
            var active = runners.Values.Where(r => r.Status != MirrorStatus.Idle).ToArray();
            if (active.Length == 0) return "";
            if (active.Any(r => r.Status == MirrorStatus.Stopped)) return "Achievement Mirror stopped · needs attention";
            return active.Length == 1 ? $"Mirroring {active[0].Pair.DisplayName}" : $"Mirroring {active.Length} games";
        }
    }
    public bool HasGlobalSummary => GlobalSummary.Length > 0;

    partial void OnSelectedPairChanged(MirrorPair? value)
    {
        if (value != null)
        {
            SteamAppIdText = value.SteamAppId > 0 ? value.SteamAppId.ToString() : "";
            XboxTitleIdText = value.XboxTitleId;
            IsSteamToXbox = value.Direction == MirrorDirection.SteamToXbox;
            MirrorAppUnlocks = value.MirrorAppUnlocks;
        }
        SteamIdText = value != null && value.SteamId.Length > 0 ? value.SteamId : host.LocalSteamId;
        SourceAlreadyUnlockedText = "";
        RebuildRows();
        RefreshRunnerState();
    }
    partial void OnIsSteamToXboxChanged(bool value)
    {
        OnPropertyChanged(nameof(IsXboxToSteam)); OnPropertyChanged(nameof(SourceHeader)); OnPropertyChanged(nameof(TargetHeader));
        OnPropertyChanged(nameof(AppUnlocksLabel)); OnPropertyChanged(nameof(AppUnlocksDetail)); OnPropertyChanged(nameof(AppUnlocksHelp)); OnPropertyChanged(nameof(WarningText));
        var pair = SelectedPair;
        var direction = value ? MirrorDirection.SteamToXbox : MirrorDirection.XboxToSteam;
        if (pair == null || pair.Direction == direction || IsRunning) return;
        // Rows are built per direction; the old ones no longer apply.
        pair.Direction = direction; pair.Matches = []; pair.SteamList = []; pair.XboxList = []; pair.LastScanned = null;
        Save(); RebuildRows();
        Status = "Direction changed. Scan again to match achievements for this direction.";
    }
    partial void OnMirrorAppUnlocksChanged(bool value)
    {
        if (SelectedPair == null || SelectedPair.MirrorAppUnlocks == value) return;
        SelectedPair.MirrorAppUnlocks = value; Save();
    }
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanEditPair));
    partial void OnSourceAlreadyUnlockedTextChanged(string value) => OnPropertyChanged(nameof(WarningText));

    [RelayCommand]
    public void NewPair()
    {
        var pair = new MirrorPair();
        Pairs.Add(pair); SelectedPair = pair;
        SteamAppIdText = ""; XboxTitleIdText = ""; StoreLinkText = "";
        Status = "Enter the Steam App ID and Xbox Title ID, then scan.";
    }

    [RelayCommand]
    public void DeletePair()
    {
        var pair = SelectedPair;
        if (pair == null || IsRunning) return;
        Pairs.Remove(pair); runners.TryRemove(pair.Id, out _); Save();
        SelectedPair = Pairs.FirstOrDefault();
        if (SelectedPair == null) NewPair();
    }

    [RelayCommand]
    public void SaveApiKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length == 0) { Status = "Paste your Steam Web API key first. You can create one at steamcommunity.com/dev/apikey."; return; }
        try { MirrorStore.SaveKey(key); apiKey = key; ApiKeyInput = ""; Status = "Steam Web API key saved."; }
        catch { Status = "The key could not be saved."; }
        OnPropertyChanged(nameof(HasApiKey)); OnPropertyChanged(nameof(ApiKeyStatus));
    }

    [RelayCommand]
    public void ClearApiKey()
    {
        try { MirrorStore.DeleteKey(); } catch { }
        apiKey = ""; OnPropertyChanged(nameof(HasApiKey)); OnPropertyChanged(nameof(ApiKeyStatus));
        Status = "Steam Web API key removed.";
    }

    [RelayCommand]
    public async Task LookupStoreLinkAsync()
    {
        if (!CanEditPair) return;
        Status = "Looking up the Store link…";
        try
        {
            var (titleId, name, message) = await host.LookupStoreTitleAsync(StoreLinkText, lifetime.Token);
            if (titleId == null) { Status = message; return; }
            XboxTitleIdText = titleId;
            if (SelectedPair != null) SelectedPair.XboxName = name;
            Status = $"Found {name} · Title ID {titleId}. Scan to match achievements.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status = "Store lookup failed: " + error.Message; }
    }

    /// <summary>Loads both achievement lists and builds the match list, keeping earlier decisions.</summary>
    [RelayCommand]
    public async Task ScanAsync()
    {
        var pair = SelectedPair;
        if (pair == null || !CanEditPair) return;
        if (!long.TryParse(SteamAppIdText.Trim(), out var appId) || appId <= 0) { Status = "Enter the game's Steam App ID (a number, e.g. 3669870)."; return; }
        if (!uint.TryParse(XboxTitleIdText.Trim(), out var titleNumber) || titleNumber == 0) { Status = "Enter the game's decimal Xbox Title ID, or look it up from a Store link."; return; }
        if (host.XboxXuid.Length == 0) { Status = "Attach the Xbox PC app first so the Mirror can read your Xbox achievements."; return; }
        if (!HasApiKey) { Status = "Save your Steam Web API key first. It's how the Mirror reads Steam achievements on any device."; return; }
        if (!SteamWebApi.IsSteamId64(SteamIdText)) { Status = "Enter your SteamID64 (17 digits), or sign in to Steam on this PC so it can be filled in."; return; }
        IsScanning = true; Status = "Reading achievements from Steam and Xbox…";
        try
        {
            var titleId = titleNumber.ToString();
            var steamTask = ReadSteamWebAsync(appId, SteamIdText.Trim(), lifetime.Token);
            var xboxTask = host.ReadXboxAsync(titleId, lifetime.Token);
            await Task.WhenAll(steamTask, xboxTask);
            var steamList = steamTask.Result; var (xboxList, xboxName) = xboxTask.Result;
            if (steamList.Count == 0) { Status = "Steam returned no achievements for this App ID."; return; }
            if (xboxList.Count == 0) { Status = "Xbox returned no achievements for this Title ID."; return; }
            pair.SteamAppId = appId; pair.XboxTitleId = titleId; pair.SteamId = SteamIdText.Trim();
            pair.SteamName = host.SteamAppName(appId) ?? pair.SteamName;
            if (xboxName.Length > 0) pair.XboxName = xboxName;
            pair.Matches = MirrorMatcher.Build(pair.Direction, steamList, xboxList, pair.Matches);
            pair.SteamList = steamList.Select(a => new MirrorNamedId(a.Id, a.Name)).ToList();
            pair.XboxList = xboxList.Select(a => new MirrorNamedId(a.Id, a.Name)).ToList();
            pair.LastScanned = DateTimeOffset.Now;
            var source = pair.Direction == MirrorDirection.SteamToXbox ? steamList : xboxList;
            var already = source.Count(a => a.Unlocked);
            SourceAlreadyUnlockedText = already > 0 ? $"{already} achievement(s) you already earned on {pair.Source} won't be copied." : "";
            Save(); RebuildRows();
            Status = pair.UnresolvedCount == 0
                ? $"Scanned {pair.DisplayName}: every achievement is matched. Review the list, then start mirroring."
                : $"Scanned {pair.DisplayName}: {pair.UnresolvedCount} achievement(s) need review before mirroring can start.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status = "Scan failed: " + error.Message; }
        finally { IsScanning = false; RefreshRunnerState(); }
    }

    [RelayCommand]
    public async Task StartStopAsync()
    {
        var pair = SelectedPair;
        if (pair == null) return;
        if (IsRunning) { StopPair(pair); return; }
        if (pair.Matches.Count == 0) { Status = "Scan first so the achievements can be matched."; return; }
        if (!pair.ReadyToMonitor) { Status = $"{pair.UnresolvedCount} achievement(s) still need review. Pick a match or choose \"No equivalent\" for each."; return; }
        if (pair.Direction == MirrorDirection.SteamToXbox && !SteamWebApi.IsSteamId64(pair.SteamId)) { Status = "Scan again with your SteamID64 filled in."; return; }
        var clash = runners.Values.FirstOrDefault(r => r.Pair != pair && r.Status != MirrorStatus.Idle
            && (r.Pair.SteamAppId == pair.SteamAppId || r.Pair.XboxTitleId == pair.XboxTitleId));
        if (clash != null) { Status = $"{clash.Pair.DisplayName} already mirrors this game. Stop it first."; return; }
        Status = "Reading the current unlocks so nothing already earned is copied…";
        try
        {
            var baseline = await ReadSourceAsync(pair, lifetime.Token);
            var runner = runners.GetOrAdd(pair.Id, _ => CreateRunner(pair));
            runner.Start(baseline);
            nextPoll[pair.Id] = DateTimeOffset.Now + (pair.Direction == MirrorDirection.SteamToXbox ? SteamPollInterval : XboxPollInterval);
            nextPresenceCheck = DateTimeOffset.MinValue;
            EnsureLoop(); UpdateWatcher();
            Status = $"Mirroring {pair.DisplayName}. New unlocks on {pair.Source} will appear on {pair.Target}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status = "Mirroring could not start: " + error.Message; }
        RefreshRunnerState();
    }

    [RelayCommand]
    public Task ResumeSendPendingAsync() => ResumeAsync(true);

    [RelayCommand]
    public Task ResumeDiscardPendingAsync() => ResumeAsync(false);

    private async Task ResumeAsync(bool send)
    {
        var runner = SelectedRunner;
        if (runner == null || runner.Status != MirrorStatus.Stopped) return;
        try { await runner.ResumeAsync(send, lifetime.Token); }
        catch (InvalidOperationException error) { Status = error.Message; }
        EnsureLoop(); UpdateWatcher(); RefreshRunnerState();
    }

    private void StopPair(MirrorPair pair)
    {
        if (runners.TryGetValue(pair.Id, out var runner)) runner.Stop();
        UpdateWatcher(); _ = UpdateIdleAsync(CancellationToken.None);
        Status = $"Stopped mirroring {pair.DisplayName}.";
        RefreshRunnerState();
    }

    /// <summary>Stops every pair, e.g. when the app closes.</summary>
    public void StopAll()
    {
        foreach (var runner in runners.Values) runner.Stop();
        if (idleStartedByMirror != 0) { host.StopSteamIdle(); idleStartedByMirror = 0; }
        UpdateWatcher();
    }

    // ---- Hooks for unlocks Achievement Labs makes itself ----

    /// <summary>Achievement Labs unlocked an Xbox achievement. Returns false if mirroring it failed.</summary>
    public Task<bool> ReportXboxAppUnlockAsync(string titleId, string achievementId, string name, CancellationToken cancellationToken)
    {
        var runner = runners.Values.FirstOrDefault(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.XboxToSteam && r.Pair.XboxTitleId == titleId);
        return runner == null ? Task.FromResult(true) : runner.ReportAppUnlockAsync(achievementId, name, cancellationToken);
    }

    /// <summary>Achievement Labs unlocked a Steam achievement. Returns false if mirroring it failed.</summary>
    public Task<bool> ReportSteamAppUnlockAsync(long appId, string apiName, string name, CancellationToken cancellationToken)
    {
        var runner = runners.Values.FirstOrDefault(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.SteamToXbox && r.Pair.SteamAppId == appId);
        return runner == null ? Task.FromResult(true) : runner.ReportAppUnlockAsync(apiName, name, cancellationToken);
    }

    /// <summary>
    /// Checks Xbox Auto Unlock queues before they start. Only applies while an Xbox → Steam pair is being
    /// mirrored with "Also mirror Xbox unlocks I make in Achievement Labs" on. Blocking issues mean an
    /// achievement in the queue can't be mirrored; non-blocking ones are titles that simply aren't paired.
    /// </summary>
    public IReadOnlyList<MirrorPreflightIssue> CheckAutoUnlockQueues(IEnumerable<(string TitleId, string TitleName, IReadOnlyList<string> AchievementIds)> queues)
    {
        var active = runners.Values.Where(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.XboxToSteam && r.Pair.MirrorAppUnlocks).ToArray();
        if (active.Length == 0) return [];
        var issues = new List<MirrorPreflightIssue>();
        foreach (var (titleId, titleName, ids) in queues)
        {
            var runner = active.FirstOrDefault(r => r.Pair.XboxTitleId == titleId);
            if (runner == null)
            {
                var saved = Pairs.FirstOrDefault(p => p.Direction == MirrorDirection.XboxToSteam && p.XboxTitleId == titleId);
                issues.Add(new(titleName, saved == null ? "Not paired in Achievement Mirror, so its unlocks won't reach Steam." : "Paired, but mirroring isn't running for it, so its unlocks won't reach Steam.", false));
                continue;
            }
            if (runner.Status == MirrorStatus.Stopped) { issues.Add(new(titleName, "Achievement Mirror is stopped for this game. Resolve it first.", true)); continue; }
            var missing = ids.Distinct().Count(id => runner.Pair.FindBySource(id) is not { IsResolved: true });
            if (missing > 0) issues.Add(new(titleName, $"{missing} of {ids.Distinct().Count()} queued achievement(s) have no confirmed Steam match. Rescan and review them.", true));
        }
        return issues;
    }

    // ---- Monitoring loop ----

    private MirrorRunner CreateRunner(MirrorPair pair)
    {
        IMirrorTarget target = pair.Direction == MirrorDirection.SteamToXbox
            ? new XboxTarget(host, pair.XboxTitleId) : new SteamTarget(host, pair.SteamAppId);
        var runner = new MirrorRunner(pair, target);
        runner.Changed += () => Dispatcher.UIThread.Post(RefreshRunnerState);
        runner.Logged += entry =>
        {
            MirrorStore.AppendLog(pair.DisplayName, entry);
            Dispatcher.UIThread.Post(() => { if (SelectedPair == pair) AddLog(entry); });
        };
        runner.Mirrored += (sourceId, at) => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedPair != pair) return;
            var row = Rows.FirstOrDefault(r => r.Match.SourceId(pair.Direction) == sourceId);
            if (row != null) row.LastMirrored = $"To {pair.Target} {at.LocalDateTime:HH:mm}";
        });
        runner.Stopped += (title, message) => { host.Alert(title, message); Dispatcher.UIThread.Post(RefreshRunnerState); };
        return runner;
    }

    private void EnsureLoop()
    {
        if (loop is { IsCompleted: false }) return;
        loop = Task.Run(() => RunLoopAsync(lifetime.Token));
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var active = runners.Values.Where(r => r.Status != MirrorStatus.Idle).ToArray();
            if (active.Length == 0) { await UpdateIdleAsync(token); return; }
            foreach (var runner in active)
            {
                if (!nextPoll.TryGetValue(runner.Pair.Id, out var due) || DateTimeOffset.Now < due) continue;
                nextPoll[runner.Pair.Id] = DateTimeOffset.Now + (runner.Pair.Direction == MirrorDirection.SteamToXbox ? SteamPollInterval : XboxPollInterval);
                await PollAsync(runner, "checked", token);
            }
            if (DateTimeOffset.Now >= nextPresenceCheck)
            {
                nextPresenceCheck = DateTimeOffset.Now + PresencePollInterval;
                await UpdateIdleAsync(token);
            }
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(MirrorRunner runner, string detectedBy, CancellationToken token)
    {
        try
        {
            var snapshot = await ReadSourceAsync(runner.Pair, token);
            if (lastPollError.TryRemove(runner.Pair.Id, out _)) Dispatcher.UIThread.Post(() => Status = $"Reading {runner.Pair.Source} works again for {runner.Pair.DisplayName}.");
            await runner.ObserveSourceAsync(snapshot, detectedBy, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // A failed read never unlocks anything; it is reported once until it recovers.
            var message = $"Couldn't read {runner.Pair.Source} for {runner.Pair.DisplayName}: {error.Message}";
            if (lastPollError.TryGetValue(runner.Pair.Id, out var previous) && previous == message) return;
            lastPollError[runner.Pair.Id] = message;
            Dispatcher.UIThread.Post(() => Status = message + " The Mirror keeps trying.");
        }
    }

    private Task<IReadOnlyList<MirrorAchievement>> ReadSourceAsync(MirrorPair pair, CancellationToken token) =>
        pair.Direction == MirrorDirection.SteamToXbox
            ? ReadSteamWebAsync(pair.SteamAppId, pair.SteamId, token)
            : ReadXboxListAsync(pair.XboxTitleId, token);

    private async Task<IReadOnlyList<MirrorAchievement>> ReadXboxListAsync(string titleId, CancellationToken token) => (await host.ReadXboxAsync(titleId, token)).Achievements;

    private async Task<IReadOnlyList<MirrorAchievement>> ReadSteamWebAsync(long appId, string steamId, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return await new SteamWebApi(http).GetPlayerAchievementsAsync(apiKey, steamId, appId, token);
    }

    /// <summary>
    /// Watches Steam's local stats folder. When the stats file for a mirrored game changes (Steam writes it
    /// on unlock when the game runs on this PC), the Web API is checked right away instead of at the next poll.
    /// </summary>
    private void UpdateWatcher()
    {
        var wanted = runners.Values.Any(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.SteamToXbox);
        if (!wanted) { statsWatcher?.Dispose(); statsWatcher = null; return; }
        if (statsWatcher != null) return;
        try
        {
            var folder = Path.Combine(host.SteamInstallPath, "appcache", "stats");
            if (host.SteamInstallPath.Length == 0 || !Directory.Exists(folder)) return;
            statsWatcher = new FileSystemWatcher(folder, "UserGameStats_*.bin") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            statsWatcher.Changed += OnStatsFileChanged; statsWatcher.Created += OnStatsFileChanged; statsWatcher.Renamed += OnStatsFileChanged;
            statsWatcher.EnableRaisingEvents = true;
        }
        catch { statsWatcher?.Dispose(); statsWatcher = null; }
    }

    private void OnStatsFileChanged(object sender, FileSystemEventArgs e)
    {
        // UserGameStats_<accountId>_<appId>.bin
        var parts = Path.GetFileNameWithoutExtension(e.Name ?? "").Split('_');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var appId)) return;
        var runner = runners.Values.FirstOrDefault(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.SteamToXbox && r.Pair.SteamAppId == appId);
        if (runner == null) return;
        if (SteamWebApi.IsSteamId64(runner.Pair.SteamId) && parts[1] != SteamWebApi.AccountId(runner.Pair.SteamId).ToString()) return;
        _ = Task.Run(async () =>
        {
            // Steam may take a moment to upload the unlock, so check a few times.
            foreach (var wait in FileTriggerDelays)
            {
                try { await Task.Delay(wait, lifetime.Token); } catch (OperationCanceledException) { return; }
                await PollAsync(runner, "Steam stats file on this PC", lifetime.Token);
            }
        });
    }

    /// <summary>
    /// Xbox → Steam pairs idle the game on Steam only while the Xbox status shows you in that title,
    /// so Steam playtime follows real play.
    /// </summary>
    private async Task UpdateIdleAsync(CancellationToken token)
    {
        try
        {
            long wanted = 0;
            foreach (var runner in runners.Values.Where(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.XboxToSteam))
            {
                if (await host.IsXboxPresenceInTitleAsync(runner.Pair.XboxTitleId, token)) { wanted = runner.Pair.SteamAppId; break; }
            }
            if (wanted == idleStartedByMirror) return;
            if (idleStartedByMirror != 0 && host.SteamIdleAppId == idleStartedByMirror)
            {
                host.StopSteamIdle();
                Log(runners.Values.FirstOrDefault(r => r.Pair.SteamAppId == idleStartedByMirror)?.Pair, "Xbox no longer shows this game · Steam idling stopped.");
            }
            idleStartedByMirror = 0;
            if (wanted != 0)
            {
                await host.StartSteamIdleAsync(wanted, token);
                idleStartedByMirror = wanted;
                Log(runners.Values.FirstOrDefault(r => r.Pair.SteamAppId == wanted)?.Pair, "Xbox shows you in this game · Steam idling started.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Dispatcher.UIThread.Post(() => Status = "Steam idling couldn't be updated: " + error.Message); }
    }

    private void Log(MirrorPair? pair, string message)
    {
        if (pair == null) return;
        var entry = new MirrorLogEntry(DateTimeOffset.Now, message);
        MirrorStore.AppendLog(pair.DisplayName, entry);
        Dispatcher.UIThread.Post(() => { if (SelectedPair == pair) AddLog(entry); });
    }

    // ---- View state ----

    private void AddLog(MirrorLogEntry entry)
    {
        LogEntries.Insert(0, entry);
        while (LogEntries.Count > MirrorRunner.MaxLogEntries) LogEntries.RemoveAt(LogEntries.Count - 1);
    }

    private void RebuildRows()
    {
        Rows.Clear();
        var pair = SelectedPair;
        if (pair != null && pair.Matches.Count > 0)
        {
            var options = TargetOptions(pair);
            foreach (var match in pair.SourceRows) Rows.Add(new MirrorRowViewModel(match, pair.Direction, options, OnRowChanged));
        }
        LogEntries.Clear();
        if (SelectedRunner is { } runner) foreach (var entry in runner.Log.Reverse()) LogEntries.Add(entry);
        OnPropertyChanged(nameof(MatchSummary));
    }

    private static List<MirrorOption> TargetOptions(MirrorPair pair)
    {
        var list = pair.Direction == MirrorDirection.SteamToXbox ? pair.XboxList : pair.SteamList;
        return [MirrorOption.NoEquivalent, .. list.DistinctBy(a => a.Id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a => new MirrorOption(a.Id, a.Name))];
    }

    private void OnRowChanged()
    {
        Save();
        OnPropertyChanged(nameof(MatchSummary));
    }

    private void RefreshRunnerState()
    {
        OnPropertyChanged(nameof(IsMonitoring)); OnPropertyChanged(nameof(IsStopped)); OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanEditPair)); OnPropertyChanged(nameof(StopReason)); OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingText)); OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(StateBackground));
        OnPropertyChanged(nameof(StateForeground)); OnPropertyChanged(nameof(StartStopLabel)); OnPropertyChanged(nameof(MatchSummary));
        OnPropertyChanged(nameof(GlobalSummary)); OnPropertyChanged(nameof(HasGlobalSummary));
    }

    private void Save()
    {
        try { MirrorStore.SavePairs(Pairs); }
        catch { Status = "Mirror pairs could not be saved."; }
    }

    public void Dispose() { lifetime.Cancel(); StopAll(); statsWatcher?.Dispose(); }

    private sealed class XboxTarget(IMirrorHost host, string titleId) : IMirrorTarget
    {
        public async Task<IReadOnlyList<MirrorAchievement>> ReadAsync(CancellationToken cancellationToken) => (await host.ReadXboxAsync(titleId, cancellationToken)).Achievements;
        public Task UnlockAsync(string achievementId, CancellationToken cancellationToken) => host.UnlockXboxAsync(titleId, achievementId, cancellationToken);
    }

    private sealed class SteamTarget(IMirrorHost host, long appId) : IMirrorTarget
    {
        public Task<IReadOnlyList<MirrorAchievement>> ReadAsync(CancellationToken cancellationToken) => host.ReadSteamLocalAsync(appId, cancellationToken);
        public Task UnlockAsync(string achievementId, CancellationToken cancellationToken) => host.UnlockSteamAsync(appId, achievementId, cancellationToken);
    }
}
