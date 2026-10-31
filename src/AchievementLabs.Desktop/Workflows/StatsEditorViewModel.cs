using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace AchievementLabs.Desktop.Workflows
{
    public partial class StatsEditorViewModel : ObservableObject, INativePageLifecycle
    {
        private readonly NativeAccountContext account;
        private readonly NativeNotices _snackbarService;
        private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
        private XboxApiClient? _xboxRestAPI;
        private string _lastKnownXauth = "";
        private static readonly Lazy<StatModeCatalog> StatCatalog = new(LoadStatModeCatalog);
        private readonly Func<string, XboxApiClient> clientFactory;
        private readonly bool allowRemoteCatalog;
        internal string? DebugRootDirectory { get; set; }
        private string loadedTitleId = "", loadedXuid = "";
        private string migratedStatsDirectory = "";
        private void EnsureStatsFileReady()
        {
            var directory = Path.GetFullPath(account.EventsDirectory);
            if (migratedStatsDirectory == directory) return;
            AchievementLabs.Core.StatCatalogStore.MigrateLegacy(directory);
            migratedStatsDirectory = directory;
        }

        public StatsEditorViewModel(NativeNotices snackbarService, NativeAccountContext account, Func<string, XboxApiClient>? clientFactory = null, bool allowRemoteCatalog = true)
        {
            this.account = account;
            _snackbarService = snackbarService;
            this.clientFactory = clientFactory ?? (authorization => new XboxApiClient(authorization));
            this.allowRemoteCatalog = allowRemoteCatalog;
        }

        private XboxApiClient GetRestAPI()
        {
            if (_xboxRestAPI == null || account.XAUTH != _lastKnownXauth)
            {
                _xboxRestAPI?.Dispose();
                _lastKnownXauth = account.XAUTH;
                _xboxRestAPI = clientFactory(account.XAUTH);
            }
            return _xboxRestAPI;
        }

        public void ReleaseClient()
        {
            if (IsBusy) return;
            _xboxRestAPI?.Dispose();
            _xboxRestAPI = null;
            _lastKnownXauth = "";
        }

        #region Observable Properties

        [ObservableProperty] private bool _isInitialized = false;
        [ObservableProperty] private string _titleId = "";
        [ObservableProperty] private string _statusText = "Enter a Title ID and click Load Stats.";
        [ObservableProperty] private string _gameName = "Game: None";
        [ObservableProperty] private bool _isLoading = false;
        [ObservableProperty] private bool _isUpdating;
        public bool IsBusy => IsLoading || IsUpdating;
        public bool CanEditTitle => !IsBusy;
        partial void OnIsLoadingChanged(bool value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanEditTitle)); }
        partial void OnIsUpdatingChanged(bool value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanEditTitle)); }
        partial void OnTitleIdChanged(string value)
        {
            loadedTitleId = loadedXuid = "";
            StatItems.Clear(); _eventsDataForTitle = null;
            HasEditableStats = HasEventData = HasEditableEventData = false;
            EventDataInfo = TitleMetadataInfo = "";
            StatusText = "Load stats for the selected title before editing.";
            RefreshSupport();
        }
        public string StatModeLabel { get; private set; } = "Stats type: Unknown";
        public string StatSupportLabel { get; private set; } = "Choose a title to check stat support.";
        private string CurrentStatMode => GetCatalogStatMode(TitleId) is var mode && mode != "Unknown" ? mode
            : _eventsDataForTitle?["StatsMode"]?.ToString() is "Event" or "EventBased" ? "Event"
            : _eventsDataForTitle?["StatsMode"]?.ToString() is "Title" or "TitleBased" ? "Title"
            : HasEditableEventData ? "Event" : "Unknown";
        private string? ReadStatTemplate()
        {
            var path = File.Exists(Path.Combine(account.EventsDirectory, "index.json"))
                ? Path.Combine(account.EventsDirectory, TitleId, TitleId + ".json") : Path.Combine(account.EventsDirectory, TitleId + ".json");
            if (File.Exists(path)) return File.ReadAllText(path);
            var assembly = typeof(AchievementLabs.Core.StatCatalogStore).Assembly;
            var indexed = assembly.GetManifestResourceNames().Contains("AchievementLabs.LocalEvents.index.json");
            var expected = "AchievementLabs.LocalEvents." + (indexed ? TitleId + "." : "") + TitleId + ".json";
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.Replace('/', '.').Replace('\\', '.') == expected);
            using var stream = resource == null ? null : assembly.GetManifestResourceStream(resource);
            if (stream == null) return null;
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        public void RefreshSupport()
        {
            if (!uint.TryParse(TitleId, out var id) || id == 0) { StatModeLabel = "Stats type: Unknown"; StatSupportLabel = "Enter a decimal Xbox Title ID."; }
            else
            {
                CheckEventData();
                var mode = CurrentStatMode;
                StatModeLabel = "Stats type: " + (mode == "Event" ? "Event-based" : mode == "Title" ? "Title-based" : "Unknown");
                StatSupportLabel = mode == "Title" ? "Supported · title-based stats (available writable stats are checked when loaded)."
                    : mode == "Event" && HasEditableEventData && ReadStatTemplate() != null ? "Supported · mapped event stats in Stats.json."
                    : mode == "Event" ? "Not Supported · Stats.json needs event-stat recipes and a standalone title template."
                    : "Not Supported · no known stat mode or mapped event-stat recipes.";
            }
            OnPropertyChanged(nameof(StatModeLabel)); OnPropertyChanged(nameof(StatSupportLabel));
        }
        [ObservableProperty] private bool _hasEventData = false;
        [ObservableProperty] private bool _hasEditableEventData = false;
        [ObservableProperty] private bool _hasEditableStats = false;
        [ObservableProperty] private string _eventDataInfo = "";
        [ObservableProperty] private string _titleMetadataInfo = "";
        [ObservableProperty] private ObservableCollection<StatDisplayItem> _statItems = new();

        #endregion

        public class StatDisplayItem : ObservableObject
        {
            private string _name = "";
            public string Name
            {
                get => _name;
                set
                {
                    if (SetProperty(ref _name, value))
                        OnPropertyChanged(nameof(DisplayLabel));
                }
            }

            private string _displayName = "";
            public string DisplayName
            {
                get => _displayName;
                set
                {
                    if (SetProperty(ref _displayName, value))
                        OnPropertyChanged(nameof(DisplayLabel));
                }
            }

            public string DisplayLabel =>
                !string.IsNullOrWhiteSpace(DisplayName) && !string.Equals(DisplayName, Name, StringComparison.OrdinalIgnoreCase)
                    ? $"{DisplayName} ({Name})"
                    : Name;

            private string _type = "";
            public string Type
            {
                get => _type;
                set => SetProperty(ref _type, value);
            }

            private string _currentValue = "";
            public string CurrentValue
            {
                get => _currentValue;
                set => SetProperty(ref _currentValue, value);
            }

            private string _newValue = "";
            public string NewValue
            {
                get => _newValue;
                set => SetProperty(ref _newValue, value);
            }

            private bool _isEditable = false;
            public bool IsEditable
            {
                get => _isEditable;
                set => SetProperty(ref _isEditable, value);
            }

            private string _editStatus = "";
            public string EditStatus
            {
                get => _editStatus;
                set => SetProperty(ref _editStatus, value);
            }

            public string? Scid { get; set; }
            public string? TitleId { get; set; }
            public string OwnerXuid { get; set; } = "";
            public bool IsTitleBasedStat { get; set; }
            public bool IsEventBasedStat { get; set; }
            public string Source { get; set; } = "";
            public string SuggestedValue { get; set; } = "";
            public string Confidence { get; set; } = "";
        }

        private sealed class RawTitleStat
        {
            public string Name { get; init; } = "";
            public string Type { get; init; } = "";
            public string Value { get; init; } = "";
            public string? Scid { get; init; }
            public string? TitleId { get; init; }
        }

        private sealed class TitleStatDefinition
        {
            public string Name { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string Type { get; init; } = "Integer";
            public string Source { get; init; } = "Local title stat catalog";
            public string Confidence { get; init; } = "Curated title-based stat definition";
            public string SuggestedValue { get; init; } = "";
        }

        private sealed class StatModeCatalog
        {
            public HashSet<string> EventBasedTitleIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> TitleBasedTitleIds { get; } = new(StringComparer.OrdinalIgnoreCase);

            public string GetMode(string titleId)
            {
                if (EventBasedTitleIds.Contains(titleId))
                    return "Event";

                if (TitleBasedTitleIds.Contains(titleId))
                    return "Title";

                return "Unknown";
            }
        }

        // Cached event data for the current title
        private JObject? _eventsDataForTitle;

        public void OnNavigatedTo()
        {
            if (!IsInitialized && account.InitComplete)
            {
                IsInitialized = true;
            }
        }

        public void OnNavigatedFrom() { }

        [RelayCommand]
        public async Task LoadStats()
        {
            if (IsBusy || !account.InitComplete) return;
            if (string.IsNullOrWhiteSpace(TitleId))
            {
                _snackbarService.Show("Error", "Please enter a Title ID.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            var normalizedTitleId = TitleId.Trim();
            if (!uint.TryParse(normalizedTitleId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTitleId) || parsedTitleId == 0)
            {
                StatusText = "Enter a valid decimal Xbox Title ID.";
                _snackbarService.Show("Invalid Title ID", StatusText,
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            TitleId = parsedTitleId.ToString(CultureInfo.InvariantCulture);
            loadedTitleId = loadedXuid = "";
            var loadingTitleId = TitleId;
            var loadingXuid = account.XUIDOnly;
            RefreshSupport();
            if (CurrentStatMode == "Event")
            {
                StatItems.Clear();
                GameName = $"Title ID: {TitleId}";
                if (HasEditableEventData && ReadStatTemplate() != null && _eventsDataForTitle?["Enabled"]?.Value<bool>() != false)
                {
                    foreach (var stat in ((JObject)_eventsDataForTitle!["Stats"]!).Properties())
                        StatItems.Add(new StatDisplayItem { Name = stat.Name, Type = (string?)stat.Value["Type"] ?? "Unknown", CurrentValue = "Not loaded",
                            IsEditable = IsEditableStatDefinition(stat.Value), IsEventBasedStat = true, TitleId = TitleId, OwnerXuid = loadingXuid,
                            Source = "Stats.json", EditStatus = IsEditableStatDefinition(stat.Value) ? "Mapped event stat" : "Not Supported" });
                    loadedTitleId = loadingTitleId; loadedXuid = loadingXuid;
                    HasEditableStats = StatItems.Any(s => s.IsEditable);
                    StatusText = $"Loaded {StatItems.Count} event-stat definitions from Stats.json. Current values are not confirmed.";
                }
                else { HasEditableStats = false; StatusText = StatSupportLabel; }
                return;
            }

            IsLoading = true;
            StatusText = "Loading game info...";
            HasEditableStats = false;
            StatItems.Clear();

            try
            {
                if (account.EnsureAuthorizationAsync != null) await account.EnsureAuthorizationAsync(CancellationToken.None);
                // Get game info
                var gameInfo = await GetRestAPI().GetGameTitleAsync(account.XUIDOnly, TitleId);
                var titleInfo = gameInfo?.Titles?.FirstOrDefault();
                var gameName = titleInfo?.Name ?? $"Title {TitleId}";
                GameName = $"Game: {gameName}";

                // Check for event data
                CheckEventData();
                var statCatalogMode = GetCatalogStatMode(TitleId);
                if (statCatalogMode == "Event")
                    TitleMetadataInfo = AppendMetadataPart(TitleMetadataInfo, "Stats mode: Event-based catalog");
                else if (statCatalogMode == "Title")
                    TitleMetadataInfo = AppendMetadataPart(TitleMetadataInfo, "Stats mode: Title-based catalog");

                StatusText = "Resolving title stats...";
                var serviceConfigId = await GetRestAPI().GetTitleServiceConfigIdAsync(account.XUIDOnly, TitleId);

                // Get stats — first try with MinutesPlayed to see what stat names exist
                StatusText = "Fetching stats...";

                // Build stat name list from event data if available
                var statNames = new List<string>();
                if (_eventsDataForTitle != null)
                {
                    var statsSection = _eventsDataForTitle["Stats"] as JObject;
                    if (statsSection != null)
                    {
                        foreach (var stat in statsSection)
                        {
                            if (!statNames.Contains(stat.Key))
                                statNames.Add(stat.Key);
                        }
                    }
                }

                GameStatsResponse? statsResponse = null;

                // Determine which stats are editable (have event data)
                var editableStatNames = new HashSet<string>();
                var metadataStatNames = new HashSet<string>();
                if (_eventsDataForTitle != null)
                {
                    var statsSection = _eventsDataForTitle["Stats"] as JObject;
                    if (statsSection != null)
                    {
                        foreach (var stat in statsSection)
                        {
                            metadataStatNames.Add(stat.Key);
                            if (IsEditableStatDefinition(stat.Value))
                                editableStatNames.Add(stat.Key);
                        }
                    }
                }

                StatusText = "Loading stat catalog...";
                var achievementMetadata = await GetRestAPI().GetAchievementsForTitleAsync(account.XUIDOnly, TitleId);
                if (string.IsNullOrWhiteSpace(serviceConfigId))
                {
                    serviceConfigId = achievementMetadata?.achievements?
                        .Select(achievement => achievement.serviceConfigId)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                }
                AddDerivedAchievementStats(achievementMetadata);
                var localTitleStatDefinitions = LoadLocalTitleStatDefinitions(TitleId, gameName);
                var localTitleStatDefinitionMap = localTitleStatDefinitions
                    .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                var titleStatsLoaded = 0;
                var titleHubStats = await GetRestAPI().GetTitleHubStatsRawAsync(account.XUIDOnly, TitleId);
                TitleMetadataInfo = AppendMetadataPart(TitleMetadataInfo, $"TitleHub stats HTTP {titleHubStats.StatusCode}");
                if (titleHubStats.StatusCode >= 200 && titleHubStats.StatusCode < 300)
                {
                    foreach (var stat in ExtractTitleHubVisibleStats(titleHubStats.Body, TitleId, serviceConfigId))
                    {
                        if (StatItems.Any(item => string.Equals(item.Name, stat.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var isEventEditable = statCatalogMode == "Event" && editableStatNames.Contains(stat.Name);
                        var isCatalogEventBased = statCatalogMode == "Event";
                        var isCatalogTitleBased = statCatalogMode == "Title";
                        var hasScid = !string.IsNullOrWhiteSpace(stat.Scid) || !string.IsNullOrWhiteSpace(serviceConfigId);
                        var isTitleEditable = statCatalogMode == "Title" && hasScid && !isEventEditable;
                        StatItems.Add(new StatDisplayItem
                        {
                            Name = stat.Name,
                            DisplayName = localTitleStatDefinitionMap.TryGetValue(stat.Name, out var definition)
                                ? definition.DisplayName
                                : "",
                            Type = string.IsNullOrWhiteSpace(stat.Type) ? InferStatType(stat.Value) : stat.Type,
                            CurrentValue = NormalizeStatValueForDisplay(stat.Value),
                            NewValue = "",
                            IsEditable = isEventEditable || isTitleEditable,
                            EditStatus = isEventEditable
                                ? "Editable (Events)"
                                : isTitleEditable
                                    ? "Editable (Title)"
                                    : isCatalogEventBased
                                        ? "Event payload needed"
                                        : isCatalogTitleBased
                                            ? "Title stat missing SCID"
                                            : "Read-Only (missing SCID)",
                            Source = "TitleHub profile stats",
                            SuggestedValue = GetSuggestedValue(stat.Name),
                            Confidence = "Profile-visible stat returned by TitleHub Stats decoration",
                            Scid = stat.Scid ?? serviceConfigId,
                            TitleId = stat.TitleId ?? TitleId,
                            IsTitleBasedStat = isTitleEditable,
                            IsEventBasedStat = isEventEditable || isCatalogEventBased
                        });
                        titleStatsLoaded++;
                    }
                }

                statNames = await BuildStatNameCatalogAsync(TitleId, gameName, metadataStatNames, achievementMetadata, localTitleStatDefinitions);
                statsResponse = statNames.Count > 0
                    ? await GetRestAPI().GetGameStatsAsync(account.XUIDOnly, TitleId, statNames)
                    : null;

                if (!string.IsNullOrWhiteSpace(serviceConfigId))
                {
                    TitleMetadataInfo = AppendMetadataPart(TitleMetadataInfo, $"SCID: {serviceConfigId}");
                    var rawStats = await GetRestAPI().GetAllGameStatsRawAsync(account.XUIDOnly, serviceConfigId);
                    if (rawStats.StatusCode >= 200 && rawStats.StatusCode < 300)
                    {
                        foreach (var stat in ExtractRawTitleStats(rawStats.Body, serviceConfigId))
                        {
                            if (StatItems.Any(item => string.Equals(item.Name, stat.Name, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var isEventEditable = statCatalogMode == "Event" && editableStatNames.Contains(stat.Name);
                            var isCatalogEventBased = statCatalogMode == "Event";
                            var isCatalogTitleBased = statCatalogMode == "Title";
                            var isTitleEditable = statCatalogMode == "Title";
                            var isEditable = isEventEditable || isTitleEditable;
                            var editStatus = isEventEditable
                                ? "Editable (Events)"
                                : isCatalogEventBased
                                    ? "Event payload needed"
                                    : isCatalogTitleBased
                                        ? "Editable (Title)"
                                        : "Editable (Title, inferred)";
                            StatItems.Add(new StatDisplayItem
                            {
                                Name = stat.Name,
                                DisplayName = localTitleStatDefinitionMap.TryGetValue(stat.Name, out var definition)
                                    ? definition.DisplayName
                                    : "",
                                Type = string.IsNullOrWhiteSpace(stat.Type) ? InferStatType(stat.Value) : stat.Type,
                                CurrentValue = NormalizeStatValueForDisplay(stat.Value),
                                NewValue = "",
                                IsEditable = isEditable,
                                EditStatus = editStatus,
                                Source = isEventEditable ? GetStatSource(stat.Name) : isCatalogEventBased ? "Event stats catalog" : "Title Stats API",
                                SuggestedValue = GetSuggestedValue(stat.Name),
                                Confidence = isEventEditable ? GetStatConfidence(stat.Name) : isCatalogEventBased ? "Title is listed in EventBasedStats.txt; requires event request data" : "Read from userstats SCID; write via title-managed stats",
                                Scid = stat.Scid ?? serviceConfigId,
                                TitleId = stat.TitleId ?? TitleId,
                                IsTitleBasedStat = isTitleEditable && !isEventEditable,
                                IsEventBasedStat = isEventEditable || isCatalogEventBased
                            });
                            titleStatsLoaded++;
                        }
                    }
                    else
                    {
                        TitleMetadataInfo = AppendMetadataPart(TitleMetadataInfo, $"Raw stats HTTP {rawStats.StatusCode}");
                    }
                }

                WriteStatsLoadDebugLog(TitleId, gameName, serviceConfigId, statCatalogMode, statNames, statsResponse, titleStatsLoaded);

                // Populate display items from API response
                if (statsResponse?.StatListsCollection != null)
                {
                    foreach (var collection in statsResponse.StatListsCollection)
                    {
                        foreach (var stat in collection.Stats)
                        {
                            var statName = stat.Name ?? "";
                            if (StatItems.Any(item => string.Equals(item.Name, statName, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var isEventEditable = statCatalogMode == "Event" && editableStatNames.Contains(statName);
                            var isCatalogEventBased = statCatalogMode == "Event";
                            var isCatalogTitleBased = statCatalogMode == "Title";
                            var hasScid = !string.IsNullOrWhiteSpace(stat.Scid) || !string.IsNullOrWhiteSpace(serviceConfigId);
                            var isTitleEditable = statCatalogMode == "Title" && hasScid && !isEventEditable;
                            var isEditable = isEventEditable || isTitleEditable;
                            var isMetadataOnly = !isEditable && metadataStatNames.Contains(statName);
                            var editStatus = isEventEditable
                                ? "Editable (Events)"
                                : isTitleEditable
                                    ? "Editable (Title)"
                                    : isCatalogEventBased
                                        ? "Event payload needed"
                                        : isMetadataOnly
                                            ? "Metadata only"
                                            : "Read-Only (missing SCID)";
                            StatItems.Add(new StatDisplayItem
                            {
                                Name = statName == "" ? "Unknown" : statName,
                                DisplayName = localTitleStatDefinitionMap.TryGetValue(statName, out var definition)
                                    ? definition.DisplayName
                                    : "",
                                Type = stat.Type ?? "Unknown",
                                CurrentValue = NormalizeStatValueForDisplay(stat.Value),
                                NewValue = "",
                                IsEditable = isEditable,
                                EditStatus = editStatus,
                                Source = isEventEditable ? GetStatSource(statName) : isCatalogEventBased ? "Event stats catalog" : "Title Stats API",
                                SuggestedValue = GetSuggestedValue(statName),
                                Confidence = isEventEditable ? GetStatConfidence(statName) : isCatalogEventBased ? "Title is listed in EventBasedStats.txt; requires event request data" : isTitleEditable ? "Read from userstats batch; write via title-managed stats" : "Read from userstats batch; write requires SCID",
                                Scid = stat.Scid ?? serviceConfigId,
                                TitleId = stat.TitleId,
                                IsTitleBasedStat = isTitleEditable,
                                IsEventBasedStat = isEventEditable || isCatalogEventBased
                            });
                        }
                    }
                }

                foreach (var definition in localTitleStatDefinitions)
                {
                    if (StatItems.Any(item => string.Equals(item.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var isCatalogEventBased = statCatalogMode == "Event";
                    var hasScid = !string.IsNullOrWhiteSpace(serviceConfigId);
                    StatItems.Add(new StatDisplayItem
                    {
                        Name = definition.Name,
                        DisplayName = definition.DisplayName,
                        Type = definition.Type,
                        CurrentValue = "0",
                        NewValue = "",
                        IsEditable = hasScid && statCatalogMode == "Title",
                        EditStatus = hasScid && !isCatalogEventBased ? "Editable (Title)" : "Read-Only (missing SCID)",
                        Source = definition.Source,
                        SuggestedValue = definition.SuggestedValue,
                        Confidence = definition.Confidence,
                        Scid = serviceConfigId,
                        TitleId = TitleId,
                        IsTitleBasedStat = hasScid && statCatalogMode == "Title",
                        IsEventBasedStat = isCatalogEventBased
                    });
                }

                // Also add event-data stats that weren't returned by the API (stats not yet set)
                foreach (var statName in metadataStatNames)
                {
                    if (!StatItems.Any(s => s.Name == statName))
                    {
                        var isEditable = statCatalogMode == "Event" && editableStatNames.Contains(statName);
                        StatItems.Add(new StatDisplayItem
                        {
                            Name = statName,
                            Type = "Unknown",
                            CurrentValue = "Not Set",
                            NewValue = "",
                            IsEditable = isEditable,
                            EditStatus = isEditable ? "Editable (New)" : "Metadata only",
                            Source = GetStatSource(statName),
                            SuggestedValue = GetSuggestedValue(statName),
                            Confidence = GetStatConfidence(statName),
                            TitleId = TitleId,
                            IsEventBasedStat = isEditable
                        });
                    }
                }

                foreach (var item in StatItems)
                    if (!item.IsEditable) item.EditStatus = "Not Supported";
                var editableCount = StatItems.Count(s => s.IsEditable);
                var notSupportedCount = StatItems.Count(s => !s.IsEditable);
                var titleBasedCount = StatItems.Count(s => s.IsTitleBasedStat);
                HasEditableStats = editableCount > 0;
                if (TitleId != loadingTitleId || account.XUIDOnly != loadingXuid)
                {
                    StatItems.Clear(); HasEditableStats = false;
                    StatusText = "The title or account changed while loading. Load stats again.";
                    return;
                }
                loadedTitleId = loadingTitleId; loadedXuid = loadingXuid;
                foreach (var item in StatItems) { item.OwnerXuid = loadedXuid; item.TitleId ??= loadedTitleId; }
                StatusText = $"Loaded {StatItems.Count} stat(s). {titleBasedCount} title-based editable, {editableCount - titleBasedCount} event-based editable, {notSupportedCount} Not Supported.";
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                _snackbarService.Show("Error", $"Failed to load stats: {ex.Message}",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public void SaveDiscoveredStats()
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(TitleId))
            {
                _snackbarService.Show("Error", "Load a title before saving discovered stats.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            var discoveredStats = StatItems
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && item.Name != "Unknown")
                .ToList();

            if (discoveredStats.Count == 0)
            {
                _snackbarService.Show("No Stats", "No discovered stats are available to save.",
                    NoticeAppearance.Caution, new NoticeIcon(NoticeSymbol.Warning24), _snackbarDuration);
                return;
            }

            var dataPath = Path.Combine(account.EventsDirectory, "Stats.json");

            try
            {
                EnsureStatsFileReady();
                Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
                var original = File.Exists(dataPath) ? File.ReadAllText(dataPath) : null;
                var root = original != null
                    ? JObject.Parse(original)
                    : new JObject();

                var titleObject = EnsureTitleDataObject(root, TitleId.Trim());
                titleObject["Name"] ??= TrimGameName(GameName);
                titleObject["TitleName"] ??= TrimGameName(GameName);

                var scid = discoveredStats
                    .Select(item => item.Scid)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(scid))
                    titleObject["ServiceConfigId"] = scid;

                if (discoveredStats.Any(item => item.IsTitleBasedStat) && titleObject["StatsMode"] == null)
                    titleObject["StatsMode"] = "TitleBased";

                var statsObject = titleObject["Stats"] as JObject;
                if (statsObject == null)
                {
                    statsObject = new JObject();
                    titleObject["Stats"] = statsObject;
                }

                var added = 0;
                var updated = 0;
                foreach (var item in discoveredStats.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var existing = statsObject[item.Name];
                    if (IsEditableStatDefinition(existing))
                        continue;

                    var metadata = BuildDiscoveredStatMetadata(item);
                    if (existing is JObject existingObject)
                    {
                        foreach (var property in metadata.Properties())
                        {
                            if (existingObject[property.Name] == null)
                                existingObject[property.Name] = property.Value.DeepClone();
                        }
                        updated++;
                    }
                    else
                    {
                        statsObject[item.Name] = metadata;
                        added++;
                    }
                }

                if ((File.Exists(dataPath) ? File.ReadAllText(dataPath) : null) != original) throw new IOException("Catalog changed while saving. Reload and try again.");
                if (original != null) File.Copy(dataPath, dataPath + "." + Guid.NewGuid().ToString("N") + ".bak");
                var temporary = dataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, root.ToString(Formatting.Indented), Encoding.UTF8); File.Move(temporary, dataPath, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }

                _eventsDataForTitle = titleObject;
                EventDataInfo = $"Saved discovered stats: {added} added, {updated} updated";
                TitleMetadataInfo = BuildTitleMetadataInfo(titleObject);
                StatusText = $"Saved {added + updated} discovered stat metadata entr{(added + updated == 1 ? "y" : "ies")} to Stats.json.";
                _snackbarService.Show("Stats Saved", StatusText,
                    NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.CheckmarkCircle24), TimeSpan.FromSeconds(4));
            }
            catch (Exception ex)
            {
                StatusText = $"Save failed: {ex.Message}";
                _snackbarService.Show("Save Failed", ex.Message,
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), TimeSpan.FromSeconds(6));
            }
        }

        private void CheckEventData()
        {
            _eventsDataForTitle = null;
            HasEventData = false;
            HasEditableEventData = false;
            EventDataInfo = "";
            TitleMetadataInfo = "";

            string dataPath = Path.Combine(account.EventsDirectory, "Stats.json");

            try
            {
                string json;
                if (File.Exists(dataPath)) json = File.ReadAllText(dataPath);
                else
                {
                    using var stream = typeof(AchievementLabs.Core.StatCatalogStore).Assembly.GetManifestResourceStream("AchievementLabs.LocalEvents.Stats.json");
                    if (stream == null) return;
                    using var reader = new StreamReader(stream); json = reader.ReadToEnd();
                }
                var allData = JObject.Parse(json);
                _eventsDataForTitle = allData[TitleId] as JObject;

                if (_eventsDataForTitle != null && _eventsDataForTitle["Enabled"]?.Value<bool>() != false)
                {
                    AddCandidateReplacementStats(_eventsDataForTitle);
                    var statsSection = _eventsDataForTitle["Stats"] as JObject;
                    if (statsSection != null && statsSection.Count > 0)
                    {
                        var editableCount = statsSection.Properties().Count(property => IsEditableStatDefinition(property.Value));
                        var metadataOnlyCount = statsSection.Count - editableCount;
                        HasEventData = true;
                        HasEditableEventData = editableCount > 0;
                        EventDataInfo = $"Stat metadata: {editableCount} event-editable, {metadataOnlyCount} metadata-only";
                    }
                    else
                    {
                        EventDataInfo = "Title metadata exists but no Stats section found.";
                    }

                    TitleMetadataInfo = BuildTitleMetadataInfo(_eventsDataForTitle);
                }
            }
            catch { }
        }

        private static bool IsEditableStatDefinition(JToken? statData)
        {
            if (statData is not JObject statObject || !statObject.Properties().Any())
                return false;

            return statObject.Properties().Any(property =>
                Regex.IsMatch(property.Name, @"^Request\d+$", RegexOptions.IgnoreCase)
                || property.Name.EndsWith("Replacement", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "EventName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "Metadata", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "Data", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddCandidateReplacementStats(JObject titleData)
        {
            var candidateData = titleData["CandidateReplacementData"] as JObject;
            var candidates = candidateData?["Candidates"] as JObject;
            var statsSection = titleData["Stats"] as JObject;
            if (candidateData == null || candidates == null || statsSection == null)
                return;

            var metadataPattern = candidateData["MetadataPattern"]?.ToString();
            var dataPattern = candidateData["DataPattern"]?.ToString();
            if (string.IsNullOrWhiteSpace(metadataPattern) || string.IsNullOrWhiteSpace(dataPattern))
                return;

            foreach (var candidate in candidates.Properties())
            {
                if (candidate.Value is not JObject candidateObject)
                    continue;

                var eventName = candidateObject["EventName"]?.ToString();
                var property = candidateObject["Property"]?.ToString();
                if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(property))
                    continue;

                var metadata = metadataPattern.Replace("REPLACEPROPERTY", property);
                var data = dataPattern
                    .Replace("REPLACEEVENTNAME", eventName)
                    .Replace("REPLACEPROPERTY", property);

                statsSection[candidate.Name] = new JObject
                {
                    ["EventName"] = new JObject
                    {
                        ["ReplacementType"] = "Replace",
                        ["Target"] = "REPLACEEVENTNAME",
                        ["Replacement"] = eventName
                    },
                    ["Metadata"] = new JObject
                    {
                        ["ReplacementType"] = "Replace",
                        ["Target"] = "REPLACEMETADATA",
                        ["Replacement"] = metadata
                    },
                    ["Data"] = new JObject
                    {
                        ["ReplacementType"] = "Replace",
                        ["Target"] = "REPLACEDATA",
                        ["Replacement"] = data
                    },
                    ["SuggestedValue"] = candidateObject["Value"]?.DeepClone() ?? "",
                    ["Confidence"] = candidateObject["Confidence"]?.ToString() ?? "static candidate"
                };
            }
        }

        private static JObject EnsureTitleDataObject(JObject root, string titleId)
        {
            if (root[titleId] is JObject existing)
                return existing;

            var titleObject = new JObject
            {
                ["StatsMode"] = GetCatalogStatMode(titleId),
                ["Stats"] = new JObject(),
                ["Notes"] = new JArray
                {
                    "Stats discovered from Xbox TitleHub/profile-visible metadata. Add event payloads separately for event-based stats."
                }
            };

            var supportedTitleIds = root.Property("SupportedTitleIDs");
            if (supportedTitleIds != null)
                supportedTitleIds.AddBeforeSelf(new JProperty(titleId, titleObject));
            else
                root[titleId] = titleObject;

            return titleObject;
        }

        private static JObject BuildDiscoveredStatMetadata(StatDisplayItem item)
        {
            var metadata = new JObject
            {
                ["Type"] = string.IsNullOrWhiteSpace(item.Type) ? "Unknown" : item.Type,
                ["Source"] = string.IsNullOrWhiteSpace(item.Source) ? "Discovered" : item.Source,
                ["Confidence"] = string.IsNullOrWhiteSpace(item.Confidence) ? "Discovered stat metadata" : item.Confidence,
                ["CurrentValueAtDiscovery"] = item.CurrentValue ?? "",
                ["DiscoveredUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["EditableMode"] = item.IsTitleBasedStat ? "TitleBased" : item.IsEventBasedStat ? "EventBased" : "Unknown"
            };

            if (!string.IsNullOrWhiteSpace(item.Scid))
                metadata["ServiceConfigId"] = item.Scid;

            if (!string.IsNullOrWhiteSpace(item.TitleId))
                metadata["TitleId"] = item.TitleId;

            return metadata;
        }

        private static void EnsureSupportedTitleIdsLast(JObject root)
        {
            var supportedTitleIds = root.Property("SupportedTitleIDs");
            if (supportedTitleIds == null)
                return;

            var value = supportedTitleIds.Value.DeepClone();
            supportedTitleIds.Remove();
            root.Add("SupportedTitleIDs", value);
        }

        private static string TrimGameName(string gameName)
        {
            const string prefix = "Game:";
            return gameName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? gameName[prefix.Length..].Trim()
                : gameName.Trim();
        }

        private string GetStatSource(string statName)
        {
            if (_eventsDataForTitle?["DiscoveredEvents"] is not JObject discoveredEvents)
                return "";

            var sources = new List<string>();
            foreach (var discoveredEvent in discoveredEvents.Properties())
            {
                if (discoveredEvent.Value["RelatedStats"] is not JArray relatedStats)
                    continue;

                if (relatedStats.Values<string>().Any(value => value == statName))
                    sources.Add(discoveredEvent.Name);
            }

            return sources.Count == 0 ? "" : string.Join(", ", sources);
        }

        private string GetSuggestedValue(string statName)
        {
            var statObject = _eventsDataForTitle?["Stats"]?[statName] as JObject;
            return statObject?["SuggestedValue"]?.ToString() ?? "";
        }

        private string GetStatConfidence(string statName)
        {
            var statObject = _eventsDataForTitle?["Stats"]?[statName] as JObject;
            return statObject?["Confidence"]?.ToString() ?? "";
        }

        private static string BuildTitleMetadataInfo(JObject titleData)
        {
            var parts = new List<string>();
            AddMetadataPart(parts, "Status", titleData["ResearchStatus"]?.ToString());
            AddMetadataPart(parts, "SCID", titleData["ServiceConfigId"]?.ToString());
            AddMetadataPart(parts, "Sandbox", titleData["Sandbox"]?.ToString());
            AddMetadataPart(parts, "Package", titleData["PackageFamilyName"]?.ToString());

            if (titleData["DiscoveredEvents"] is JObject discoveredEvents)
                parts.Add($"Discovered events: {discoveredEvents.Count}");

            if (titleData["Notes"] is JArray notes && notes.Count > 0)
                parts.Add($"Note: {notes.FirstOrDefault()}");

            return string.Join("  |  ", parts);
        }

        private static void AddMetadataPart(List<string> parts, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{label}: {value}");
        }

        private static StatModeCatalog LoadStatModeCatalog()
        {
            var catalog = new StatModeCatalog();
            // Supported modes come from the shipped lists, not similarly named Downloads files.
            foreach (var root in GetStatCatalogRoots().Take(1))
            {
                LoadStatModeFile(Path.Combine(root, "EventBasedStats.txt"), catalog.EventBasedTitleIds);
                LoadStatModeFile(Path.Combine(root, "TitleBasedStats.txt"), catalog.TitleBasedTitleIds);
            }

            return catalog;
        }

        internal static string GetCatalogStatMode(string titleId) => StatCatalog.Value.GetMode(titleId.Trim());

        private static IEnumerable<string> GetStatCatalogRoots()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "StatsCatalog");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "StatsCatalog");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        private static void LoadStatModeFile(string path, HashSet<string> destination)
        {
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadLines(path))
            {
                var match = Regex.Match(line, @"^\s*(\d{1,12})\s*-");
                if (match.Success)
                    destination.Add(match.Groups[1].Value);
            }
        }

        private async Task<List<string>> BuildStatNameCatalogAsync(
            string titleId,
            string gameName,
            HashSet<string> localMetadataStats,
            AchievementsResponse? achievementMetadata,
            IReadOnlyList<TitleStatDefinition> localTitleStatDefinitions)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddStatNames(names, localMetadataStats);
            AddStatNames(names, localTitleStatDefinitions.Select(definition => definition.Name));
            AddStatNames(names, ExtractCandidateStatsFromTitleData(_eventsDataForTitle));
            AddStatNames(names, ExtractAdditionalStatNames(_eventsDataForTitle));
            AddStatNames(names, ExtractStatNamesFromAchievements(achievementMetadata));
            AddStatNames(names, await TryLoadPluginStatsCatalogAsync(titleId, gameName));
            AddStatNames(names, GetBuiltInCandidateStatNames());

            if (names.Count == 0)
                names.Add("MinutesPlayed");

            TitleMetadataInfo = AppendMetadataPart(TitleMetadataInfo, $"Requested stats: {names.Count}");
            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IReadOnlyList<TitleStatDefinition> LoadLocalTitleStatDefinitions(string titleId, string gameName)
        {
            var definitions = new List<TitleStatDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in GetTitleStatDefinitionFiles())
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    var root = JToken.Parse(File.ReadAllText(path));
                    foreach (var definition in ExtractTitleStatDefinitions(root, titleId, gameName))
                    {
                        if (seen.Add(definition.Name))
                            definitions.Add(definition);
                    }
                }
                catch
                {
                    // Catalog files are optional. Ignore malformed local experiments.
                }
            }

            return definitions;
        }

        private static IEnumerable<string> GetTitleStatDefinitionFiles()
        {
            foreach (var root in GetStatCatalogRoots())
            {
                yield return Path.Combine(root, "TitleStatDefinitions.json");
                yield return Path.Combine(root, "TitleStats.json");
            }
        }

        private static IEnumerable<TitleStatDefinition> ExtractTitleStatDefinitions(JToken root, string titleId, string gameName)
        {
            if (root is JObject rootObject)
            {
                var direct = rootObject[titleId];
                if (direct != null)
                {
                    foreach (var definition in ExtractDefinitionsFromTitleBlock(direct))
                        yield return definition;
                }

                foreach (var property in rootObject.Properties())
                {
                    if (string.Equals(property.Name, titleId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (property.Value is JObject valueObject &&
                        IsMatchingTitleDefinitionBlock(valueObject, titleId, gameName))
                    {
                        foreach (var definition in ExtractDefinitionsFromTitleBlock(valueObject))
                            yield return definition;
                    }
                }
            }

            foreach (var titleObject in WalkObjects(root).Where(obj => IsMatchingTitleDefinitionBlock(obj, titleId, gameName)))
            {
                foreach (var definition in ExtractDefinitionsFromTitleBlock(titleObject))
                    yield return definition;
            }
        }

        private static bool IsMatchingTitleDefinitionBlock(JObject obj, string titleId, string gameName)
        {
            var objectTitleId = FirstString(obj, "titleId", "TitleId", "xboxTitleId", "title_id", "id");
            if (string.Equals(objectTitleId, titleId, StringComparison.OrdinalIgnoreCase))
                return true;

            var objectGameName = FirstString(obj, "gameName", "GameName", "titleName", "TitleName", "name", "Name", "title");
            return !string.IsNullOrWhiteSpace(gameName)
                && !string.IsNullOrWhiteSpace(objectGameName)
                && (objectGameName.Contains(gameName, StringComparison.OrdinalIgnoreCase)
                    || gameName.Contains(objectGameName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<TitleStatDefinition> ExtractDefinitionsFromTitleBlock(JToken block)
        {
            var statsToken = block["Stats"] ?? block["stats"] ?? block["Definitions"] ?? block["definitions"];
            if (statsToken == null)
                statsToken = block is JArray ? block : null;

            if (statsToken is JObject statsObject)
            {
                foreach (var property in statsObject.Properties())
                {
                    if (property.Value is JObject statObject)
                    {
                        var definition = BuildTitleStatDefinition(property.Name, statObject);
                        if (definition != null)
                            yield return definition;
                    }
                    else if (LooksLikeStatName(property.Name))
                    {
                        yield return new TitleStatDefinition { Name = property.Name };
                    }
                }
            }
            else if (statsToken is JArray statsArray)
            {
                foreach (var statObject in statsArray.OfType<JObject>())
                {
                    var definition = BuildTitleStatDefinition(null, statObject);
                    if (definition != null)
                        yield return definition;
                }
            }
        }

        private static TitleStatDefinition? BuildTitleStatDefinition(string? fallbackName, JObject statObject)
        {
            var name = FirstString(statObject, "statName", "StatName", "name", "Name", "id", "Id") ?? fallbackName;
            if (!LooksLikeStatName(name))
                return null;

            return new TitleStatDefinition
            {
                Name = name!.Trim(),
                DisplayName = FirstString(statObject, "displayName", "DisplayName", "label", "Label", "title", "Title", "description", "Description") ?? "",
                Type = FirstString(statObject, "type", "Type", "valueType", "ValueType", "displayFormat", "DisplayFormat") ?? "Integer",
                Source = FirstString(statObject, "source", "Source") ?? "Local title stat catalog",
                Confidence = FirstString(statObject, "confidence", "Confidence") ?? "Curated title-based stat definition",
                SuggestedValue = FirstString(statObject, "suggestedValue", "SuggestedValue", "defaultValue", "DefaultValue") ?? ""
            };
        }

        private static void AddStatNames(HashSet<string> destination, IEnumerable<string>? names)
        {
            if (names == null)
                return;

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    destination.Add(name.Trim());
            }
        }

        private static IEnumerable<string> ExtractCandidateStatsFromTitleData(JObject? titleData)
        {
            if (titleData == null)
                yield break;

            foreach (var token in titleData.DescendantsAndSelf().OfType<JObject>())
            {
                foreach (var key in new[] { "CandidateStat", "StatName", "statName", "name" })
                {
                    var value = token[key]?.ToString();
                    if (LooksLikeStatName(value))
                        yield return value!;
                }
            }
        }

        private static IEnumerable<string> ExtractAdditionalStatNames(JObject? titleData)
        {
            if (titleData?["AdditionalStatNames"] is not JArray names)
                yield break;

            foreach (var name in names.Values<string>())
            {
                if (LooksLikeStatName(name))
                    yield return name!;
            }
        }

        private static IEnumerable<string> ExtractStatNamesFromAchievements(AchievementsResponse? achievementMetadata)
        {
            if (achievementMetadata?.achievements == null)
                yield break;

            foreach (var achievement in achievementMetadata.achievements)
            {
                if (achievement.progression?.requirements == null)
                    continue;

                foreach (var requirement in achievement.progression.requirements)
                {
                    if (LooksLikeStatName(requirement.id))
                        yield return requirement.id!;
                }
            }
        }

        private async Task<IEnumerable<string>> TryLoadPluginStatsCatalogAsync(string titleId, string gameName)
        {
            if (!allowRemoteCatalog) return [];
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string baseUrl = "https://raw.githubusercontent.com/nacliaf/xau-plugin/master/Stats/";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var candidates = new List<string>
            {
                $"{titleId}.json",
                $"{titleId}",
                "games.json",
                "index.json",
                "data.json"
            };

            foreach (var candidate in candidates)
            {
                if (deadline.IsCancellationRequested) break;
                var json = await TryGetJsonAsync(client, baseUrl + candidate, deadline.Token);
                if (json == null)
                    continue;

                AddStatNames(discovered, ExtractStatNamesFromCatalogJson(json));

                foreach (var fileId in ExtractMatchingFileIds(json, titleId, gameName))
                {
                    foreach (var fileCandidate in new[] { fileId, $"{fileId}.json" }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (deadline.IsCancellationRequested) break;
                        var fileJson = await TryGetJsonAsync(client, baseUrl + fileCandidate, deadline.Token);
                        if (fileJson != null)
                            AddStatNames(discovered, ExtractStatNamesFromCatalogJson(fileJson));
                    }
                }
            }

            return discovered;
        }

        private static async Task<JToken?> TryGetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var body = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return null;

                return JToken.Parse(body);
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> ExtractMatchingFileIds(JToken root, string titleId, string gameName)
        {
            foreach (var obj in WalkObjects(root))
            {
                var objectTitleId = FirstString(obj, "title_id", "titleId", "xboxTitleId", "xbox_title_id", "id");
                var objectGameName = FirstString(obj, "game_name", "gameName", "name", "title");
                var isMatch = string.Equals(objectTitleId, titleId, StringComparison.OrdinalIgnoreCase) ||
                              (!string.IsNullOrWhiteSpace(objectGameName) &&
                               objectGameName.Contains(gameName, StringComparison.OrdinalIgnoreCase));

                if (!isMatch)
                    continue;

                var fileId = FirstString(obj, "file_id", "fileId", "file", "path");
                if (!string.IsNullOrWhiteSpace(fileId))
                    yield return fileId!;
            }
        }

        private static IEnumerable<string> ExtractStatNamesFromCatalogJson(JToken root)
        {
            foreach (var obj in WalkObjects(root))
            {
                var statName = FirstString(obj, "stat", "stat_name", "statName", "name", "id");
                if (LooksLikeStatName(statName))
                    yield return statName!;
            }
        }

        private static IEnumerable<JObject> WalkObjects(JToken root)
        {
            if (root is JObject rootObject)
                yield return rootObject;

            if (root is not JContainer container)
                yield break;

            foreach (var obj in container.Descendants().OfType<JObject>())
                yield return obj;
        }

        private static string? FirstString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = obj[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static bool LooksLikeStatName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.Length > 96)
                return false;

            return Regex.IsMatch(value.Trim(), @"^[A-Za-z_][A-Za-z0-9_.: -]*$");
        }

        private static IEnumerable<string> GetBuiltInCandidateStatNames()
        {
            return new[]
            {
                "MinutesPlayed",
                "SecondsPlayed",
                "TimePlayed",
                "PlayedTime",
                "TimePlayedSeconds",
                "TimePlayedMinutes",
                "TotalTimePlayed",
                "TotalMinutesPlayed",
                "TotalSecondsPlayed",
                "LifetimeScore",
                "TotalScore",
                "HighestScore",
                "Score",
                "GamesPlayed",
                "MatchesPlayed",
                "GamesWon",
                "MatchesWon",
                "Wins",
                "Kills",
                "Deaths",
                "Assists",
                "Headshots",
                "EnemiesKilled",
                "TotalKills",
                "KillCount",
                "StealthKills",
                "Assassinations",
                "Executions",
                "AnimalsKilled",
                "LegendaryAnimalsKilled",
                "Level",
                "PlayerLevel",
                "PlayerXP",
                "PlayerXp",
                "Experience",
                "ExperiencePoints",
                "TotalXP",
                "TotalXp",
                "XP",
                "Rank",
                "PlayerRank",
                "Coins",
                "Currency",
                "Money",
                "Gold",
                "Silver",
                "Drachmae",
                "Resources",
                "ItemsCollected",
                "Collectibles",
                "CollectiblesFound",
                "CollectiblesCollected",
                "LocationsDiscovered",
                "LocationsCompleted",
                "RegionsDiscovered",
                "SyncPoints",
                "SynchronizationPoints",
                "ViewpointsSynchronized",
                "QuestsCompleted",
                "MainQuestsCompleted",
                "SideQuestsCompleted",
                "MissionsCompleted",
                "ContractsCompleted",
                "CultistsKilled",
                "MercenariesDefeated",
                "ShipsSunk",
                "NavalKills",
                "ArenaWins",
                "ConquestsWon",
                "DistanceTravelled",
                "DistanceTraveled",
                "DistanceRun",
                "DistanceSailed",
                "DistanceRidden",
                "Progress",
                "TotalProgress",
                "CompletedScenario"
            };
        }

        private void WriteStatsLoadDebugLog(
            string titleId,
            string gameName,
            string? serviceConfigId,
            string statCatalogMode,
            IReadOnlyCollection<string> requestedStats,
            GameStatsResponse? statsResponse,
            int rawTitleStatsLoaded)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "AchievementLabs",
                    "Debug",
                    "StatsEditorLoads");
                Directory.CreateDirectory(root);

                var capturedUtc = DateTime.UtcNow;
                var returnedStats = new JArray();
                if (statsResponse?.StatListsCollection != null)
                {
                    foreach (var collection in statsResponse.StatListsCollection)
                    {
                        foreach (var stat in collection.Stats)
                        {
                            returnedStats.Add(new JObject
                            {
                                ["name"] = stat.Name ?? "",
                                ["type"] = stat.Type ?? "",
                                ["value"] = stat.Value ?? "",
                                ["scid"] = stat.Scid ?? "",
                                ["titleId"] = stat.TitleId ?? ""
                            });
                        }
                    }
                }

                var rawBatch = GetRestAPI().LastGameStatsRawResponse;
                var payload = new JObject
                {
                    ["capturedUtc"] = capturedUtc.ToString("O"),
                    ["titleId"] = titleId,
                    ["gameName"] = gameName,
                    ["serviceConfigId"] = serviceConfigId ?? "",
                    ["statCatalogMode"] = statCatalogMode,
                    ["requestedStatsCount"] = requestedStats.Count,
                    ["requestedStats"] = new JArray(requestedStats),
                    ["rawTitleStatsLoaded"] = rawTitleStatsLoaded,
                    ["batchReturnedStatsCount"] = returnedStats.Count,
                    ["batchReturnedStats"] = returnedStats,
                    ["lastBatchRawResponse"] = TryParseJsonToken(rawBatch) ?? rawBatch ?? ""
                };

                var fileName = $"{capturedUtc:yyyyMMdd_HHmmss_fff}_{SanitizeFilePart(titleId)}.json";
                File.WriteAllText(Path.Combine(root, fileName), payload.ToString(Formatting.Indented));
            }
            catch
            {
                // Debug capture is best-effort; it should never block stats loading.
            }
        }

        private static JToken? TryParseJsonToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                return JToken.Parse(value);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeStatValueForDisplay(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "--" : value;
        }

        private static string AppendMetadataPart(string existing, string part)
        {
            if (string.IsNullOrWhiteSpace(part))
                return existing;

            if (existing.Contains(part, StringComparison.OrdinalIgnoreCase))
                return existing;

            return string.IsNullOrWhiteSpace(existing) ? part : $"{existing}  |  {part}";
        }

        private static List<RawTitleStat> ExtractTitleHubVisibleStats(string responseBody, string titleId, string? fallbackScid)
        {
            var results = new List<RawTitleStat>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = JToken.Parse(responseBody);
                var titleTokens = root is JContainer rootContainer
                    ? rootContainer.Descendants().Concat(new[] { root })
                    : new[] { root };
                var titleObjects = titleTokens
                    .OfType<JObject>()
                    .Where(obj => string.Equals(obj["titleId"]?.ToString(), titleId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(obj["modernTitleId"]?.ToString(), titleId, StringComparison.OrdinalIgnoreCase));

                foreach (var titleObject in titleObjects)
                {
                    var statsToken = titleObject["stats"] ?? titleObject["Stats"];
                    if (statsToken == null || statsToken.Type == JTokenType.Null)
                        continue;

                    var statTokens = statsToken is JContainer statsContainer
                        ? statsContainer.Descendants().Concat(new[] { statsToken })
                        : new[] { statsToken };
                    foreach (var obj in statTokens.OfType<JObject>())
                    {
                        var name = obj["statName"]?.ToString()
                            ?? obj["statname"]?.ToString()
                            ?? obj["statisticName"]?.ToString()
                            ?? obj["id"]?.ToString()
                            ?? obj["name"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                            continue;

                        var valueToken = obj["value"]
                            ?? obj["displayValue"]
                            ?? obj["currentValue"]
                            ?? obj["numberValue"]
                            ?? obj["stringValue"];
                        if (valueToken == null)
                            continue;

                        results.Add(new RawTitleStat
                        {
                            Name = name,
                            Type = obj["type"]?.ToString()
                                ?? obj["valueType"]?.ToString()
                                ?? obj["statisticType"]?.ToString()
                                ?? obj["displayFormat"]?.ToString()
                                ?? "",
                            Value = TokenToDisplayValue(valueToken),
                            Scid = obj["scid"]?.ToString()
                                ?? obj["serviceConfigId"]?.ToString()
                                ?? fallbackScid,
                            TitleId = obj["titleId"]?.ToString()
                                ?? obj["modernTitleId"]?.ToString()
                                ?? titleId
                        });
                    }
                }
            }
            catch
            {
                // TitleHub stats parsing is opportunistic; the raw payload is logged for follow-up.
            }

            return results.OrderBy(stat => stat.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void AddDerivedAchievementStats(AchievementsResponse? achievementMetadata)
        {
            if (achievementMetadata?.achievements == null || achievementMetadata.achievements.Count == 0)
                return;

            AddDerivedAchievementCategoryStat(
                achievementMetadata,
                "Campaign Achievements Unlocked",
                achievement => AchievementTextContainsAny(achievement, "campaign", "story", "mission"));
            AddDerivedAchievementCategoryStat(
                achievementMetadata,
                "Multiplayer Achievements Unlocked",
                achievement => AchievementTextContainsAny(achievement, "multiplayer", "versus", "public match", "fireteam"));
            AddDerivedAchievementCategoryStat(
                achievementMetadata,
                "Zombies Achievements Unlocked",
                achievement => AchievementTextContainsAny(achievement, "zombie", "zombies", "undead"));
        }

        private void AddDerivedAchievementCategoryStat(
            AchievementsResponse achievementMetadata,
            string statName,
            Func<OneCoreAchievementResponse, bool> predicate)
        {
            if (StatItems.Any(item => string.Equals(item.Name, statName, StringComparison.OrdinalIgnoreCase)))
                return;

            var matchingAchievements = achievementMetadata.achievements.Where(predicate).ToList();
            if (matchingAchievements.Count == 0)
                return;

            var unlocked = matchingAchievements.Count(IsAchievementUnlocked);
            StatItems.Add(new StatDisplayItem
            {
                Name = statName,
                Type = "Derived",
                CurrentValue = $"{unlocked}/{matchingAchievements.Count}",
                NewValue = "",
                IsEditable = false,
                EditStatus = "Read-Only (derived)",
                Source = "Achievements API",
                SuggestedValue = "",
                Confidence = "Derived from achievement metadata because TitleHub/userstats did not expose a writable stat row",
                TitleId = TitleId
            });
        }

        private static bool IsAchievementUnlocked(OneCoreAchievementResponse achievement)
        {
            return string.Equals(achievement.progressState, "Achieved", StringComparison.OrdinalIgnoreCase)
                || string.Equals(achievement.progressState, "Unlocked", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(achievement.progression?.timeUnlocked);
        }

        private static bool AchievementTextContainsAny(OneCoreAchievementResponse achievement, params string[] terms)
        {
            var haystack = new StringBuilder();
            haystack.Append(' ').Append(achievement.name);
            haystack.Append(' ').Append(achievement.description);
            haystack.Append(' ').Append(achievement.lockedDescription);
            haystack.Append(' ').Append(achievement.achievementType);
            haystack.Append(' ').Append(achievement.participationType);

            foreach (var association in achievement.titleAssociations)
                haystack.Append(' ').Append(association.name).Append(' ').Append(association.id);

            foreach (var reward in achievement.rewards)
                haystack.Append(' ').Append(reward.name).Append(' ').Append(reward.description);

            var text = haystack.ToString();
            return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static List<RawTitleStat> ExtractRawTitleStats(string responseBody, string fallbackScid)
        {
            var results = new List<RawTitleStat>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = JToken.Parse(responseBody);
                var tokens = root is JContainer container
                    ? container.Descendants().Concat(new[] { root })
                    : new[] { root };

                foreach (var obj in tokens.OfType<JObject>())
                {
                    var name = obj["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var valueToken = obj["value"] ?? obj["Value"] ?? obj["properties"]?["value"] ?? obj["Properties"]?["value"];
                    if (valueToken == null)
                        continue;

                    if (!seen.Add(name))
                        continue;

                    results.Add(new RawTitleStat
                    {
                        Name = name,
                        Type = obj["type"]?.ToString() ?? obj["valueType"]?.ToString() ?? "",
                        Value = TokenToDisplayValue(valueToken),
                        Scid = obj["scid"]?.ToString() ?? obj["Scid"]?.ToString() ?? fallbackScid,
                        TitleId = obj["titleId"]?.ToString() ?? obj["TitleId"]?.ToString()
                    });
                }
            }
            catch
            {
                // Raw stats parsing is opportunistic. The calling path will still show the HTTP status.
            }

            return results.OrderBy(stat => stat.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string TokenToDisplayValue(JToken valueToken)
        {
            if (valueToken is JValue value)
                return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "";

            return valueToken.ToString(Formatting.None);
        }

        private static string InferStatType(string value)
        {
            if (bool.TryParse(value, out _))
                return "Boolean";

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return "Integer";

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return "Double";

            return "String";
        }

        internal static JToken BuildStatValue(string newValue, string type, string currentValue)
        {
            var typeHint = string.IsNullOrWhiteSpace(type) || type.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? InferStatType(currentValue).ToLowerInvariant() : type.ToLowerInvariant();
            if (typeHint.Contains("string")) return new JValue(newValue);

            if (typeHint.Contains("bool") && bool.TryParse(newValue, out var boolValue))
                return new JValue(boolValue);

            if ((typeHint.Contains("int") || typeHint.Contains("long") || typeHint.Contains("uint") || typeHint.Contains("integer")) &&
                long.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            {
                return new JValue(longValue);
            }

            if ((typeHint.Contains("double") || typeHint.Contains("float") || typeHint.Contains("single") || typeHint.Contains("decimal") || typeHint.Contains("number")) &&
                double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) && double.IsFinite(doubleValue))
            {
                return new JValue(doubleValue);
            }

            throw new FormatException($"Enter a valid {typeHint} value using an invariant decimal point.");
        }

        public async Task UpdateSingleStat(StatDisplayItem item)
        {
            if (IsBusy) return;
            IsUpdating = true;
            try { await UpdateSingleStatCoreAsync(item); }
            finally { IsUpdating = false; }
        }

        private async Task UpdateSingleStatCoreAsync(StatDisplayItem item)
        {
            if (!StatItems.Contains(item) || loadedTitleId != TitleId || loadedXuid != account.XUIDOnly
                || item.OwnerXuid != loadedXuid || string.IsNullOrEmpty(loadedXuid)
                || item.TitleId != loadedTitleId || !(CurrentStatMode == "Title" && item.IsTitleBasedStat || CurrentStatMode == "Event" && item.IsEventBasedStat && HasEditableEventData))
            {
                item.EditStatus = "Reload required";
                StatusText = "Load a supported title's stats for the connected account before updating.";
                return;
            }
            if (!item.IsEditable || string.IsNullOrWhiteSpace(item.NewValue))
            {
                _snackbarService.Show("Error", "Enter a new value for the stat.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            try
            {
                if (account.EnsureAuthorizationAsync != null) await account.EnsureAuthorizationAsync(CancellationToken.None);
                if (!item.IsTitleBasedStat && account.EnsureEventTokenAsync != null) await account.EnsureEventTokenAsync(CancellationToken.None);
            }
            catch
            {
                item.EditStatus = "Current token required";
                _snackbarService.Show("Token required", "Paste and save a current token in Settings before updating stats.",
                    NoticeAppearance.Caution, new NoticeIcon(NoticeSymbol.Warning24), _snackbarDuration);
                return;
            }

            if (loadedXuid != account.XUIDOnly || loadedTitleId != TitleId || !StatItems.Contains(item))
            { item.EditStatus = "Reload required"; return; }

            if (item.IsTitleBasedStat)
            {
                await UpdateTitleBasedStat(item);
                return;
            }

            var eventsToken = account.EventsToken;
            if (string.IsNullOrWhiteSpace(eventsToken))
            {
                _snackbarService.Show("Error: No Events Token",
                    "Event-based stat editing requires an events token.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            if (_eventsDataForTitle == null) return;

            var statsSection = _eventsDataForTitle["Stats"] as JObject;
            if (statsSection == null || !statsSection.ContainsKey(item.Name)) return;

            var statData = statsSection[item.Name]!;
            if (statData is not JObject statObject || !statObject.Properties().Any())
            {
                item.EditStatus = "No event data";
                _snackbarService.Show("No Stat Event Data",
                    $"{item.Name} is listed for discovery, but no update event has been defined yet.",
                    NoticeAppearance.Caution, new NoticeIcon(NoticeSymbol.Warning24), TimeSpan.FromSeconds(5));
                return;
            }

            // Load template
            var templateBody = ReadStatTemplate();
            if (templateBody == null)
            {
                _snackbarService.Show("Error", $"Event template {TitleId}.json not found.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            item.EditStatus = "Sending...";

            try
            {
                DateTime timestamp = DateTime.UtcNow;

                // Detect multi-request vs single-request format
                bool isMultiRequest = false;
                foreach (var entry in statObject)
                {
                    if (IsStatMetadataProperty(entry.Key))
                        continue;

                    isMultiRequest = ((JObject)entry.Value!).Property("ReplacementType") == null;
                    break;
                }

                var requestBodies = new List<string>();

                if (isMultiRequest)
                {
                    foreach (var request in statObject)
                    {
                        if (IsStatMetadataProperty(request.Key))
                            continue;

                        var requestbody = templateBody;
                        foreach (var replacement in (JObject)request.Value!)
                        {
                            if (IsStatMetadataProperty(replacement.Key))
                                continue;

                            if (!ApplyReplacement(ref requestbody, replacement.Value!, item.NewValue))
                                return;
                        }
                        requestbody = ApplyCommonReplacements(requestbody, timestamp);
                        requestBodies.Add(requestbody);
                    }
                }
                else
                {
                    var requestbody = templateBody;
                    foreach (var replacement in statObject)
                    {
                        if (IsStatMetadataProperty(replacement.Key))
                            continue;

                        if (!ApplyReplacement(ref requestbody, replacement.Value!, item.NewValue))
                            return;
                    }
                    requestbody = ApplyCommonReplacements(requestbody, timestamp);
                    requestBodies.Add(requestbody);
                }

                // Validate every payload before the first request; no unresolved or wrong-title data is submitted.
                for (var i = 0; i < requestBodies.Count; i++)
                {
                    if (Regex.IsMatch(requestBodies[i], @"REPLACE[A-Z0-9_]+")) throw new InvalidDataException("Unresolved stat event placeholder.");
                    var parsed = JObject.Parse(requestBodies[i]);
                    if (parsed["data"]?["baseData"]?["titleId"]?.ToString() != TitleId) throw new InvalidDataException("Stat event title mismatch.");
                    requestBodies[i] = parsed.ToString(Formatting.None);
                    using var strict = System.Text.Json.JsonDocument.Parse(requestBodies[i]);
                }
                // Send request(s)
                bool allSucceeded = true;
                var debugDir = CreateStatDebugDirectory(item.Name);
                var requestResults = new JArray();

                for (int reqIdx = 0; reqIdx < requestBodies.Count; reqIdx++)
                {
                    var requestName = $"Request{reqIdx + 1}";
                    var requestFile = $"request_{reqIdx + 1:00}_{requestName}.json";
                    File.WriteAllText(Path.Combine(debugDir, requestFile), FormatJsonForDebug(requestBodies[reqIdx]));

                    var result = await GetRestAPI().UnlockEventBasedAchievementWithDiagnostics(eventsToken, requestBodies[reqIdx]);
                    var responseFile = $"response_{reqIdx + 1:00}_{requestName}.txt";
                    File.WriteAllText(Path.Combine(debugDir, responseFile), $"{result.StatusCode} {result.ReasonPhrase}{Environment.NewLine}{result.ResponseBody}");

                    requestResults.Add(new JObject
                    {
                        ["requestIndex"] = reqIdx + 1,
                        ["requestName"] = requestName,
                        ["requestFile"] = requestFile,
                        ["responseFile"] = responseFile,
                        ["request"] = BuildStatRequestMetadata(requestBodies[reqIdx]),
                        ["response"] = BuildStatResponseMetadata(result)
                    });

                    if (result.StatusCode < 200 || result.StatusCode >= 300)
                    {
                        if (result.StatusCode is 401 or 403) account.EventTokenRejected?.Invoke();
                        var truncated = result.ResponseBody.Length > 200 ? result.ResponseBody.Substring(0, 200) + "..." : result.ResponseBody;
                        _snackbarService.Show($"Error: HTTP {result.StatusCode}",
                            truncated, NoticeAppearance.Danger,
                            new NoticeIcon(NoticeSymbol.ErrorCircle24), TimeSpan.FromSeconds(5));
                        allSucceeded = false;
                        break;
                    }
                }

                File.WriteAllText(Path.Combine(debugDir, "summary.json"), new JObject
                {
                    ["completedUtc"] = DateTime.UtcNow.ToString("O"),
                    ["titleId"] = TitleId,
                    ["statName"] = item.Name,
                    ["requestedValue"] = item.NewValue,
                    ["allRequestsHttpSuccess"] = allSucceeded,
                    ["requests"] = requestResults
                }.ToString(Formatting.Indented));

                if (allSucceeded)
                {
                    item.NewValue = "";
                    item.EditStatus = "Submitted; refresh to verify";
                    _snackbarService.Show("Stat event submitted",
                        $"Refresh {item.Name} to verify the value.",
                        NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
                }
                else
                {
                    item.EditStatus = "Failed";
                }
            }
            catch (Exception ex)
            {
                item.EditStatus = "Error";
                _snackbarService.Show("Error", $"Failed to update stat: {ex.Message}",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
            }
        }

        private async Task UpdateTitleBasedStat(StatDisplayItem item)
        {
            var requestedValue = item.NewValue;
            JToken typedValue;
            try { typedValue = BuildStatValue(requestedValue, item.Type, item.CurrentValue); }
            catch (FormatException error) { item.EditStatus = "Invalid value"; StatusText = error.Message; return; }
            var serviceConfigId = item.Scid;
            if (string.IsNullOrWhiteSpace(serviceConfigId))
                serviceConfigId = await GetRestAPI().GetTitleServiceConfigIdAsync(account.XUIDOnly, item.TitleId ?? TitleId);

            if (!Guid.TryParse(serviceConfigId, out var validScid) || validScid == Guid.Empty)
            {
                item.EditStatus = "Not Supported"; item.IsEditable = false;
                _snackbarService.Show("Missing SCID",
                    "Could not resolve this title's service config id.",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                return;
            }

            item.EditStatus = "Sending...";
            string? debugDir = null;
            try
            {
                try { debugDir = CreateStatDebugDirectory(item.Name); } catch { /* Diagnostics must not block a stat write. */ }
                var result = await GetRestAPI().WriteTitleStatWithDiagnosticsAsync(account.XUIDOnly, serviceConfigId, item.Name, typedValue);
                if (!string.IsNullOrWhiteSpace(result.RequestBody))
                {
                    WriteStatDiagnostic(debugDir, "request_title_stat_actual.json", FormatJsonForDebug(result.RequestBody));
                }

                WriteStatDiagnostic(debugDir, "response_title_stat.txt",
                    $"HTTP {result.StatusCode} {result.ReasonPhrase}{Environment.NewLine}{result.RequestUri}{Environment.NewLine}{Environment.NewLine}{result.ResponseBody}");

                WriteStatDiagnostic(debugDir, "summary.json", new JObject
                {
                    ["completedUtc"] = DateTime.UtcNow.ToString("O"),
                    ["titleId"] = item.TitleId ?? TitleId,
                    ["serviceConfigId"] = serviceConfigId,
                    ["statName"] = item.Name,
                    ["requestedValue"] = item.NewValue,
                    ["typedValue"] = typedValue,
                    ["statusCode"] = result.StatusCode,
                    ["reasonPhrase"] = result.ReasonPhrase,
                    ["requestUri"] = result.RequestUri,
                    ["actualRequestBody"] = TryParseJsonToken(result.RequestBody) ?? result.RequestBody,
                    ["responseBodyPreview"] = Truncate(result.ResponseBody ?? string.Empty, 1000)
                }.ToString(Formatting.Indented));

                if (result.StatusCode >= 200 && result.StatusCode < 300)
                {
                    item.NewValue = "";
                    item.EditStatus = "Accepted; refresh to verify";
                    try
                    {
                        var readback = await GetRestAPI().GetTitleStatRawAsync(account.XUIDOnly, serviceConfigId, item.Name);
                        if (readback.StatusCode >= 200 && readback.StatusCode < 300)
                        {
                            var observed = ExtractRawTitleStats(readback.Body, serviceConfigId)
                                .FirstOrDefault(stat => stat.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                            if (observed != null)
                            {
                                item.CurrentValue = NormalizeStatValueForDisplay(observed.Value);
                                if (StatValueMatches(observed.Value, typedValue)) item.EditStatus = "Verified";
                            }
                        }
                    }
                    catch { /* Accepted writes may not be visible immediately; preserve their status. */ }
                    _snackbarService.Show("Stat request accepted", $"{item.Name}: {item.EditStatus}",
                        NoticeAppearance.Success, new NoticeIcon(NoticeSymbol.Checkmark24), _snackbarDuration);
                }
                else
                {
                    item.EditStatus = result.StatusCode is 400 or 403 or 404 or 405 or 422 ? "Not Supported" : $"HTTP {result.StatusCode}";
                    if (item.EditStatus == "Not Supported") item.IsEditable = false;
                    var truncated = Truncate(result.ResponseBody ?? "", 200);
                    _snackbarService.Show($"Statswrite HTTP {result.StatusCode}",
                        string.IsNullOrWhiteSpace(truncated) ? result.ReasonPhrase : truncated,
                        NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                item.EditStatus = "Error";
                WriteStatDiagnostic(debugDir, "exception.txt", ex.ToString());
                _snackbarService.Show("Error", $"Failed to update title stat: {ex.Message}",
                    NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
            }
        }

        private static bool StatValueMatches(string observed, JToken expected)
        {
            if (expected.Type == JTokenType.String) return observed == expected.Value<string>();
            try { return JToken.DeepEquals(BuildStatValue(observed, expected.Type.ToString(), observed), expected); }
            catch { return false; }
        }

        [RelayCommand]
        public async Task UpdateAllStats()
        {
            if (IsBusy) return;
            var editableItems = StatItems.Where(s => s.IsEditable && !string.IsNullOrWhiteSpace(s.NewValue)).ToList();
            if (editableItems.Count == 0)
            {
                _snackbarService.Show("Nothing to Update", "Enter new values for stats you want to change.",
                    NoticeAppearance.Caution, new NoticeIcon(NoticeSymbol.Warning24), _snackbarDuration);
                return;
            }

            StatusText = $"Updating {editableItems.Count} stat(s)...";
            IsUpdating = true;
            try
            {
            int successCount = 0;
            foreach (var item in editableItems)
            {
                await UpdateSingleStatCoreAsync(item);
                if (item.EditStatus is "Verified" or "Accepted; refresh to verify")
                    successCount++;
            }

            StatusText = $"Accepted {successCount}/{editableItems.Count} stat request(s). Readback status is shown on each row.";
            }
            finally { IsUpdating = false; }
        }

        #region Replacement Helpers

        private string CreateStatDebugDirectory(string statName)
        {
            var root = DebugRootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "Debug", "StatEdits");
            Directory.CreateDirectory(root);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var dir = Path.Combine(root, $"{timestamp}_{SanitizeFilePart(TitleId)}_{SanitizeFilePart(statName)}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteStatDiagnostic(string? directory, string fileName, string contents)
        {
            if (directory == null) return;
            try { File.WriteAllText(Path.Combine(directory, fileName), contents); }
            catch { /* Logging failure must not alter acceptance or readback status. */ }
        }

        private static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            return Regex.Replace(value, @"[^\w\-. ]+", "_").Trim();
        }

        private static string FormatJsonForDebug(string requestBody)
        {
            try
            {
                return JToken.Parse(requestBody).ToString(Formatting.Indented);
            }
            catch
            {
                return requestBody;
            }
        }

        private static JObject BuildStatRequestMetadata(string requestBody)
        {
            var metadata = new JObject
            {
                ["payloadLength"] = requestBody.Length,
                ["containsReplacementPlaceholder"] = requestBody.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)
            };

            try
            {
                var parsed = JObject.Parse(requestBody);
                var baseData = parsed["data"]?["baseData"];
                metadata["validJson"] = true;
                metadata["eventName"] = baseData?["name"]?.ToString() ?? string.Empty;
                metadata["titleId"] = baseData?["titleId"]?.ToString() ?? string.Empty;
                metadata["serviceConfigId"] = baseData?["serviceConfigId"]?.ToString() ?? string.Empty;
                metadata["propertyNames"] = new JArray(((JObject?)baseData?["properties"])?.Properties().Select(property => property.Name) ?? Enumerable.Empty<string>());
                metadata["measurementNames"] = new JArray(((JObject?)baseData?["measurements"])?.Properties().Select(property => property.Name) ?? Enumerable.Empty<string>());
            }
            catch (Exception ex)
            {
                metadata["validJson"] = false;
                metadata["parseError"] = ex.Message;
            }

            return metadata;
        }

        private static JObject BuildStatResponseMetadata(EventUnlockResult result)
        {
            return new JObject
            {
                ["statusCode"] = result.StatusCode,
                ["reasonPhrase"] = result.ReasonPhrase,
                ["requestUri"] = result.RequestUri,
                ["elapsedMilliseconds"] = result.ElapsedMilliseconds,
                ["responseBodyLength"] = result.ResponseBody?.Length ?? 0,
                ["responseBodyPreview"] = Truncate(result.ResponseBody ?? string.Empty, 500),
                ["requestHeaders"] = JObject.FromObject(result.RequestHeaders ?? new Dictionary<string, string>()),
                ["contentHeaders"] = JObject.FromObject(result.ContentHeaders ?? new Dictionary<string, string>()),
                ["responseHeaders"] = JObject.FromObject(result.ResponseHeaders ?? new Dictionary<string, string>())
            };
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            return value.Substring(0, maxLength) + "...";
        }

        private bool ApplyReplacement(ref string requestbody, dynamic ReplacementData, string statValue)
        {
            // For stats, REPLACEVALUE is substituted with the user-provided stat value
            string? replacement = ReplacementData.ReplacementType?.ToString();
            switch (replacement)
            {
                case "Replace":
                    string target = ReplacementData.Target.ToString();
                    string replaceWith = ReplacementData.Replacement.ToString();
                    // If the replacement value is REPLACEVALUE, substitute with the user's input
                    if (replaceWith == "REPLACEVALUE")
                        replaceWith = statValue;
                    else
                        replaceWith = replaceWith.Replace("REPLACEVALUE", statValue);
                    requestbody = requestbody.Replace(target, replaceWith);
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
                case "StatValue":
                    // Direct stat value replacement — the target placeholder gets the user's value
                    requestbody = requestbody.Replace(ReplacementData.Target.ToString(), statValue);
                    return true;
                default:
                    _snackbarService.Show("Error: Bad Stat Data",
                        $"Unknown replacement type: {replacement}",
                        NoticeAppearance.Danger, new NoticeIcon(NoticeSymbol.ErrorCircle24), _snackbarDuration);
                    return false;
            }
        }

        private static bool IsStatMetadataProperty(string propertyName)
        {
            return propertyName is "SuggestedValue" or "Confidence";
        }

        private string ApplyCommonReplacements(string requestbody, DateTime timestamp)
        {
            requestbody = requestbody.Replace("REPLACETIME", timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
            requestbody = requestbody.Replace("REPLACESEQ", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            requestbody = requestbody.Replace("REPLACEXUID", account.XUIDOnly);
            requestbody = requestbody.Replace("REPLACESESSIONGUID", Guid.NewGuid().ToString());
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
