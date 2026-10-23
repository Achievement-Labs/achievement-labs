using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AchievementLabs.Models;


namespace AchievementLabs.Desktop.Workflows
{
    public partial class AutoUnlockerViewModel : ObservableObject, INativePageLifecycle
    {
        private readonly NativeAccountContext account;
        public AchievementLabs.Core.EventCatalogClient? EventCatalog { get; set; }
        private readonly NativeNotices _snackbarService;
        private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
        private XboxApiClient? _xboxRestAPI;
        private string _lastKnownXauth = "";

        private CancellationTokenSource? _cancellationTokenSource;
        private AutoUnlockState? _state;
        private long lastPresenceHeartbeat;
        public string StatePath { get; set; } = AutoUnlockState.GetSavePath();
        public AutoUnlockState? PreparedState => _state;
        /// <summary>
        /// Achievement Mirror hook, awaited after each successful unlock (title ID, achievement ID, name).
        /// Returning false means the unlock couldn't be mirrored, and the queue stops before the next one.
        /// </summary>
        public Func<string, string, string, CancellationToken, Task<bool>>? MirrorUnlockAsync { get; set; }
        private string mirrorFailure = "";
        public bool HadFailure { get; private set; }
        public bool PresenceCleanupSucceeded { get; private set; } = true;
        public bool QueueReady { get; private set; }
        public bool ContinueAfterFailure { get; set; } = true;
        public event Action<bool>? WaitingForToken;

