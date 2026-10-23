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
    [ObservableProperty] private bool steamUnlocked;
    [ObservableProperty] private bool xboxUnlocked;
    public bool NeedsConfirm => Match.State == MirrorMatchState.Suggested;
    private bool Paired => Match.State is MirrorMatchState.Matched or MirrorMatchState.Confirmed;
    private string Unlocks => SteamUnlocked && XboxUnlocked ? "both" : XboxUnlocked ? "xbox" : SteamUnlocked ? "steam" : "";
    public string StatusLabel => Paired && Unlocks.Length > 0 ? Unlocks switch
    {
        "both" => "Unlocked on both",
        "xbox" => "Unlocked on Xbox",
        _ => "Unlocked on Steam"
    } : Match.State switch
    {
        MirrorMatchState.Matched => "Matched",
        MirrorMatchState.Confirmed => "Confirmed",
        MirrorMatchState.Suggested => "Confirm pair",
        MirrorMatchState.Ignored => "Not mirrored",
        _ => "Needs review"
    };
    // Xbox green, Steam's dark navy-black, gold for both.
    public string StatusBackground => Paired && Unlocks.Length > 0 ? Unlocks switch { "both" => "#C9A227", "xbox" => "#107C10", _ => "#171A21" } : Match.State switch
    {
        MirrorMatchState.Matched or MirrorMatchState.Confirmed => "#21352E",
        MirrorMatchState.Ignored => "#2A303B",
        _ => "#40331A"
    };
    public string StatusForeground => Paired && Unlocks.Length > 0 ? Unlocks switch { "both" => "#241B00", "xbox" => "#FFFFFF", _ => "#C7D5E0" } : Match.State switch
    {
        MirrorMatchState.Matched or MirrorMatchState.Confirmed => "#91CFB1",
        MirrorMatchState.Ignored => "#B9BEC8",
        _ => "#F0C674"
    };
    public string StatusBorder => Paired && Unlocks == "steam" ? "#3D4450" : "Transparent";
    public string? StatusTip => LastMirrored.Length > 0 ? LastMirrored : null;

    partial void OnSelectedOptionChanged(MirrorOption? value)
    {
        if (loading) return;
        if (value == null) { MirrorMatcher.SetTarget(Match, Direction, null); Match.State = MirrorMatchState.Unmatched; }
        else if (value.Id == null) { MirrorMatcher.SetTarget(Match, Direction, null); Match.State = MirrorMatchState.Ignored; }
        else { MirrorMatcher.SetTarget(Match, Direction, new(value.Id, value.Name, "", false, null)); Match.State = MirrorMatchState.Confirmed; }
        Refresh(); changed();
    }
    partial void OnLastMirroredChanged(string value) => Refresh();
    partial void OnSteamUnlockedChanged(bool value) => Refresh();
    partial void OnXboxUnlockedChanged(bool value) => Refresh();

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
        OnPropertyChanged(nameof(StatusBorder)); OnPropertyChanged(nameof(StatusTip));
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
    private static readonly TimeSpan LocalPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan XboxPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] FileTriggerDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2)];

    private readonly IMirrorHost host;
    private readonly ConcurrentDictionary<Guid, MirrorRunner> runners = new();
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

    /// <summary>The running build, so test builds can't be mistaken for each other.</summary>
    public static string BuildVersion { get; } = typeof(AchievementMirrorViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public static string BuildLabel => "Achievement Labs build " + BuildVersion;

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
    [ObservableProperty] private bool showInGame = true;
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

    private bool refreshingPairs;
    partial void OnSelectedPairChanged(MirrorPair? value)
    {
        if (refreshingPairs) return;
        if (value != null)
        {
            SteamAppIdText = value.SteamAppId > 0 ? value.SteamAppId.ToString() : "";
            XboxTitleIdText = value.XboxTitleId;
            IsSteamToXbox = value.Direction == MirrorDirection.SteamToXbox;
            MirrorAppUnlocks = value.MirrorAppUnlocks;
            ShowInGame = value.ShowInGame;
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
    partial void OnShowInGameChanged(bool value)
    {
        if (SelectedPair == null || SelectedPair.ShowInGame == value) return;
        SelectedPair.ShowInGame = value; Save();
        nextPresenceCheck = DateTimeOffset.MinValue; // Apply right away if mirroring is running.
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
            var steamTask = ReadSteamForScanAsync(appId, SteamIdText.Trim(), lifetime.Token);
            var xboxTask = host.ReadXboxAsync(titleId, lifetime.Token);
            await Task.WhenAll(steamTask, xboxTask);
            var steamList = steamTask.Result; var (xboxList, xboxName) = xboxTask.Result;
            if (steamList.Count == 0) { Status = "Steam returned no achievements for this App ID."; return; }
            if (xboxList.Count == 0) { Status = "Xbox returned no achievements for this Title ID."; return; }
            pair.SteamAppId = appId; pair.XboxTitleId = titleId; pair.SteamId = SteamIdText.Trim();
            pair.SteamName = host.SteamAppName(appId) ?? pair.SteamName;
            if (xboxName.Length > 0) pair.XboxName = xboxName;
            pair.Matches = MirrorMatcher.Build(pair.Direction, steamList, xboxList, pair.Matches);
            pair.SteamList = steamList.Select(a => new MirrorNamedId(a.Id, a.Name, a.Unlocked)).ToList();
            pair.XboxList = xboxList.Select(a => new MirrorNamedId(a.Id, a.Name, a.Unlocked)).ToList();
            pair.LastScanned = DateTimeOffset.Now;
            var source = pair.Direction == MirrorDirection.SteamToXbox ? steamList : xboxList;
            var already = source.Count(a => a.Unlocked);
            SourceAlreadyUnlockedText = already > 0 ? $"{already} achievement(s) you already earned on {pair.Source} won't be copied." : "";
            Save(); RefreshPairName(pair); RebuildRows();
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
        if (PresenceConflictMessage(pair) is { } conflict)
        {
            Status = conflict;
            host.Alert("Achievement Mirror can't start", conflict);
            return;
        }
        Status = "Reading the current unlocks so nothing already earned is copied…";
        try
        {
            ResetSources(pair);
            IReadOnlyList<MirrorAchievement> baseline;
            string baselineSource;
            if (pair.Direction == MirrorDirection.SteamToXbox)
            {
                var local = await TryReadSourceAsync(pair, SourceKind.SteamLocal, lifetime.Token);
                var web = await TryReadSourceAsync(pair, SourceKind.SteamWeb, lifetime.Token);
                if (local.List == null && web.List == null) throw new InvalidOperationException(web.Error ?? local.Error ?? "Steam achievements couldn't be read.");
                // This PC's own stats file is current; the Web API can lag, so it is only the baseline when there is no local file.
                baseline = local.List ?? web.List!;
                baselineSource = local.List != null ? "this PC" : "the Steam Web API";
                if (local.List != null) SetBaseline(pair, SourceKind.SteamLocal, local.List);
                if (web.List != null) SetBaseline(pair, SourceKind.SteamWeb, web.List);
            }
            else
            {
                baseline = await ReadSourceAsync(pair, SourceKind.Xbox, lifetime.Token);
                baselineSource = "Xbox";
                SetBaseline(pair, SourceKind.Xbox, baseline);
            }
            var runner = runners.GetOrAdd(pair.Id, _ => CreateRunner(pair));
            runner.Start(baseline);
            UpdateUnlockStates(pair, pair.Source, baseline);
            _ = RefreshTargetStatesAsync(pair);
            Log(pair, $"Baseline read from {baselineSource} · Achievement Labs build {BuildVersion}.");
            if (pair.Direction == MirrorDirection.SteamToXbox && !sources.ContainsKey((pair.Id, SourceKind.SteamLocal)))
                Log(pair, "No Steam stats file for this game on this PC yet, so only the Steam Web API is used. It can lag behind by a minute or more.");
            var now = DateTimeOffset.Now;
            foreach (var kind in KindsFor(pair)) nextPoll[(pair.Id, kind)] = now + Interval(kind);
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
        if (PresenceConflictMessage(runner.Pair) is { } conflict) { Status = conflict; return; }
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
        host.StopMirrorPresence();
        UpdateWatcher();
    }

    // ---- Hooks for unlocks Achievement Labs makes itself ----

    private MirrorRunner? AppUnlockRunner(MirrorDirection direction, string titleId, long appId) => runners.Values.FirstOrDefault(r =>
        r.Status != MirrorStatus.Idle && r.Pair.Direction == direction && (direction == MirrorDirection.XboxToSteam ? r.Pair.XboxTitleId == titleId : r.Pair.SteamAppId == appId));

    /// <summary>Call before Achievement Labs unlocks an Xbox achievement (see <see cref="MirrorRunner.ExpectAppUnlock"/>).</summary>
    public void ExpectXboxAppUnlock(string titleId, string achievementId, string name) => AppUnlockRunner(MirrorDirection.XboxToSteam, titleId, 0)?.ExpectAppUnlock(achievementId, name);
    public void CancelXboxAppUnlock(string titleId, string achievementId) => AppUnlockRunner(MirrorDirection.XboxToSteam, titleId, 0)?.CancelAppUnlock(achievementId);
    /// <summary>Call before Achievement Labs unlocks a Steam achievement.</summary>
    public void ExpectSteamAppUnlock(long appId, string apiName, string name) => AppUnlockRunner(MirrorDirection.SteamToXbox, "", appId)?.ExpectAppUnlock(apiName, name);
    public void CancelSteamAppUnlock(long appId, string apiName) => AppUnlockRunner(MirrorDirection.SteamToXbox, "", appId)?.CancelAppUnlock(apiName);

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
            ? new XboxTarget(host, pair.XboxTitleId, token => EnsurePresenceForUnlockAsync(pair, token)) : new SteamTarget(host, pair.SteamAppId);
        var runner = new MirrorRunner(pair, target);
        runner.Changed += () => Dispatcher.UIThread.Post(RefreshRunnerState);
        runner.Logged += entry =>
        {
            MirrorStore.AppendLog(pair.DisplayName, entry);
            Dispatcher.UIThread.Post(() => { if (SelectedPair == pair) AddLog(entry); });
        };
        runner.Mirrored += (sourceId, at) => Dispatcher.UIThread.Post(() =>
        {
            if (pair.FindBySource(sourceId)?.TargetId(pair.Direction) is { Length: > 0 } targetId) MarkUnlocked(pair, pair.Target, targetId);
            if (SelectedPair != pair) return;
            var row = Rows.FirstOrDefault(r => r.Match.SourceId(pair.Direction) == sourceId);
            if (row != null) row.LastMirrored = $"Mirrored to {pair.Target} at {at.LocalDateTime:HH:mm:ss}";
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
            if (active.Length == 0) { await UpdateIdleAsync(token); await UpdateXboxPresenceAsync(token); return; }
            foreach (var runner in active)
            {
                foreach (var kind in KindsFor(runner.Pair))
                {
                    if (!nextPoll.TryGetValue((runner.Pair.Id, kind), out var due) || DateTimeOffset.Now < due) continue;
                    nextPoll[(runner.Pair.Id, kind)] = DateTimeOffset.Now + Interval(kind);
                    await PollAsync(runner, kind, "checked", token);
                }
            }
            await UpdateXboxPresenceAsync(token);
            if (DateTimeOffset.Now >= nextPresenceCheck)
            {
                nextPresenceCheck = DateTimeOffset.Now + PresencePollInterval;
                await UpdateIdleAsync(token);
            }
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
        }
    }

    private enum SourceKind { SteamLocal, SteamWeb, Xbox }

    private sealed class SourceState
    {
        /// <summary>What this source said was unlocked (and when) the first time it was read after Start.</summary>
        public Dictionary<string, DateTimeOffset?> Known { get; } = new(StringComparer.Ordinal);
        public int LastUnlocked = -1;
        public string? LastError;
    }

    private readonly ConcurrentDictionary<(Guid, SourceKind), SourceState> sources = new();
    private readonly ConcurrentDictionary<(Guid, SourceKind), DateTimeOffset> nextPoll = new();

    private static IEnumerable<SourceKind> KindsFor(MirrorPair pair) =>
        pair.Direction == MirrorDirection.SteamToXbox ? [SourceKind.SteamLocal, SourceKind.SteamWeb] : [SourceKind.Xbox];

    private static TimeSpan Interval(SourceKind kind) => kind switch
    {
        SourceKind.SteamLocal => LocalPollInterval,
        SourceKind.SteamWeb => SteamPollInterval,
        _ => XboxPollInterval
    };

    private static string Label(SourceKind kind) => kind switch
    {
        SourceKind.SteamLocal => "Steam on this PC",
        SourceKind.SteamWeb => "Steam Web API",
        _ => "Xbox"
    };

    private void ResetSources(MirrorPair pair)
    {
        foreach (var key in sources.Keys.Where(k => k.Item1 == pair.Id).ToArray()) sources.TryRemove(key, out _);
        foreach (var key in nextPoll.Keys.Where(k => k.Item1 == pair.Id).ToArray()) nextPoll.TryRemove(key, out _);
        foreach (var key in loggedOnce.Keys.Where(k => k.Item1 == pair.Id).ToArray()) loggedOnce.TryRemove(key, out _);
    }

    private SourceState SetBaseline(MirrorPair pair, SourceKind kind, IReadOnlyList<MirrorAchievement> list)
    {
        var state = new SourceState { LastUnlocked = list.Count(a => a.Unlocked) };
        foreach (var achievement in list.Where(a => a.Unlocked)) state.Known[achievement.Id] = achievement.UnlockedAt;
        sources[(pair.Id, kind)] = state;
        return state;
    }

    /// <summary>
    /// Hides unlocks this source already reported when mirroring started, so a source that lags behind
    /// (the Steam Web API) can't make an old or re-locked achievement look new. An achievement counts as
    /// new only if it wasn't unlocked in this source's baseline, or was unlocked again at a different time.
    /// </summary>
    private static IReadOnlyList<MirrorAchievement> WithoutKnown(SourceState state, IReadOnlyList<MirrorAchievement> list) =>
        list.Select(a => a.Unlocked && state.Known.TryGetValue(a.Id, out var at) && at == a.UnlockedAt ? a with { Unlocked = false } : a).ToArray();

    private async Task PollAsync(MirrorRunner runner, SourceKind kind, string detectedBy, CancellationToken token, bool alwaysReport = false)
    {
        var pair = runner.Pair;
        var key = (pair.Id, kind);
        try
        {
            var list = await ReadSourceAsync(pair, kind, token);
            // The status column follows this PC's stats file when there is one; the Web API lags behind it.
            if (kind != SourceKind.SteamWeb || !sources.ContainsKey((pair.Id, SourceKind.SteamLocal))) UpdateUnlockStates(pair, pair.Source, list);
            if (!sources.TryGetValue(key, out var state))
            {
                // First successful read of this source (e.g. the local file appeared later): it becomes its baseline.
                SetBaseline(pair, kind, list);
                Log(pair, $"{Label(kind)} is now available · {list.Count(a => a.Unlocked)} of {list.Count} unlocked, used as its starting point.");
                return;
            }
            if (state.LastError != null) { state.LastError = null; Log(pair, $"Reading {Label(kind)} works again."); }
            var unlocked = list.Count(a => a.Unlocked);
            if (unlocked != state.LastUnlocked || alwaysReport)
                Log(pair, $"{Label(kind)} reports {unlocked} of {list.Count} unlocked ({detectedBy}).");
            state.LastUnlocked = unlocked;
            await runner.ObserveSourceAsync(WithoutKnown(state, list), Label(kind), token);
        }
        catch (OperationCanceledException) { }
        catch (FileNotFoundException) when (kind == SourceKind.SteamLocal) { /* No local file for this game yet. */ }
        catch (Exception error)
        {
            // A failed read never unlocks anything; it is reported once until it recovers.
            var message = $"Couldn't read {Label(kind)}: {error.Message}";
            var state = sources.GetOrAdd(key, _ => new SourceState());
            if (!alwaysReport && state.LastError == message) return;
            state.LastError = message;
            Log(pair, message + " The Mirror keeps trying.", isError: true);
            Dispatcher.UIThread.Post(() => Status = $"{pair.DisplayName}: {message} The Mirror keeps trying.");
        }
    }

    /// <summary>Reads every source right now and writes what each reports to the activity log.</summary>
    [RelayCommand]
    public async Task CheckNowAsync()
    {
        var runner = SelectedRunner;
        if (runner == null || runner.Status == MirrorStatus.Idle) { Status = "Start mirroring first."; return; }
        Status = $"Checking {runner.Pair.Source} now…";
        foreach (var kind in KindsFor(runner.Pair))
        {
            if (kind == SourceKind.SteamLocal && !sources.ContainsKey((runner.Pair.Id, kind))) { Log(runner.Pair, "Steam on this PC: no stats file for this game."); continue; }
            await PollAsync(runner, kind, "checked now", lifetime.Token, alwaysReport: true);
        }
        Status = $"Checked {runner.Pair.Source}. See the activity log.";
    }

    private async Task<(IReadOnlyList<MirrorAchievement>? List, string? Error)> TryReadSourceAsync(MirrorPair pair, SourceKind kind, CancellationToken token)
    {
        try { return (await ReadSourceAsync(pair, kind, token), null); }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return (null, null); }
        catch (Exception error) { return (null, error.Message); }
    }

    private Task<IReadOnlyList<MirrorAchievement>> ReadSourceAsync(MirrorPair pair, SourceKind kind, CancellationToken token) => kind switch
    {
        SourceKind.SteamLocal => Task.Run(() => ReadSteamLocal(pair), token),
        SourceKind.SteamWeb => ReadSteamWebAsync(pair.SteamAppId, pair.SteamId, token),
        _ => ReadXboxListAsync(pair.XboxTitleId, token)
    };

    private IReadOnlyList<MirrorAchievement> ReadSteamLocal(MirrorPair pair)
    {
        if (host.SteamInstallPath.Length == 0 || !SteamWebApi.IsSteamId64(pair.SteamId)) throw new FileNotFoundException();
        return SteamLocalStats.TryRead(host.SteamInstallPath, SteamWebApi.AccountId(pair.SteamId), pair.SteamAppId) ?? throw new FileNotFoundException();
    }

    private async Task<IReadOnlyList<MirrorAchievement>> ReadXboxListAsync(string titleId, CancellationToken token) => (await host.ReadXboxAsync(titleId, token)).Achievements;

    /// <summary>Scan reads this PC's Steam stats file (instant, like SAM) and uses the Web API only without one.</summary>
    private async Task<IReadOnlyList<MirrorAchievement>> ReadSteamForScanAsync(long appId, string steamId, CancellationToken token)
    {
        try
        {
            if (host.SteamInstallPath.Length > 0 && SteamWebApi.IsSteamId64(steamId)
                && await Task.Run(() => SteamLocalStats.TryRead(host.SteamInstallPath, SteamWebApi.AccountId(steamId), appId), token) is { Count: > 0 } local)
                return local;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Unreadable local file: fall back to the Web API. */ }
        return await ReadSteamWebAsync(appId, steamId, token);
    }

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
        var pair = runners.Values.First(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.SteamToXbox).Pair;
        try
        {
            var folder = Path.Combine(host.SteamInstallPath, "appcache", "stats");
            if (host.SteamInstallPath.Length == 0 || !Directory.Exists(folder))
            {
                Log(pair, "Steam's local stats folder wasn't found, so only the Steam Web API is checked (every 15 seconds, and it can lag).");
                return;
            }
            statsWatcher = new FileSystemWatcher(folder, "UserGameStats_*.bin") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            statsWatcher.Changed += OnStatsFileChanged; statsWatcher.Created += OnStatsFileChanged; statsWatcher.Renamed += OnStatsFileChanged;
            statsWatcher.EnableRaisingEvents = true;
            Log(pair, "Watching Steam's stats file on this PC (instant), plus the Steam Web API every 15 seconds for other devices.");
        }
        catch (Exception error)
        {
            statsWatcher?.Dispose(); statsWatcher = null;
            Log(pair, "Couldn't watch Steam's stats files (" + error.Message + "). The Steam Web API is still checked every 15 seconds.", isError: true);
        }
    }

    private DateTimeOffset lastFileNotice;

    private void OnStatsFileChanged(object sender, FileSystemEventArgs e)
    {
        // UserGameStats_<accountId>_<appId>.bin
        var parts = Path.GetFileNameWithoutExtension(e.Name ?? "").Split('_');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var appId)) return;
        var runner = runners.Values.FirstOrDefault(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.SteamToXbox && r.Pair.SteamAppId == appId);
        if (runner == null) return;
        if (SteamWebApi.IsSteamId64(runner.Pair.SteamId) && parts[1] != SteamWebApi.AccountId(runner.Pair.SteamId).ToString()) return;
        var now = DateTimeOffset.Now;
        if (now - lastFileNotice < TimeSpan.FromSeconds(1)) return; // Steam writes the file several times per save.
        lastFileNotice = now;
        _ = Task.Run(async () =>
        {
            // Read once Steam has finished writing, then once more in case it saves in two steps.
            foreach (var wait in FileTriggerDelays)
            {
                try { await Task.Delay(wait, lifetime.Token); } catch (OperationCanceledException) { return; }
                await PollAsync(runner, SourceKind.SteamLocal, "stats file changed", lifetime.Token);
            }
        });
    }

    private static readonly TimeSpan PresenceBurst = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlayingWebInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> presenceBurstUntil = new();
    private readonly SemaphoreSlim presenceGate = new(1, 1);
    private DateTimeOffset nextPlayingWebCheck, presenceRetryAfter;
    private long webPlayingAppId;
    private string? playingSource;

    /// <summary>Steam → Xbox pairs show the game on Xbox; with "Show me in the game" on, Xbox → Steam pairs do too.</summary>
    private static bool UsesXboxPresence(MirrorPair pair) => pair.Direction == MirrorDirection.SteamToXbox || pair.ShowInGame;

    private string? PresenceConflictMessage(MirrorPair pair) =>
        UsesXboxPresence(pair) && host.XboxPresenceConflict(pair.XboxTitleId) is { } other
            ? $"{other} Achievement Mirror needs to show you in {pair.DisplayName} on Xbox, and Xbox can only show one game at a time. Stop that first, then {(runners.TryGetValue(pair.Id, out var r) && r.Status == MirrorStatus.Stopped ? "resume" : "start")} mirroring."
            : null;

    /// <summary>Before each Steam → Xbox unlock: make sure Xbox shows you in the game, if only briefly.</summary>
    private async Task EnsurePresenceForUnlockAsync(MirrorPair pair, CancellationToken token)
    {
        if (PresenceConflictMessage(pair) is { } conflict) throw new InvalidOperationException(conflict);
        presenceBurstUntil[pair.Id] = DateTimeOffset.Now + PresenceBurst;
        if (host.XboxPresenceShows(pair.XboxTitleId)) return;
        await presenceGate.WaitAsync(token);
        try
        {
            if (host.XboxPresenceShows(pair.XboxTitleId)) return;
            if (host.MirrorPresenceTitle != null) host.StopMirrorPresence();
            if (await host.StartMirrorPresenceAsync(pair.XboxTitleId, token)) Log(pair, $"Showing you in {pair.DisplayName} on Xbox for this unlock.");
            else Log(pair, "Couldn't show you in the game on Xbox (another operation is busy). Unlocking anyway.", isError: true);
        }
        finally { presenceGate.Release(); }
    }

    /// <summary>
    /// Steam → Xbox pairs show you in the game on Xbox only while Steam shows you playing it (on this PC or,
    /// through the Web API, on another device), plus a short burst around each mirrored unlock. Presence
    /// another feature sends for a different title is never overridden: the pair stops and says why.
    /// </summary>
    private async Task UpdateXboxPresenceAsync(CancellationToken token)
    {
        if (!await presenceGate.WaitAsync(0, token)) return;
        try
        {
            var monitoring = runners.Values.Where(r => r.Status == MirrorStatus.Monitoring && UsesXboxPresence(r.Pair)).ToArray();
            foreach (var runner in monitoring)
                if (PresenceConflictMessage(runner.Pair) is { } conflict) runner.Halt(conflict);
            monitoring = monitoring.Where(r => r.Status == MirrorStatus.Monitoring).ToArray();

            var local = monitoring.Length > 0 ? host.SteamRunningAppId : 0;
            if (monitoring.Length > 0 && DateTimeOffset.Now >= nextPlayingWebCheck && apiKey.Length > 0)
            {
                nextPlayingWebCheck = DateTimeOffset.Now + PlayingWebInterval;
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    webPlayingAppId = await new SteamWebApi(http).GetPlayingAppIdAsync(apiKey, monitoring[0].Pair.SteamId, token);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Keep the last answer; the local check still works. */ }
            }
            var now = DateTimeOffset.Now;
            MirrorRunner? wanted = null; string? reason = null;
            foreach (var runner in monitoring)
            {
                var appId = runner.Pair.SteamAppId;
                if (runner.Pair.ShowInGame) { wanted = runner; reason = "Mirroring is on"; break; }
                if (local == appId) { wanted = runner; reason = "Steam on this PC shows you playing"; break; }
                if (webPlayingAppId == appId) { wanted = runner; reason = "Steam Web API shows you playing (another device, or Steam hasn't caught up after you quit)"; break; }
                if (presenceBurstUntil.TryGetValue(runner.Pair.Id, out var until) && until > now) { wanted = runner; reason = null; break; }
            }

            var current = host.MirrorPresenceTitle;
            if (wanted == null)
            {
                if (current != null)
                {
                    host.StopMirrorPresence();
                    Log(runners.Values.FirstOrDefault(r => r.Pair.XboxTitleId == current)?.Pair,
                        runners.Values.Any(r => r.Pair.XboxTitleId == current && r.Status == MirrorStatus.Monitoring) ? "Steam no longer shows you playing · Xbox presence stopped." : "Xbox presence stopped.");
                }
                playingSource = null;
                return;
            }
            var pair = wanted.Pair;
            if (current == pair.XboxTitleId || host.XboxPresenceShows(pair.XboxTitleId))
            {
                if (reason != null && reason != playingSource) { playingSource = reason; if (current == pair.XboxTitleId) Log(pair, reason + "."); }
                return;
            }
            if (now < presenceRetryAfter) return;
            if (current != null) host.StopMirrorPresence();
            if (await host.StartMirrorPresenceAsync(pair.XboxTitleId, token))
            {
                playingSource = reason;
                Log(pair, (reason ?? "Mirrored unlock") + $" · showing you in {pair.DisplayName} on Xbox.");
            }
            else presenceRetryAfter = now + TimeSpan.FromSeconds(60);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Dispatcher.UIThread.Post(() => Status = "Xbox presence couldn't be updated: " + error.Message); }
        finally { presenceGate.Release(); }
    }

    /// <summary>
    /// Xbox → Steam pairs idle the game on Steam only while the Xbox status shows you in that title,
    /// so Steam playtime follows real play.
    /// </summary>
    private async Task UpdateIdleAsync(CancellationToken token)
    {
        try
        {
            long wanted = 0; string why = "";
            var running = host.SteamRunningAppId;
            foreach (var runner in runners.Values.Where(r => r.Status == MirrorStatus.Monitoring && r.Pair.ShowInGame))
            {
                // The real game (or SAM) already shows you playing on Steam; a second session isn't needed.
                if (running == runner.Pair.SteamAppId && idleStartedByMirror != runner.Pair.SteamAppId) { LogOnce(runner.Pair, "realgame", "Steam already shows you playing this game · not idling it."); continue; }
                wanted = runner.Pair.SteamAppId; why = "Mirroring is on"; break;
            }
            if (wanted == 0)
                foreach (var runner in runners.Values.Where(r => r.Status != MirrorStatus.Idle && r.Pair.Direction == MirrorDirection.XboxToSteam && !r.Pair.ShowInGame))
                    if (await host.IsXboxPresenceInTitleAsync(runner.Pair.XboxTitleId, token)) { wanted = runner.Pair.SteamAppId; why = "Xbox shows you in this game"; break; }
            if (wanted == idleStartedByMirror) return;
            // Never replace Steam idling another feature started (Steam tab presence).
            if (wanted != 0 && host.SteamIdleAppId is var other && other != 0 && other != idleStartedByMirror && other != wanted)
            {
                LogOnce(runners.Values.FirstOrDefault(r => r.Pair.SteamAppId == wanted)?.Pair, "steamidle", $"Steam presence for app {other} is already running in Achievement Labs · not idling this game. Stop it to let the Mirror idle.");
                return;
            }
            if (idleStartedByMirror != 0 && host.SteamIdleAppId == idleStartedByMirror)
            {
                host.StopSteamIdle();
                Log(runners.Values.FirstOrDefault(r => r.Pair.SteamAppId == idleStartedByMirror)?.Pair, "Steam idling stopped.");
            }
            idleStartedByMirror = 0;
            if (wanted != 0)
            {
                await host.StartSteamIdleAsync(wanted, token);
                idleStartedByMirror = wanted;
                Log(runners.Values.FirstOrDefault(r => r.Pair.SteamAppId == wanted)?.Pair, $"{why} · idling the game on Steam.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Dispatcher.UIThread.Post(() => Status = "Steam idling couldn't be updated: " + error.Message); }
    }

    private readonly ConcurrentDictionary<(Guid, string), string> loggedOnce = new();

    /// <summary>Logs a recurring condition only when its message changes.</summary>
    private void LogOnce(MirrorPair? pair, string key, string message)
    {
        if (pair == null || loggedOnce.TryGetValue((pair.Id, key), out var previous) && previous == message) return;
        loggedOnce[(pair.Id, key)] = message;
        Log(pair, message);
    }

    private void Log(MirrorPair? pair, string message, bool isError = false)
    {
        if (pair == null) return;
        var entry = new MirrorLogEntry(DateTimeOffset.Now, message, isError);
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
            ApplyUnlockStates(pair);
        }
        LogEntries.Clear();
        if (SelectedRunner is { } runner) foreach (var entry in runner.Log.Reverse()) LogEntries.Add(entry);
        OnPropertyChanged(nameof(MatchSummary));
    }

    /// <summary>Stores which achievements are unlocked on one platform and refreshes the status column.</summary>
    private void UpdateUnlockStates(MirrorPair pair, MirrorPlatform platform, IReadOnlyList<MirrorAchievement> list)
    {
        var unlocked = list.Where(a => a.Unlocked).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Dispatcher.UIThread.Post(() =>
        {
            var current = platform == MirrorPlatform.Steam ? pair.SteamList : pair.XboxList;
            var next = current.Count == 0 ? list.Select(a => new MirrorNamedId(a.Id, a.Name, a.Unlocked)).ToList()
                : current.Select(a => a with { Unlocked = unlocked.Contains(a.Id) }).ToList();
            if (current.Count > 0 && current.SequenceEqual(next)) return;
            if (platform == MirrorPlatform.Steam) pair.SteamList = next; else pair.XboxList = next;
            Save();
            if (SelectedPair == pair) ApplyUnlockStates(pair);
        });
    }

    private void MarkUnlocked(MirrorPair pair, MirrorPlatform platform, string id)
    {
        var list = platform == MirrorPlatform.Steam ? pair.SteamList : pair.XboxList;
        var next = list.Select(a => a.Id == id ? a with { Unlocked = true } : a).ToList();
        if (platform == MirrorPlatform.Steam) pair.SteamList = next; else pair.XboxList = next;
        Save();
        if (SelectedPair == pair) ApplyUnlockStates(pair);
    }

    /// <summary>Reads the target platform once so the status column shows what is already unlocked there.</summary>
    private async Task RefreshTargetStatesAsync(MirrorPair pair)
    {
        try
        {
            var list = pair.Target == MirrorPlatform.Xbox
                ? (await host.ReadXboxAsync(pair.XboxTitleId, lifetime.Token)).Achievements
                : await host.ReadSteamLocalAsync(pair.SteamAppId, lifetime.Token);
            UpdateUnlockStates(pair, pair.Target, list);
        }
        catch { /* Status only; mirroring verifies the target itself. */ }
    }

    private void ApplyUnlockStates(MirrorPair pair)
    {
        var steam = pair.SteamList.Where(a => a.Unlocked).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var xbox = pair.XboxList.Where(a => a.Unlocked).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            row.SteamUnlocked = row.Match.SteamId.Length > 0 && steam.Contains(row.Match.SteamId);
            row.XboxUnlocked = !string.IsNullOrEmpty(row.Match.XboxId) && xbox.Contains(row.Match.XboxId);
        }
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

    /// <summary>Re-inserts the pair so the picker shows its new name after a scan.</summary>
    private void RefreshPairName(MirrorPair pair)
    {
        var index = Pairs.IndexOf(pair);
        if (index < 0) return;
        refreshingPairs = true;
        try { Pairs.RemoveAt(index); Pairs.Insert(index, pair); SelectedPair = pair; }
        finally { refreshingPairs = false; }
    }

    private void Save()
    {
        try { MirrorStore.SavePairs(Pairs); }
        catch { Status = "Mirror pairs could not be saved."; }
    }

    public void Dispose() { lifetime.Cancel(); StopAll(); statsWatcher?.Dispose(); }

    private sealed class XboxTarget(IMirrorHost host, string titleId, Func<CancellationToken, Task> ensurePresence) : IMirrorTarget
    {
        public async Task<IReadOnlyList<MirrorAchievement>> ReadAsync(CancellationToken cancellationToken) => (await host.ReadXboxAsync(titleId, cancellationToken)).Achievements;
        public async Task UnlockAsync(string achievementId, CancellationToken cancellationToken)
        {
            await ensurePresence(cancellationToken);
            await host.UnlockXboxAsync(titleId, achievementId, cancellationToken);
        }
    }

    private sealed class SteamTarget(IMirrorHost host, long appId) : IMirrorTarget
    {
        public Task<IReadOnlyList<MirrorAchievement>> ReadAsync(CancellationToken cancellationToken) => host.ReadSteamLocalAsync(appId, cancellationToken);
        public Task UnlockAsync(string achievementId, CancellationToken cancellationToken) => host.UnlockSteamAsync(appId, achievementId, cancellationToken);
    }
}