        private void SaveState()
        {
            if (_state == null) return;
            _state.LastSaved = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StatePath))!);
            var temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(_state, Formatting.Indented));
            File.Move(temporary, StatePath, true);
            SaveGameQueueSnapshot(_state);
        }

        private AutoUnlockState? LoadState() => File.Exists(StatePath)
            ? JsonConvert.DeserializeObject<AutoUnlockState>(File.ReadAllText(StatePath)) : null;

        public async Task<AchievementLabs.Core.BuildQueueItemStatus> RunPreparedAsync(CancellationToken token)
        {
            if (IsRunning) throw new InvalidOperationException("This title is already running.");
            _state = LoadState() ?? throw new InvalidDataException("This game's saved progress is missing.");
            if (_state.OwnerXuid != account.XUIDOnly) throw new InvalidOperationException("The saved queue belongs to another account. Rebuild it for this account.");
            if (_state.Queue == null || _state.CurrentIndex < 0 || _state.CurrentIndex > _state.Queue.Count
                || !double.IsFinite(_state.SpeedMultiplier) || _state.SpeedMultiplier <= 0
                || !double.IsFinite(_state.RemainingDelaySeconds) || _state.RemainingDelaySeconds < 0
                || _state.Queue.Any(i => i.EventRequestIndex < 0 || !double.IsFinite(i.DelaySeconds) || i.DelaySeconds < 0))
                throw new InvalidDataException("This game's checkpoint is invalid. Rebuild the game queue.");
            GameName = _state.GameName; TitleId = _state.TitleId;
            SpeedMultiplier = _state.SpeedMultiplier;
            PopulateQueueDisplay(); UpdateProgressText();
            await StartAutoUnlockProcess(token);
            if (mirrorFailure.Length > 0)
                throw new AchievementLabs.Core.BuildQueueHandoffException(mirrorFailure + " The build queue is paused.");
            if (!PresenceCleanupSucceeded)
                throw new AchievementLabs.Core.BuildQueueHandoffException("The previous title's presence could not be stopped. The build queue is paused; check the Xbox connection before resuming.");
            return token.IsCancellationRequested ? AchievementLabs.Core.BuildQueueItemStatus.Paused
                : HadFailure ? AchievementLabs.Core.BuildQueueItemStatus.Failed
                : _state.Queue.All(i => i.Completed) ? AchievementLabs.Core.BuildQueueItemStatus.Completed
                : AchievementLabs.Core.BuildQueueItemStatus.Paused;
        }

        public void Stop() => _cancellationTokenSource?.Cancel();

        /// <summary>
        /// Gets the current XboxApiClient instance, recreating it if the XAUTH token has changed.
        /// This ensures the auto unlocker keeps working when the token refreshes.
        /// </summary>
        private XboxApiClient GetRestAPI()
        {
            if (_xboxRestAPI == null || account.XAUTH != _lastKnownXauth)
            {
                _xboxRestAPI?.Dispose();
                _lastKnownXauth = account.XAUTH;
                _xboxRestAPI = new XboxApiClient(account.XAUTH);
            }
            return _xboxRestAPI;
        }

        public void ReleaseClient()
        {
            if (IsRunning) return;
            _xboxRestAPI?.Dispose();
            _xboxRestAPI = null;
            _lastKnownXauth = "";
        }

        public AutoUnlockerViewModel(NativeNotices snackbarService, NativeAccountContext account)
        {
            this.account = account;
            _snackbarService = snackbarService;
        }

        #region Observable Properties

        // Desktop supplies the same connected-session sender used by the achievement tab.
        public Func<string, string, CancellationToken, Task<bool>>? SendEventAchievementAsync { get; set; }
        public Func<string, CancellationToken, Task<AchievementsResponse?>>? ReadQueueAchievementsAsync { get; set; }
        public static bool UsesEvents(IEnumerable<OneCoreAchievementResponse> achievements) =>
            achievements.Any(a => a.progression?.requirements?.Any(r =>
                !string.IsNullOrWhiteSpace(r.id) && r.id != Guid.Empty.ToString()) == true);

        public void ValidateQueueMetadata(AchievementsResponse response)
        {
            if (_state == null) return;
            if (response.achievements.Count == 0)
                throw new InvalidOperationException("Could not verify the saved queue's achievement metadata. Refresh the game and try again.");
            _state.IsEventBased = UsesEvents(response.achievements);
            var scid = response.achievements.Select(a => a.serviceConfigId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id) && id != Guid.Empty.ToString());
            if (scid != null) _state.ServiceConfigId = scid;
        }

        public async Task PrepareEventMappingsAsync(CancellationToken ct)
        {
            if (_state?.IsEventBased != true) return;
            if (EventCatalog == null) throw new InvalidOperationException("Event catalog is unavailable.");
            // Mapping shape is process-local, not part of the saved queue. Load it after every restart.
            await EventCatalog.GetTitleAsync(_state.TitleId, ct);
        }

        public Func<CancellationToken, Task<bool>> RefreshEventTokenAsync { get; set; } = _ => Task.FromResult(false);
        [ObservableProperty] private bool _refreshTokenOnFailure = false;
        public Action<string>? FailureNotification { get; set; }
        [ObservableProperty] private bool _notifyOnFailure = true;
        [ObservableProperty] private bool _stopOnFailure = false;

        [ObservableProperty] private bool _isInitialized = false;
        [ObservableProperty] private string _referenceGamertag = "";
        [ObservableProperty] private string _titleId = "";
        [ObservableProperty] private string _statusText = "Idle";
        [ObservableProperty] private string _startStopButtonText = "Start Auto Unlock";
        [ObservableProperty] private bool _isRunning = false;
        [ObservableProperty] private bool _isConfigEnabled = true;
        [ObservableProperty] private string _gameName = "Game: None";
        [ObservableProperty] private string _referenceInfo = "Reference User: None";
        [ObservableProperty] private string _progressText = "Progress: 0/0";
        [ObservableProperty] private string _nextUnlockText = "Next Unlock: N/A";
        [ObservableProperty] private string _timeRemainingText = "Time Until Next: N/A";
        [ObservableProperty] private string _completionRemainingText = "Time to completion: N/A";
        [ObservableProperty] private string _estimatedCompletionText = "Estimated finish: N/A";
        [ObservableProperty] private double _speedMultiplier = 1.0;
        [ObservableProperty] private string _speedDisplay = "Speed: 1.0x (Real Time)";
        [ObservableProperty] private ObservableCollection<AutoUnlockQueueDisplay> _queueItems = new();
        [ObservableProperty] private bool _hasExistingState = false;
        [ObservableProperty] private string _existingStateInfo = "";

        #endregion

        public class AutoUnlockQueueDisplay : ObservableObject
        {
            private decimal _delayMinutes;

            public int Index { get; set; }
            public string AchievementName { get; set; } = "";
            public string AchievementId { get; set; } = "";
            public int Gamerscore { get; set; }
            private bool canEditDelay;
            public bool CanEditDelay { get => canEditDelay; set => SetProperty(ref canEditDelay, value); }
            internal Action<int, decimal>? DelayChanged { get; set; }
            public decimal DelayMinutes
            {
                get => _delayMinutes;
                set
                {
                    var normalized = Math.Max(0, value);
                    if (_delayMinutes == normalized) return;
                    _delayMinutes = normalized;
                    DelayChanged?.Invoke(Index - 1, normalized);
                }
            }
            public string Status { get; set; } = "Pending";
            public string RowBackground => Status == "Unlocked" ? "#245C38" : "Transparent";
        }

        public Func<bool> ExternalPresenceActive { get; set; } = () => false;
        partial void OnIsRunningChanged(bool value)
        {
            foreach (var item in QueueItems) item.CanEditDelay = !value;
            UpdateCompletionEstimate();
        }
        public SemaphoreSlim PresenceGate { get; set; } = new(1, 1);
        public string ActiveTitleId => _state?.TitleId ?? TitleId;

        private async Task UpdatePresenceAsync(bool stop = false)
        {
            await PresenceGate.WaitAsync();
            try
            {
                if (ExternalPresenceActive()) return;
                if (stop)
                {
                    var response = await GetRestAPI().StopHeartbeatAsync(account.XUIDOnly);
                    if (response.StatusCode < 200 || response.StatusCode >= 300)
                        throw new InvalidOperationException($"Presence stop rejected: HTTP {response.StatusCode}.");
                }
                else
                {
                    var response = await GetRestAPI().SendHeartbeatAsync(account.XUIDOnly, ActiveTitleId);
                    if (response.StatusCode < 200 || response.StatusCode >= 300)
                        throw new InvalidOperationException($"Queue presence heartbeat: HTTP {response.StatusCode}");
                }
            }
            finally { PresenceGate.Release(); }
        }

        public void OnNavigatedTo()
        {
            RefreshSavedGameQueues();
            if (!IsInitialized && account.InitComplete)
                InitializeViewModel();
        }

        public void OnNavigatedFrom()
        {
        }

        private void InitializeViewModel()
        {
            IsInitialized = true;
            CheckForExistingState();
        }

        private void CheckForExistingState()
        {
            var state = LoadState();
            if (state != null && state.Queue.Count > 0 && state.CurrentIndex < state.Queue.Count)
            {
                HasExistingState = true;
                var completed = state.Queue.Count(q => q.Completed);
                ExistingStateInfo = $"Saved session: {state.GameName} ({state.ReferenceGamertag}) - {completed}/{state.Queue.Count} completed";
            }
            else
            {
                HasExistingState = false;
                ExistingStateInfo = "";
            }
        }

        [RelayCommand]
        public async Task ResumeExistingSession()
        {
            if (IsRunning) return;
            var state = LoadState();
            if (state == null)
            {
                _snackbarService.Show("Error", "No saved session found.", NoticeAppearance.Danger,
                    new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            _state = state;
            if (state.OwnerXuid != account.XUIDOnly) { StatusText = "Rebuild this saved queue for the connected account before resuming."; return; }
            RefreshTokenOnFailure = state.RefreshTokenOnFailure;
            NotifyOnFailure = state.NotifyOnFailure;
            StopOnFailure = state.StopOnFailure;
            ReferenceGamertag = state.ReferenceGamertag;
            TitleId = state.TitleId;
            GameName = $"Game: {state.GameName}";
            ReferenceInfo = $"Reference User: {state.ReferenceGamertag} ({state.ReferenceXuid})";
            SpeedMultiplier = state.SpeedMultiplier;
            UpdateSpeedDisplay();
            PopulateQueueDisplay();
            UpdateProgressText();

            _snackbarService.Show("Session Restored", $"Loaded saved queue for {state.GameName}",
                NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);

            await AchievementLabs.MultiSelect.QueueTools.LoadedOnly(this);
        }

        [RelayCommand]
        public Task ClearExistingSession()
        {
            if (IsRunning) return Task.CompletedTask;
            if (File.Exists(StatePath)) File.Delete(StatePath);
            HasExistingState = false;
            ExistingStateInfo = "";
            QueueItems.Clear();
            _state = null;
            StatusText = "Idle";
            GameName = "Game: None";
            ReferenceInfo = "Reference User: None";
            ProgressText = "Progress: 0/0";
            NextUnlockText = "Next Unlock: N/A";
            TimeRemainingText = "Time Until Next: N/A";
            UpdateCompletionEstimate();

            _snackbarService.Show("Session Cleared", "Saved auto unlock session has been deleted.",
                NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
            return Task.CompletedTask;
        }

        [RelayCommand]
        public async Task BuildQueue()
        {
            if (IsRunning) return;
            QueueReady = false;
            if (string.IsNullOrWhiteSpace(ReferenceGamertag) || string.IsNullOrWhiteSpace(TitleId))
            {
                _snackbarService.Show("Error", "Please enter both a reference gamertag and a title ID.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            StatusText = "Fetching reference user profile...";
            IsConfigEnabled = false;

            try
            {
                if (account.EnsureAuthorizationAsync != null) await account.EnsureAuthorizationAsync(CancellationToken.None);
                // Step 1: Resolve reference gamertag to XUID
                var profileData = await GetRestAPI().GetGamertagProfileAsync(ReferenceGamertag);
                if (profileData == null)
                {
                    _snackbarService.Show("Error", "Failed to fetch gamertag profile.",
                        NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                    StatusText = "Failed to resolve gamertag.";
                    IsConfigEnabled = true;
                    return;
                }

                var profileUser = profileData["profileUsers"]?.FirstOrDefault();
                if (profileUser == null)
                {
                    _snackbarService.Show("Error", "No profile found for that gamertag.",
                        NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                    StatusText = "Gamertag not found.";
                    IsConfigEnabled = true;
                    return;
                }

                var refXuid = profileUser["id"]?.ToString() ?? "";
                var refGamertagResolved = profileUser["settings"]?.FirstOrDefault(s => s["id"]?.ToString() == "Gamertag")?["value"]?.ToString() ?? ReferenceGamertag;

                ReferenceInfo = $"Reference User: {refGamertagResolved} ({refXuid})";
                StatusText = "Fetching reference user achievements...";

                // Step 2: Fetch reference user's achievements for this title
                var refAchievements = await GetRestAPI().GetAchievementsForTitleAsync(refXuid, TitleId);
                if (refAchievements == null || refAchievements.achievements.Count == 0)
                {
                    _snackbarService.Show("Error", "No achievements found for this title on the reference user.",
                        NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                    StatusText = "No achievements found.";
                    IsConfigEnabled = true;
                    return;
                }

                // Get service config ID from achievements
                var serviceConfigId = refAchievements.achievements[0].serviceConfigId;

                bool isEventBased = UsesEvents(refAchievements.achievements);

                HashSet<string> licensedIds = [];
                if (isEventBased)
                {
                    if (EventCatalog == null) throw new InvalidOperationException("The event template service is unavailable.");
                    var catalog = await EventCatalog.GetCatalogAsync(CancellationToken.None);
                    if (!catalog.TitleIds.Contains(TitleId) && !(catalog.TestingTitleIds ?? []).Contains(TitleId)) throw new InvalidOperationException("This event-based title is not supported.");
                    licensedIds = (await EventCatalog.GetTitleAsync(TitleId, CancellationToken.None)).AchievementIds.ToHashSet();
                }

                // Step 3: Get game info for display
                var gameInfo = await GetRestAPI().GetGameTitleAsync(account.XUIDOnly, TitleId);
                var gameName = gameInfo?.Titles?.FirstOrDefault()?.Name ?? $"Title {TitleId}";
                GameName = $"Game: {gameName}" + (isEventBased ? " [Event-Based]" : "");

                // Step 4: Filter only unlocked achievements from reference user, sorted by unlock time
                var refUnlocked = refAchievements.achievements
                    .Where(a => a.progressState == "Achieved" && !string.IsNullOrWhiteSpace(a.progression?.timeUnlocked))
                    .OrderBy(a =>
                    {
                        if (DateTime.TryParse(a.progression!.timeUnlocked, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                            return dt;
                        return DateTime.MaxValue;
                    })
                    .ToList();

                if (refUnlocked.Count == 0)
                {
                    _snackbarService.Show("Error", "Reference user has no unlocked achievements for this title.",
                        NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                    StatusText = "No unlocked achievements on reference user.";
                    IsConfigEnabled = true;
                    return;
                }

                // Step 5: Fetch current user's achievements to know which are already unlocked
                StatusText = "Fetching your achievements...";
                var myAchievements = await GetRestAPI().GetAchievementsForTitleAsync(account.XUIDOnly, TitleId);
                var myUnlockedIds = new HashSet<string>();
                if (myAchievements != null)
                {
                    foreach (var a in myAchievements.achievements.Where(a => a.progressState == "Achieved"))
                        myUnlockedIds.Add(a.id);
                }

                // Step 6: Build the queue with time deltas
                // For event-based games, load the event data to check which achievements are supported
                var queue = new List<AutoUnlockQueueEntry>();
                DateTime? previousUnlockTime = null;

                foreach (var achievement in refUnlocked)
                {
                    // Skip achievements already unlocked by current user
                    if (myUnlockedIds.Contains(achievement.id))
                        continue;

                    // For event-based games, skip achievements that don't have event data
                    if (isEventBased && !licensedIds.Contains(achievement.id)) continue;

                    DateTime unlockTime = DateTime.MinValue;
                    if (DateTime.TryParse(achievement.progression!.timeUnlocked, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        unlockTime = dt;

                    double delaySeconds = 0;
                    if (previousUnlockTime.HasValue && unlockTime > previousUnlockTime.Value)
                    {
                        var referenceDelay = (unlockTime - previousUnlockTime.Value).TotalSeconds;
                        delaySeconds = AddReferenceTimingVariation(referenceDelay);
                    }

                    // Parse gamerscore - check direct field first, then rewards list
                    int gamerscore = 0;
                    if (achievement.gamerscore != null)
                    {
                        if (achievement.gamerscore is long l) gamerscore = (int)l;
                        else if (achievement.gamerscore is int i) gamerscore = i;
                        else int.TryParse(achievement.gamerscore.ToString(), out gamerscore);
                    }
                    if (gamerscore == 0 && achievement.rewards != null)
                    {
                        var gsReward = achievement.rewards.FirstOrDefault(r =>
                            string.Equals(r.type, "Gamerscore", StringComparison.OrdinalIgnoreCase));
                        if (gsReward != null)
                            int.TryParse(gsReward.value, out gamerscore);
                    }

                    queue.Add(new AutoUnlockQueueEntry
                    {
                        AchievementId = achievement.id,
                        AchievementName = achievement.name,
                        Gamerscore = gamerscore,
                        DelaySeconds = delaySeconds,
                        Completed = false
                    });

                    previousUnlockTime = unlockTime;
                }

                if (queue.Count == 0)
                {
                    _snackbarService.Show("All Done", "All of the reference user's achievements are already unlocked on your account.",
                        NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
                    StatusText = "Nothing to unlock.";
                    IsConfigEnabled = true;
                    return;
                }

                // Step 7: Save state
                _state = new AutoUnlockState
                {
                    OwnerXuid = account.XUIDOnly,
                    ReferenceGamertag = refGamertagResolved,
                    ReferenceXuid = refXuid,
                    TitleId = TitleId,
                    GameName = gameName,
                    ServiceConfigId = serviceConfigId,
                    Queue = queue,
                    CurrentIndex = 0,
                    RemainingDelaySeconds = queue[0].DelaySeconds,
                    IsRunning = false,
                    RefreshTokenOnFailure = RefreshTokenOnFailure,
                    NotifyOnFailure = NotifyOnFailure,
                    StopOnFailure = StopOnFailure,
                    SpeedMultiplier = SpeedMultiplier,
                    UseFakeSignature = account.Settings.FakeSignatureEnabled,
                    IsEventBased = isEventBased
                };
                SaveState();

                PopulateQueueDisplay();
                UpdateProgressText();

                StatusText = $"Queue built: {queue.Count} achievements to unlock. Press Start to begin.";
                QueueReady = true;
                IsConfigEnabled = true;
                HasExistingState = true;
                ExistingStateInfo = $"Saved session: {gameName} ({refGamertagResolved}) - 0/{queue.Count} completed";

                _snackbarService.Show("Queue Built",
                    $"{queue.Count} achievements queued for auto unlock.",
                    NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                IsConfigEnabled = true;
                _snackbarService.Show("Error", $"Failed to build queue: {ex.Message}",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
            }
        }

        [RelayCommand]
        public async Task StartStopAutoUnlock()
        {
            if (IsRunning)
            {
                // Stop
                _cancellationTokenSource?.Cancel();
                StatusText = "Stopping…";
                return;
            }

            if (_state == null)
            {
                _snackbarService.Show("Error", "Build a queue first before starting.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            await StartAutoUnlockProcess();
        }

        public Action<string>? CompletionNotification { get; set; }
        public void StopForSpoofFailure(string reason)
        {
            if (!IsRunning) return;
            _cancellationTokenSource?.Cancel();
            StatusText = $"Auto unlock stopped because spoofing failed: {reason}. Click Start to resume.";
            if (_state != null) { _state.IsRunning = false; SaveState(); }
        }
        public void NotifyQueueCompletion()
        {
            if (_state == null || _state.Queue.Count == 0 || _state.CurrentIndex < _state.Queue.Count
                || _cancellationTokenSource?.IsCancellationRequested == true) return;
            var failed = _state.Queue.Count(e => !e.Completed);
            CompletionNotification?.Invoke($"{_state.GameName}: {_state.Queue.Count - failed}/{_state.Queue.Count} achievements unlocked"
                + (failed == 0 ? "." : $"; {failed} failed."));
        }
        private async Task StartAutoUnlockProcess(CancellationToken cancellationToken = default)
        {
            if (_state == null || IsRunning) return;
            if (_state.OwnerXuid != account.XUIDOnly) { StatusText = "Rebuild this queue for the connected account."; return; }
            HadFailure = false;
            mirrorFailure = "";
            PresenceCleanupSucceeded = true;

            _state.RefreshTokenOnFailure = RefreshTokenOnFailure;
            _state.NotifyOnFailure = NotifyOnFailure;
            _state.StopOnFailure = StopOnFailure;
            _state.SpeedMultiplier = SpeedMultiplier;
            IsRunning = true;
            IsConfigEnabled = false;
            StartStopButtonText = "Stop Auto Unlock";
            _state.IsRunning = true;

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cancellationTokenSource.Token;

            try
            {
                // Recheck old saved queues before sending; older versions checked only the first requirement.
                var metadata = ReadQueueAchievementsAsync != null
                    ? await ReadQueueAchievementsAsync(_state.TitleId, token)
                    : await GetRestAPI().GetAchievementsForTitleAsync(account.XUIDOnly, _state.TitleId);
                if (metadata == null) throw new InvalidOperationException("Unable to verify queue achievement metadata.");
                ValidateQueueMetadata(metadata);
                await PrepareEventMappingsAsync(token);
                SaveState();
                await WaitForCredentialsAsync(_state.IsEventBased, token);
                account.SpoofedTitleID = _state.TitleId;
                account.SpoofingStatus = 1;
                await UpdatePresenceAsync();
                lastPresenceHeartbeat = Stopwatch.GetTimestamp();
                await RunAutoUnlockLoop(token);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopped
            }
            catch (Exception ex)
            {
                HadFailure = true;
                StatusText = $"Error: {ex.Message}";
                _snackbarService.Show("Error", $"Auto unlock error: {ex.Message}",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
            }
            finally
            {
                // Stop spoofing
                try
                {
                    if (account.SpoofingStatus == 1)
                    {
                        await UpdatePresenceAsync(stop: true);
                    }
                }
                catch { PresenceCleanupSucceeded = false; StatusText = "Presence stop failed; the service may retain presence until it expires."; }
                account.SpoofingStatus = 0;
                account.SpoofedTitleID = "0";
                lastPresenceHeartbeat = 0;

                IsRunning = false;
                IsConfigEnabled = true;
                StartStopButtonText = "Start Auto Unlock";
                _cancellationTokenSource.Dispose(); _cancellationTokenSource = null;
                WaitingForToken?.Invoke(false);
                if (_state != null)
                {
                    _state.IsRunning = false;
                    SaveState();
                }
            }
        }

        private async Task RunAutoUnlockLoop(CancellationToken token)
        {
            while (_state != null && _state.CurrentIndex < _state.Queue.Count)
            {
                token.ThrowIfCancellationRequested();
                await EnsurePresenceHeartbeatAsync(token);

                var entry = _state.Queue[_state.CurrentIndex];

                if (entry.Completed)
                {
                    _state.CurrentIndex++;
                    SaveState();
                    continue;
                }

                // Calculate adjusted delay
                double adjustedDelay = _state.RemainingDelaySeconds / (_state.SpeedMultiplier > 0 ? _state.SpeedMultiplier : 1.0);

                // Update UI
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    NextUnlockText = $"Next Unlock: {entry.AchievementName} ({entry.Gamerscore}G)";
                    UpdateProgressText();
                });

                // Wait with countdown
                if (adjustedDelay > 0)
                {
                    var delayEnd = DateTime.UtcNow.AddSeconds(adjustedDelay);

                    while (DateTime.UtcNow < delayEnd)
                    {
                        token.ThrowIfCancellationRequested();

                        var remaining = delayEnd - DateTime.UtcNow;
                        _state.RemainingDelaySeconds = Math.Max(0, remaining.TotalSeconds) * (_state.SpeedMultiplier > 0 ? _state.SpeedMultiplier : 1.0);
                        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                        {
                            UpdateCompletionEstimate();
                            TimeRemainingText = $"Time Until Next: {remaining:hh\\:mm\\:ss}";
                            StatusText = $"Waiting to unlock: {entry.AchievementName}...";
                        });

                        // Save remaining delay periodically (every 10s) for resume capability
                        if ((int)remaining.TotalSeconds % 10 == 0)
                        {
                            SaveState();
                        }

                        await EnsurePresenceHeartbeatAsync(token);

                        await Task.Delay(1000, token);
                    }
                }

                token.ThrowIfCancellationRequested();
                _state.RemainingDelaySeconds = 0;
                // Unlock the achievement
                await WaitForCredentialsAsync(_state.IsEventBased, token);
                token.ThrowIfCancellationRequested();
                _state.RemainingDelaySeconds = 0;
                SaveState();
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    UpdateCompletionEstimate();
                    StatusText = $"Unlocking: {entry.AchievementName}...";
                    TimeRemainingText = "Time Until Next: Unlocking...";
                });

                bool unlockSuccess = false;
                string unlockError = "";

                try
                {
                    if (_state.IsEventBased)
                    {
                        unlockSuccess = await UnlockEventBasedAchievementAsync(entry, token);
                        if (!unlockSuccess)
                            unlockError = "Event-based unlock failed (see snackbar for details)";
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(_state.ServiceConfigId) || _state.ServiceConfigId == Guid.Empty.ToString())
                            throw new InvalidDataException("This title is missing its service configuration. Rebuild the queue.");
                        await GetRestAPI().UnlockTitleBasedAchievementAsync(
                            _state.ServiceConfigId,
                            _state.TitleId,
                            account.XUIDOnly,
                            entry.AchievementId,
                            _state.UseFakeSignature);
                        unlockSuccess = true;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    unlockError = ex.Message;
                }

                if (unlockSuccess)
                {
                    entry.Completed = true;
                    _state.CurrentIndex++;

                    // Set remaining delay for next item
                    if (_state.CurrentIndex < _state.Queue.Count)
                    {
                        _state.RemainingDelaySeconds = _state.Queue[_state.CurrentIndex].DelaySeconds;
                    }

                    SaveState();

                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        UpdateProgressText();
                        UpdateQueueItemStatus(_state.CurrentIndex - 1, "Unlocked");
                        _snackbarService.Show("Achievement Unlocked",
                            $"{entry.AchievementName} ({entry.Gamerscore}G) has been unlocked.",
                            NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
                    });

                    if (MirrorUnlockAsync != null && !await MirrorUnlockAsync(_state.TitleId, entry.AchievementId, entry.AchievementName, token))
                    {
                        // Never race ahead on Xbox when the matching Steam unlock didn't happen.
                        mirrorFailure = $"Achievement Mirror couldn't copy {entry.AchievementName} to Steam, so Auto Unlock stopped.";
                        HadFailure = true;
                        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                        {
                            StatusText = mirrorFailure + " Resolve it in Achievement Mirror, then click Start to continue.";
                            FailureNotification?.Invoke(mirrorFailure);
                        });
                        _state.IsRunning = false; SaveState();
                        return;
                    }
                }
                else
                {
                    bool stop = false;
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => stop = HandleUnlockFailure(entry, unlockError));
                    HadFailure = true;
                    if (stop || !ContinueAfterFailure) { _state.IsRunning = false; SaveState(); return; }
                    _state.CurrentIndex++;
                    _state.RemainingDelaySeconds = _state.CurrentIndex < _state.Queue.Count ? _state.Queue[_state.CurrentIndex].DelaySeconds : 0;
                    SaveState();
                }
            }

            token.ThrowIfCancellationRequested();
            // All done
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                var failures = _state?.Queue.Count(e => !e.Completed) ?? 0;
                StatusText = failures == 0 ? "Auto unlock complete!" : $"Queue finished with {failures} failed achievement(s).";
                NextUnlockText = "Next Unlock: N/A";
                TimeRemainingText = "Time Until Next: N/A";
                UpdateProgressText();
                NotifyQueueCompletion();
                _snackbarService.Show("Complete",
                    "All queued achievements have been processed.",
                    NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
            });
        }

        public bool HandleUnlockFailure(AutoUnlockQueueEntry entry, string error)
        {
            var message = $"Failed to unlock {entry.AchievementName}: {error}";
            StatusText = StopOnFailure ? message + " Queue stopped. Click Start to retry this achievement." : message;
            if (_state != null) UpdateQueueItemStatus(_state.CurrentIndex, "Failed");
            if (NotifyOnFailure)
            {
                _snackbarService.Show("Unlock Failed", message, NoticeAppearance.Danger,
                    new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                FailureNotification?.Invoke(message);
            }
            return StopOnFailure;
        }

        [RelayCommand]
        public void SaveQueue()
        {
            if (IsRunning || !IsConfigEnabled) return;
            if (_state == null) { StatusText = "Build or load a queue before saving."; return; }
            try
            {
                _state.SpeedMultiplier = SpeedMultiplier;
                _state.RefreshTokenOnFailure = RefreshTokenOnFailure;
                _state.NotifyOnFailure = NotifyOnFailure;
                _state.StopOnFailure = StopOnFailure;
                _state.IsRunning = false;
                SaveState();
                StatusText = $"Queue saved for {_state.GameName}, including custom delays and failure settings.";
            }
            catch { StatusText = "Could not save the queue. Your edits are still available here."; }
        }

        private void PopulateQueueDisplay()
        {
            if (_state == null) return;

            var items = new ObservableCollection<AutoUnlockQueueDisplay>();
            for (int i = 0; i < _state.Queue.Count; i++)
            {
                var entry = _state.Queue[i];
                var item = new AutoUnlockQueueDisplay
                {
                    Index = i + 1,
                    AchievementName = entry.AchievementName,
                    AchievementId = entry.AchievementId,
                    Gamerscore = entry.Gamerscore,
                    CanEditDelay = !IsRunning,
                    DelayMinutes = (decimal)(entry.DelaySeconds / 60.0),
                    Status = entry.Completed ? "Unlocked" : (i < _state.CurrentIndex ? "Skipped" : "Pending")
                };
                item.DelayChanged = UpdateQueueDelay;
                items.Add(item);
            }
            QueueItems = items;
        }

        private void UpdateProgressText()
        {
            UpdateCompletionEstimate();
            if (_state == null)
            {
                ProgressText = "Progress: 0/0";
                return;
            }

            var completed = _state.Queue.Count(q => q.Completed);
            ProgressText = $"Progress: {completed}/{_state.Queue.Count}";
        }

        public static TimeSpan EstimateRemaining(AutoUnlockState state, double speed)
        {
            var seconds = 0.0;
            for (var i = state.CurrentIndex; i < state.Queue.Count; i++)
                if (!state.Queue[i].Completed)
                    seconds += Math.Max(0, i == state.CurrentIndex ? state.RemainingDelaySeconds : state.Queue[i].DelaySeconds);
            return TimeSpan.FromSeconds(seconds / (double.IsFinite(speed) && speed > 0 ? speed : 1));
        }

        private void UpdateCompletionEstimate()
        {
            if (_state == null) { CompletionRemainingText = "Time to completion: N/A"; EstimatedCompletionText = "Estimated finish: N/A"; return; }
            var remaining = EstimateRemaining(_state, IsRunning ? _state.SpeedMultiplier : SpeedMultiplier);
            CompletionRemainingText = $"Time to completion: {remaining.Days}d {remaining:hh\\:mm\\:ss}";
            EstimatedCompletionText = _state.CurrentIndex >= _state.Queue.Count
                ? "Queue finished"
                : $"Estimated finish{(IsRunning ? "" : " if started now")}: {DateTimeOffset.UtcNow.Add(remaining).ToLocalTime():dddd, dd MMM yyyy HH:mm:ss zzz} (local time; request time may add delay)";
        }

        private void UpdateQueueItemStatus(int index, string status)
        {
            if (index >= 0 && index < QueueItems.Count)
            {
                // We need to replace the item since the class isn't observable
                var item = QueueItems[index];
                QueueItems[index] = new AutoUnlockQueueDisplay
                {
                    Index = item.Index,
                    AchievementName = item.AchievementName,
                    AchievementId = item.AchievementId,
                    Gamerscore = item.Gamerscore,
                    CanEditDelay = item.CanEditDelay,
                    DelayMinutes = item.DelayMinutes,
                    DelayChanged = UpdateQueueDelay,
                    Status = status
                };
            }
        }

        private static double AddReferenceTimingVariation(double referenceDelaySeconds)
        {
            if (referenceDelaySeconds <= 0) return 0;

            var maximumVariationSeconds = referenceDelaySeconds >= TimeSpan.FromHours(1).TotalSeconds
                ? TimeSpan.FromMinutes(5).TotalSeconds
                : TimeSpan.FromMinutes(1).TotalSeconds;
            var signedVariation = ((Random.Shared.NextDouble() * 2.0) - 1.0) * maximumVariationSeconds;
            return Math.Max(1, referenceDelaySeconds + signedVariation);
        }

        private void UpdateQueueDelay(int queueIndex, decimal delayMinutes)
        {
            if (_state == null || IsRunning || queueIndex < 0 || queueIndex >= _state.Queue.Count) return;

            var delaySeconds = (double)delayMinutes * 60.0;
            _state.Queue[queueIndex].DelaySeconds = delaySeconds;
            if (_state.CurrentIndex == queueIndex)
                _state.RemainingDelaySeconds = delaySeconds;
            SaveState();
            UpdateCompletionEstimate();
        }

        public void UpdateSpeed(double newSpeed)
        {
            SpeedMultiplier = newSpeed;
            if (_state != null)
            {
                _state.SpeedMultiplier = newSpeed;
                SaveState();
            }
            UpdateSpeedDisplay();
            UpdateCompletionEstimate();
        }

        partial void OnSpeedMultiplierChanged(double value)
        {
            if (!IsRunning && _state != null) { _state.SpeedMultiplier = value; SaveState(); }
            UpdateSpeedDisplay();
            UpdateCompletionEstimate();
        }

        private void UpdateSpeedDisplay()
        {
            var label = SpeedMultiplier switch
            {
                1.0 => "Real Time",
                _ when SpeedMultiplier < 1.0 => $"{1.0 / SpeedMultiplier:F1}x Slower",
                _ => $"{SpeedMultiplier:F1}x Faster"
            };
            SpeedDisplay = $"Speed: {SpeedMultiplier:F1}x ({label})";
        }

        #region Event-Based Unlock

        /// <summary>
        /// Unlocks a single event-based achievement by building and sending telemetry request(s).
        /// Returns true if all requests succeeded.
        /// </summary>
        private async Task<bool> UnlockEventBasedAchievementAsync(AutoUnlockQueueEntry entry, CancellationToken token)
        {
            if (_state == null) return false;

            if (EventCatalog == null) return false;
            var requestBodies = (await EventCatalog.GetPayloadsAsync(_state.TitleId, entry.AchievementId, account.XUIDOnly, token)).Payloads.ToList();
            if (requestBodies.Count == 0) return false;
            if (entry.EventRequestIndex > requestBodies.Count) throw new InvalidDataException("The event checkpoint no longer matches this achievement's payloads.");

            // Send all request(s)
            for (int reqIdx = entry.EventRequestIndex; reqIdx < requestBodies.Count; reqIdx++)
            {
                await WaitForCredentialsAsync(true, token);
                token.ThrowIfCancellationRequested();
                using var content = new StringContent(requestBodies[reqIdx], Encoding.UTF8, "application/x-json-stream");
                var (statusCode, responseBody) = await GetRestAPI().UnlockEventBasedAchievement(account.EventsToken, content);

                if (statusCode is 401 or 403 && account.EventTokenRejected != null)
                {
                    account.EventTokenRejected();
                    await WaitForCredentialsAsync(true, token);
                    reqIdx--; // Retry only the explicitly rejected request, after replacement.
                    continue;
                }

                if (statusCode < 200 || statusCode >= 300)
                {
                    throw new HttpRequestException($"Event request {reqIdx + 1}/{requestBodies.Count} failed: HTTP {statusCode}.", null, (System.Net.HttpStatusCode)statusCode);
                }
                entry.EventRequestIndex = reqIdx + 1;
                SaveState();
            }

            return true;
        }

        private async Task WaitForCredentialsAsync(bool needsEvents, CancellationToken token)
        {
            var waiting = false;
            var refreshAttempted = false;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (account.EnsureAuthorizationAsync != null) await account.EnsureAuthorizationAsync(token);
                        if (needsEvents && account.EnsureEventTokenAsync != null) await account.EnsureEventTokenAsync(token);
                        await EnsurePresenceHeartbeatAsync(token);
                        return;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch
                    {
                        if (needsEvents && RefreshTokenOnFailure && !refreshAttempted)
                        {
                            refreshAttempted = true;
                            if (await RefreshEventTokenAsync(token)) continue;
                        }
                        if (!waiting) { WaitingForToken?.Invoke(true); waiting = true; }
                        StatusText = "Waiting for a current token. Paste and save a token in Settings, or Stop to pause.";
                        // Missing event authorization need not end otherwise valid title presence.
                        try { await EnsurePresenceHeartbeatAsync(token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch { /* Resume heartbeat maintenance when authorization/network recovers. */ }
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                    }
                }
            }
            finally { if (waiting) WaitingForToken?.Invoke(false); }
        }

        private async Task EnsurePresenceHeartbeatAsync(CancellationToken token)
        {
            if (_state == null || account.SpoofingStatus != 1 || lastPresenceHeartbeat == 0
                || Stopwatch.GetElapsedTime(lastPresenceHeartbeat) < TimeSpan.FromMinutes(5)) return;
            if (account.EnsureAuthorizationAsync != null) await account.EnsureAuthorizationAsync(token);
            token.ThrowIfCancellationRequested();
            await UpdatePresenceAsync();
            lastPresenceHeartbeat = Stopwatch.GetTimestamp();
        }

        private bool ApplyReplacement(ref string requestbody, dynamic ReplacementData)
        {
            switch (ReplacementData.ReplacementType.ToString())
            {
                case "Replace":
                    requestbody = requestbody.Replace(ReplacementData.Target.ToString(), ReplacementData.Replacement.ToString());
                    return true;
                case "RangeInt":
                    {
                        int min = ReplacementData.Min;
                        int max = ReplacementData.Max;
                        Random random = new Random();
                        int randomint = random.Next(min, max);
                        requestbody = requestbody.Replace(ReplacementData.Target.ToString(), randomint.ToString());
                        return true;
                    }
                case "RangeFloat":
                    {
                        float min = ReplacementData.Min;
                        float max = ReplacementData.Max;
                        Random random = new Random();
                        float randomfloat = (float)random.NextDouble() * (max - min) + min;
                        requestbody = requestbody.Replace(ReplacementData.Target.ToString(), randomfloat.ToString());
                        return true;
                    }
                case "StupidFuckingLDAPTimestamp":
                    {
                        long ldapTimestamp = DateTime.Now.ToFileTime();
                        requestbody = requestbody.Replace(ReplacementData.Target.ToString(), ldapTimestamp.ToString());
                        return true;
                    }
                default:
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        _snackbarService.Show("Error: Bad Achievement Data",
                            "Unknown replacement type in event data",
                            NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                    });
                    return false;
            }
        }

        private string ApplyCommonReplacements(string requestbody, DateTime timestamp)
        {
            requestbody = requestbody.Replace("REPLACETIME", timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
            requestbody = requestbody.Replace("REPLACESEQ", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            requestbody = requestbody.Replace("REPLACEXUID", account.XUIDOnly);
            try
            {
                return JObject.Parse(requestbody).ToString(Formatting.None);
            }
            catch
            {
                return requestbody;
            }
        }

        #endregion
    }
}

