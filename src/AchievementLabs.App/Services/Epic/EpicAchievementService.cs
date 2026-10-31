using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Epic;

public interface IEpicAchievementService
{
    bool IsEpicLauncherRunning();
    IReadOnlyList<EpicGameItem> GetInstalledGames();
    EpicEosConfig LoadConfig();
    void SaveConfig(EpicEosConfig config);
    IReadOnlyList<EpicAchievementItem> LoadAchievementPlaceholders(EpicGameItem? game);
    string GetCaptureRoot();
}

public sealed class EpicAchievementService : IEpicAchievementService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs",
        "epic-eos-config.json");
    private static readonly string CaptureRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs",
        "EpicCaptures");

    public bool IsEpicLauncherRunning()
    {
        try
        {
            return Process.GetProcessesByName("EpicGamesLauncher").Any() ||
                   Process.GetProcessesByName("EpicWebHelper").Any();
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<EpicGameItem> GetInstalledGames()
    {
        var games = new Dictionary<string, EpicGameItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestPath in EnumerateManifestPaths())
        {
            try
            {
                var game = ReadManifest(manifestPath);
                if (game is null)
                    continue;

                var key = FirstNonEmpty(game.CatalogItemId, game.ArtifactId, game.AppName, manifestPath);
                games[key] = game;
            }
            catch
            {
                // A single stale or partial launcher manifest should not block the library.
            }
        }

        foreach (var installed in ReadLauncherInstalledGames().Concat(ReadThirdPartyManagedApps()).Concat(ReadCapturedLibraryGames()))
        {
            var key = FirstNonEmpty(installed.CatalogItemId, installed.ArtifactId, installed.AppName, installed.InstallPath);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (games.TryGetValue(key, out var existing))
            {
                existing.DisplayName = FirstNonEmpty(existing.DisplayName, installed.DisplayName);
                existing.InstallPath = FirstNonEmpty(existing.InstallPath, installed.InstallPath);
                existing.Source = MergeSource(existing.Source, installed.Source);
            }
            else
            {
                games[key] = installed;
            }
        }

        ApplyCapturedAchievementProgress(games.Values);

        return games.Values
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public EpicEosConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new EpicEosConfig { CaptureFolder = CaptureRoot };

            var config = JsonConvert.DeserializeObject<EpicEosConfig>(File.ReadAllText(ConfigPath)) ?? new EpicEosConfig();
            if (string.IsNullOrWhiteSpace(config.CaptureFolder))
                config.CaptureFolder = CaptureRoot;
            config.Status = config.HasMinimumConfig ? "Configured locally" : "Missing required EOS identifiers";
            return config;
        }
        catch (Exception ex)
        {
            return new EpicEosConfig { CaptureFolder = CaptureRoot, Status = $"Config read failed: {ex.Message}" };
        }
    }

    public void SaveConfig(EpicEosConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
    }

    public IReadOnlyList<EpicAchievementItem> LoadAchievementPlaceholders(EpicGameItem? game)
    {
        if (game is null)
            return Array.Empty<EpicAchievementItem>();

        var captured = LoadCapturedAchievements(game);
        if (captured.Count > 0)
            return captured;

        return Array.Empty<EpicAchievementItem>();
    }

    private static string ConfiguredCaptureRoot()
    {
        try { return File.Exists(ConfigPath) && JsonConvert.DeserializeObject<EpicEosConfig>(File.ReadAllText(ConfigPath))?.CaptureFolder is { Length: > 0 } folder ? folder : CaptureRoot; }
        catch { return CaptureRoot; }
    }

    public string GetCaptureRoot() => CaptureRoot;

    private static IReadOnlyList<EpicAchievementItem> LoadCapturedAchievements(EpicGameItem game)
    {
        var achievements = new Dictionary<string, EpicAchievementItem>(StringComparer.OrdinalIgnoreCase);
        var needles = new[]
        {
            game.AppName,
            game.CatalogItemId,
            game.ArtifactId,
            game.NamespaceId,
            game.Title
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var playerStates = ReadCapturedPlayerAchievementStates().Where(state => MatchesEpicGame(state, needles))
            .GroupBy(state => state.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var record in Descendants(payload)
                         .OfType<JObject>()
                         .Select(obj => obj["data"]?["Achievement"]?["productAchievementsRecordBySandbox"] as JObject)
                         .Where(record => record is not null))
            {
                if (!MatchesEpicAchievementRecord(record!, needles))
                    continue;

                foreach (var row in record!["achievements"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var achievement = row["achievement"] as JObject ?? row;
                    AddEpicAchievement(achievements, achievement, "Epic achievement definition");
                }
            }

            foreach (var record in Descendants(payload)
                         .OfType<JObject>()
                         .Where(obj => obj["achievements"] is JArray &&
                                       (!string.IsNullOrWhiteSpace(ReadString(obj, "productId")) ||
                                        !string.IsNullOrWhiteSpace(ReadString(obj, "sandboxId")))))
            {
                if (!MatchesEpicAchievementRecord(record, needles))
                    continue;

                foreach (var row in record["achievements"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var achievement = row["achievement"] as JObject ?? row;
                    AddEpicAchievement(achievements, achievement, "Epic public achievement catalog");
                }
            }

            foreach (var obj in Descendants(payload).OfType<JObject>())
            {
                if (!LooksLikeAchievementObject(obj))
                    continue;

                if (needles.Count > 0 && !needles.Any(needle => obj.ToString(Formatting.None).Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    var parentText = obj.Parent?.Parent?.ToString(Formatting.None) ?? string.Empty;
                    if (!needles.Any(needle => parentText.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                var id = ReadString(obj, "achievementId", "achievement_id", "id", "name", "statName", "unlockedAchievement");
                var title = ReadString(obj, "displayName", "title", "name", "unlockedDisplayName", "lockedDisplayName");
                var description = FirstNonEmpty(
                    ReadString(obj, "description", "unlockedDescription", "lockedDescription"),
                    ReadString(obj, "shortDescription"));
                var state = BuildAchievementState(obj);

                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(title))
                    continue;

                AddEpicAchievement(achievements, obj, "Captured JSON fallback");
            }
        }

        foreach (var state in playerStates.Values.Where(state => MatchesEpicGame(state, needles)))
        {
            if (achievements.TryGetValue(state.Name, out var item))
            {
                ApplyCapturedPlayerState(item, state);
            }
            else
            {
                achievements[state.Name] = new EpicAchievementItem
                {
                    Id = state.Name,
                    Name = state.Name,
                    Description = $"Player achievement state captured from Epic. XP: {state.Xp}",
                    State = state.Unlocked ? state.UnlockDate : $"Progress {state.Progress}",
                    Unlocked = state.Unlocked,
                    Progress = state.Unlocked ? 100 : Math.Clamp(state.Progress * 100, 0, 100)
                };
            }
        }

        foreach (var notification in ReadCapturedEarnedNotifications().Where(notification => MatchesEpicGame(notification, needles)))
        {
            var key = $"captured-earned-{notification.Key}";
            if (achievements.ContainsKey(key))
                continue;

            achievements[key] = new EpicAchievementItem
            {
                Id = notification.Key,
                IsDefinition = false,
                Name = $"Captured earned achievement notification {notification.Ordinal}",
                Description = "Epic overlay telemetry reported an earned achievement notification for this title. The notification does not include the individual achievement name, so this row is capture evidence rather than a definition row.",
                State = notification.Timestamp,
                Progress = 100
            };
        }

        return achievements.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyCapturedAchievementProgress(IEnumerable<EpicGameItem> games)
    {
        var progressRecords = ReadCapturedProgressRecords().ToList();
        var earnedNotifications = ReadCapturedEarnedNotifications().ToList();
        if (progressRecords.Count == 0 && earnedNotifications.Count == 0)
            return;

        foreach (var game in games)
        {
            var needles = new[]
            {
                game.AppName,
                game.CatalogItemId,
                game.ArtifactId,
                game.NamespaceId,
                game.Title
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var progress = progressRecords.FirstOrDefault(record => MatchesEpicGame(record, needles));
            var notificationCount = earnedNotifications.Count(notification => MatchesEpicGame(notification, needles));
            var totalAchievements = progress is null ? 0 : progress.TotalAchievements;
            if (totalAchievements == 0)
                totalAchievements = CountCapturedDefinitionsForGame(needles);

            if (progress is not null && progress.TotalUnlocked > 0 && totalAchievements > 0)
            {
                game.AchievementSummary = $"{progress.TotalUnlocked}/{totalAchievements} unlocked / Epic capture";
                game.Progress = Math.Round(progress.TotalUnlocked / (double)totalAchievements * 100, 2);
                continue;
            }

            if (progress is not null && progress.TotalUnlocked > 0)
            {
                game.AchievementSummary = $"{progress.TotalUnlocked} unlocked / Epic capture";
                continue;
            }

            if (notificationCount > 0)
            {
                game.AchievementSummary = progress is not null
                    ? $"{notificationCount} earned overlay notifications / profile progress read {progress.TotalUnlocked}"
                    : $"{notificationCount} earned overlay notifications";
            }
        }
    }

    private static int CountCapturedDefinitionsForGame(IReadOnlyList<string> needles)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var record in Descendants(payload)
                         .OfType<JObject>()
                         .Where(obj => obj["achievements"] is JArray &&
                                       (!string.IsNullOrWhiteSpace(ReadString(obj, "productId")) ||
                                        !string.IsNullOrWhiteSpace(ReadString(obj, "sandboxId")))))
            {
                if (!MatchesEpicAchievementRecord(record, needles))
                    continue;

                foreach (var row in record["achievements"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var achievement = row["achievement"] as JObject ?? row;
                    var name = ReadString(achievement, "achievementName", "name", "id");
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }
        }

        return names.Count;
    }

    private static IEnumerable<CapturedEpicProgress> ReadCapturedProgressRecords()
    {
        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var record in Descendants(payload)
                         .OfType<JObject>()
                         .SelectMany(obj =>
                             (obj["data"]?["PlayerAchievement"]?["playerAchievementGameRecords"]?["records"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                             .Concat(obj["data"]?["PlayerAchievement"]?["playerAchievementGameRecordsBySandbox"]?["records"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())))
            {
                var sandboxId = ReadString(record, "sandboxId");
                var totalUnlocked = ReadInt(record, "totalUnlocked");
                var totalAchievements = record["achievementSets"]?
                    .Children<JObject>()
                    .Sum(item => Math.Max(0, ReadInt(item, "totalAchievements"))) ?? 0;

                yield return new CapturedEpicProgress(
                    sandboxId,
                    string.Empty,
                    string.Empty,
                    totalUnlocked,
                    totalAchievements);
            }
        }
    }

    private static IEnumerable<CapturedEpicPlayerState> ReadCapturedPlayerAchievementStates()
    {
        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var playerAchievement in Descendants(payload)
                         .OfType<JObject>()
                         .Select(obj => obj["playerAchievement"] as JObject)
                         .Where(obj => obj is not null))
            {
                var name = ReadString(playerAchievement!, "achievementName", "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                yield return new CapturedEpicPlayerState(
                    name,
                    ReadString(playerAchievement!, "sandboxId"),
                    string.Empty,
                    string.Empty,
                    IsTruthy(ReadString(playerAchievement!, "unlocked")),
                    ReadDouble(playerAchievement!, "progress"),
                    ReadString(playerAchievement!, "unlockDate"),
                    ReadInt(playerAchievement!, "XP", "xp"));
            }
        }
    }

    private static IEnumerable<CapturedEpicNotification> ReadCapturedEarnedNotifications()
    {
        var ordinal = 1;

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var obj in Descendants(payload).OfType<JObject>())
            {
                var notificationType = ReadString(obj, "NotificationType");
                var eventName = ReadString(obj, "EventName", "Event");
                if (!notificationType.Equals("earned-egs-achievement", StringComparison.OrdinalIgnoreCase) &&
                    !eventName.Contains("achievement", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var productId = ReadString(obj, "CurrentGameId", "ProductId");
                var sandboxId = ReadString(obj, "SandboxId");
                var gameName = ReadString(obj, "CurrentGameName", "ProductName");
                if (string.IsNullOrWhiteSpace(productId) &&
                    string.IsNullOrWhiteSpace(sandboxId) &&
                    string.IsNullOrWhiteSpace(gameName))
                {
                    continue;
                }

                var timestamp = FirstNonEmpty(ReadString(obj, "Timestamp"), "Captured earned notification");
                yield return new CapturedEpicNotification(
                    $"{productId}-{sandboxId}-{ordinal}",
                    ordinal++,
                    productId,
                    sandboxId,
                    gameName,
                    timestamp);
            }
        }
    }

    private static bool MatchesEpicGame(CapturedEpicProgress progress, IReadOnlyList<string> needles) =>
        MatchesEpicGame(progress.SandboxId, progress.ProductId, progress.GameName, needles);

    private static bool MatchesEpicGame(CapturedEpicNotification notification, IReadOnlyList<string> needles) =>
        MatchesEpicGame(notification.SandboxId, notification.ProductId, notification.GameName, needles);

    private static bool MatchesEpicGame(CapturedEpicPlayerState state, IReadOnlyList<string> needles) =>
        MatchesEpicGame(state.SandboxId, state.ProductId, state.GameName, needles);

    private static bool MatchesEpicGame(string sandboxId, string productId, string gameName, IReadOnlyList<string> needles)
    {
        if (needles.Count == 0)
            return true;

        return needles.Any(needle =>
            needle.Equals(sandboxId, StringComparison.OrdinalIgnoreCase) ||
            needle.Equals(productId, StringComparison.OrdinalIgnoreCase) ||
            needle.Equals(gameName, StringComparison.OrdinalIgnoreCase) ||
            gameName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            needle.Contains(gameName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<JToken> ReadCapturedJsonPayloads()
    {
        var captureFolder = ConfiguredCaptureRoot();
        if (!Directory.Exists(captureFolder))
            yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(captureFolder, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".har", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                .Take(1000)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            foreach (var token in ReadJsonTokensFromFile(file))
                yield return token;
        }
    }

    private static IEnumerable<JToken> ReadJsonTokensFromFile(string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch
        {
            yield break;
        }

        if (TryParseJson(text, out var fullToken))
        {
            foreach (var extracted in ExtractPayloadTokens(fullToken))
                yield return extracted;
            yield break;
        }

        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseJson(line, out var lineToken))
            {
                foreach (var extracted in ExtractPayloadTokens(lineToken))
                    yield return extracted;
            }
        }
    }

    private static IEnumerable<JToken> ExtractPayloadTokens(JToken token)
    {
        yield return token;

        foreach (var textValue in Descendants(token)
                     .OfType<JValue>()
                     .Where(value => value.Type == JTokenType.String)
                     .Select(value => value.Value<string>() ?? string.Empty)
                     .Where(value => value.TrimStart().StartsWith("{") || value.TrimStart().StartsWith("[")))
        {
            if (TryParseJson(textValue, out var nested))
                yield return nested;
        }
    }

    private static bool TryParseJson(string text, out JToken token)
    {
        try
        {
            token = JToken.Parse(text);
            return true;
        }
        catch
        {
            token = JValue.CreateNull();
            return false;
        }
    }

    private static bool LooksLikeAchievementObject(JObject obj)
    {
        var keys = string.Join(" ", obj.Properties().Select(prop => prop.Name));
        var type = ReadString(obj, "type", "__typename");
        return keys.Contains("achievement", StringComparison.OrdinalIgnoreCase) ||
               keys.Contains("unlockedDisplayName", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("achievement", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesEpicAchievementRecord(JObject record, IReadOnlyList<string> needles)
    {
        if (needles.Count == 0)
            return true;

        var recordKeys = new[]
        {
            ReadString(record, "productId"),
            ReadString(record, "sandboxId")
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

        return recordKeys.Any(key => needles.Any(needle => key.Equals(needle, StringComparison.OrdinalIgnoreCase))) ||
               needles.Any(needle => record.ToString(Formatting.None).Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddEpicAchievement(IDictionary<string, EpicAchievementItem> achievements, JObject obj, string source)
    {
        var id = ReadString(obj, "achievementId", "achievement_id", "id", "achievementName", "name", "statName", "unlockedAchievement");
        var title = ReadString(obj, "displayName", "title", "name", "unlockedDisplayName", "lockedDisplayName");
        var description = FirstNonEmpty(
            ReadString(obj, "description", "unlockedDescription", "lockedDescription"),
            ReadString(obj, "shortDescription"));
        var xp = ReadString(obj, "XP", "xp");
        var rarity = ReadString(obj["rarity"] as JObject ?? new JObject(), "percent");
        var state = BuildAchievementState(obj);

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(title))
            return;

        var key = FirstNonEmpty(id, title);
        if (achievements.ContainsKey(key))
            return;

        achievements[key] = new EpicAchievementItem
        {
            Id = id,
            Name = FirstNonEmpty(title, id),
            Description = string.IsNullOrWhiteSpace(xp) && string.IsNullOrWhiteSpace(rarity)
                ? description
                : $"{description}  XP: {xp}  Rarity: {rarity}%",
            State = string.IsNullOrWhiteSpace(source) ? state : $"{state} / {source}",
            Unlocked = bool.TryParse(ReadString(obj, "unlocked", "isUnlocked", "isCompleted", "completed"), out var unlocked) ? unlocked : DateTimeOffset.TryParse(ReadString(obj, "unlockTime", "unlockedAt", "completionDate", "completedAt"), out _) ? true : null,
            ImageUrl = ReadString(obj, "unlockedIconUrl", "lockedIconUrl", "iconUrl", "icon"),
            GlobalPercent = double.TryParse(rarity, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent is >= 0 and <= 100 ? percent : null,
            Progress = IsUnlockedState(state) ? 100 : 0
        };
    }

    private static void ApplyCapturedPlayerState(EpicAchievementItem item, CapturedEpicPlayerState state)
    {
        item.State = state.Unlocked ? state.UnlockDate : $"Progress {state.Progress}";
        item.Unlocked = state.Unlocked;
        item.Progress = state.Unlocked ? 100 : Math.Clamp(state.Progress * 100, 0, 100);
        if (!string.IsNullOrWhiteSpace(state.UnlockDate) && !item.Description.Contains(state.UnlockDate, StringComparison.OrdinalIgnoreCase))
            item.Description = $"{item.Description}  Unlocked: {state.UnlockDate}";
    }

    private static string BuildAchievementState(JObject obj)
    {
        var unlocked = ReadString(obj, "unlocked", "isUnlocked", "isCompleted", "completed");
        var unlockTime = ReadString(obj, "unlockTime", "unlockedAt", "completionDate", "completedAt");
        var progress = ReadString(obj, "progress", "percentComplete", "completionPercentage");

        if (IsTruthy(unlocked))
            return FirstNonEmpty(unlockTime, "Unlocked");

        if (!string.IsNullOrWhiteSpace(progress))
            return $"Progress {progress}";

        return "Locked";
    }

    private static bool IsUnlockedState(string state) =>
        state.Equals("Unlocked", StringComparison.OrdinalIgnoreCase) ||
        DateTimeOffset.TryParse(state, out _);

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<EpicGameItem> ReadCapturedLibraryGames()
    {
        var playtimeByArtifact = ReadCapturedPlaytime();

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var record in Descendants(payload)
                         .OfType<JObject>()
                         .SelectMany(obj => obj["data"]?["Library"]?["libraryItems"]?["records"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()))
            {
                var catalogItem = record["catalogItem"] as JObject;
                var appName = ReadString(record, "appName");
                var artifactId = FirstNonEmpty(
                    ReadString(record, "artifactId"),
                    record["dependencies"]?.Children<JObject>().Select(dep => ReadString(dep, "artifactId")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty);

                var title = FirstNonEmpty(ReadString(catalogItem ?? new JObject(), "title"), ReadString(record, "sandboxName"), appName);
                var catalogId = FirstNonEmpty(ReadString(record, "catalogItemId"), ReadString(catalogItem ?? new JObject(), "id"));
                var namespaceId = FirstNonEmpty(ReadString(record, "namespace"), ReadString(catalogItem ?? new JObject(), "namespace"));
                var imageUrl = ReadEpicImageUrl(catalogItem);
                var playtime = FirstNonEmpty(
                    playtimeByArtifact.TryGetValue(appName, out var appTime) ? appTime : string.Empty,
                    playtimeByArtifact.TryGetValue(artifactId, out var artifactTime) ? artifactTime : string.Empty);

                if (string.IsNullOrWhiteSpace(title))
                    continue;

                yield return new EpicGameItem
                {
                    DisplayName = title,
                    AppName = appName,
                    CatalogItemId = catalogId,
                    ArtifactId = artifactId,
                    NamespaceId = namespaceId,
                    ImageUrl = FirstNonEmpty(imageUrl, "pack://application:,,,/Assets/achievement-labs-icon.png"),
                    AchievementSummary = string.IsNullOrWhiteSpace(playtime) ? "Captured library record" : $"Playtime: {playtime}",
                    Source = "Epic captured library",
                    Progress = 0
                };
            }
        }
    }

    private static Dictionary<string, string> ReadCapturedPlaytime()
    {
        var playtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var total in Descendants(payload)
                         .OfType<JObject>()
                         .SelectMany(obj => obj["data"]?["PlaytimeTracking"]?["total"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()))
            {
                var artifactId = ReadString(total, "artifactId");
                var totalTime = ReadString(total, "totalTime");
                if (!string.IsNullOrWhiteSpace(artifactId) && !string.IsNullOrWhiteSpace(totalTime))
                    playtimes[artifactId] = totalTime;
            }
        }

        return playtimes;
    }

    private static string ReadEpicImageUrl(JObject? catalogItem)
    {
        if (catalogItem?["keyImages"] is not JArray images)
            return string.Empty;

        foreach (var preferred in new[] { "DieselGameBox", "OfferImageTall", "Thumbnail", "DieselStoreFrontWide" })
        {
            var match = images.OfType<JObject>()
                .FirstOrDefault(image => ReadString(image, "type").Equals(preferred, StringComparison.OrdinalIgnoreCase));
            var url = ReadString(match ?? new JObject(), "url");
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        return ReadString(images.OfType<JObject>().FirstOrDefault() ?? new JObject(), "url");
    }

    private static IEnumerable<string> EnumerateManifestPaths()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Manifests"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Data")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var manifest in Directory.EnumerateFiles(root, "*.item", SearchOption.TopDirectoryOnly))
                yield return manifest;
        }
    }

    private static EpicGameItem? ReadManifest(string path)
    {
        var json = JObject.Parse(File.ReadAllText(path));
        var displayName = ReadString(json, "DisplayName");
        var appName = ReadString(json, "AppName");
        var catalogId = ReadString(json, "CatalogItemId");
        var artifactId = ReadString(json, "ArtifactId");
        var namespaceId = ReadString(json, "NamespaceId");
        var installPath = ReadString(json, "InstallLocation");

        if (string.IsNullOrWhiteSpace(displayName) &&
            string.IsNullOrWhiteSpace(appName) &&
            string.IsNullOrWhiteSpace(catalogId) &&
            string.IsNullOrWhiteSpace(artifactId))
        {
            return null;
        }

        return new EpicGameItem
        {
            DisplayName = FirstNonEmpty(displayName, appName),
            AppName = appName,
            CatalogItemId = catalogId,
            ArtifactId = artifactId,
            NamespaceId = namespaceId,
            InstallPath = installPath,
            ManifestPath = path,
            Source = "Launcher manifest"
        };
    }

    private static IEnumerable<EpicGameItem> ReadLauncherInstalledGames()
    {
        var launcherInstalled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "UnrealEngineLauncher",
            "LauncherInstalled.dat");

        if (!File.Exists(launcherInstalled))
            yield break;

        JObject json;
        try
        {
            json = JObject.Parse(File.ReadAllText(launcherInstalled));
        }
        catch
        {
            yield break;
        }

        foreach (var token in json["InstallationList"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var appName = ReadString(token, "AppName");
            var artifactId = ReadString(token, "ArtifactId");
            var installPath = ReadString(token, "InstallLocation");

            yield return new EpicGameItem
            {
                DisplayName = FirstNonEmpty(ReadString(token, "AppVersionString"), appName, artifactId),
                AppName = appName,
                ArtifactId = artifactId,
                InstallPath = installPath,
                Source = "Launcher installed list"
            };
        }
    }

    private static IEnumerable<EpicGameItem> ReadThirdPartyManagedApps()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "ThirPartyManagedApps");

        if (!Directory.Exists(root))
            yield break;

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            JObject json;
            try
            {
                json = JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                continue;
            }

            yield return new EpicGameItem
            {
                DisplayName = FirstNonEmpty(ReadString(json, "Title"), ReadString(json, "DisplayName"), ReadString(json, "AppName")),
                AppName = ReadString(json, "AppName"),
                CatalogItemId = FirstNonEmpty(ReadString(json, "CatalogID"), ReadString(json, "CatalogItemId")),
                ArtifactId = ReadString(json, "ArtifactId"),
                NamespaceId = FirstNonEmpty(ReadString(json, "Namespace"), ReadString(json, "NamespaceId")),
                InstallPath = ResolveThirdPartyInstallPath(json),
                ManifestPath = path,
                Source = $"Third-party managed app / {FirstNonEmpty(ReadString(json, "Provider"), "Epic")}"
            };
        }
    }

    private static IEnumerable<EpicGameItem> ReadJsonCacheGames()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Take(500).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var path in files)
            {
                foreach (var item in ReadGameLikeObjects(path))
                    yield return item;
            }
        }
    }

    private static IEnumerable<EpicGameItem> ReadGameLikeObjects(string path)
    {
        JToken token;
        try
        {
            token = JToken.Parse(File.ReadAllText(path));
        }
        catch
        {
            yield break;
        }

        foreach (var obj in Descendants(token).OfType<JObject>())
        {
            var title = ReadString(obj, "Title", "DisplayName", "title", "displayName", "name");
            var appName = ReadString(obj, "AppName", "appName");
            var catalogId = ReadString(obj, "CatalogID", "CatalogItemId", "catalogId", "catalogItemId");
            var namespaceId = ReadString(obj, "Namespace", "NamespaceId", "namespace", "namespaceId");
            var installPath = ReadString(obj, "InstallLocation", "InstallPath", "installLocation", "installPath");

            if (string.IsNullOrWhiteSpace(title) ||
                (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(catalogId) && string.IsNullOrWhiteSpace(namespaceId) && string.IsNullOrWhiteSpace(installPath)))
            {
                continue;
            }

            yield return new EpicGameItem
            {
                DisplayName = title,
                AppName = appName,
                CatalogItemId = catalogId,
                NamespaceId = namespaceId,
                InstallPath = installPath,
                ManifestPath = path,
                Source = "Launcher JSON cache"
            };
        }
    }

    private static string ResolveThirdPartyInstallPath(JObject json)
    {
        var explicitPath = ReadString(json, "InstallLocation", "InstallPath");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var registryPath = ReadString(json, "RegistryPath");
        var registryKey = ReadString(json, "RegistryKey");
        if (string.IsNullOrWhiteSpace(registryPath) || string.IsNullOrWhiteSpace(registryKey))
            return string.Empty;

        foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(registryPath);
                    var value = key?.GetValue(registryKey)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
                catch
                {
                    // Some provider registry locations are optional or access-restricted.
                }
            }
        }

        return string.Empty;
    }

    private static string ReadString(JObject json, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (json.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var value))
                return value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int ReadInt(JObject json, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!json.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var value))
                continue;

            if (value.Type == JTokenType.Integer)
                return value.Value<int>();

            if (int.TryParse(value.ToString(), out var parsed))
                return parsed;
        }

        return 0;
    }

    private static double ReadDouble(JObject json, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!json.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var value))
                continue;

            if (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                return value.Value<double>();

            if (double.TryParse(value.ToString(), out var parsed))
                return parsed;
        }

        return 0;
    }

    private static IEnumerable<JToken> Descendants(JToken token)
    {
        yield return token;
        foreach (var child in token.Children())
        {
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string MergeSource(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return right;

        if (string.IsNullOrWhiteSpace(right) || left.Contains(right, StringComparison.OrdinalIgnoreCase))
            return left;

        return $"{left} + {right}";
    }

    private sealed record CapturedEpicProgress(
        string SandboxId,
        string ProductId,
        string GameName,
        int TotalUnlocked,
        int TotalAchievements);

    private sealed record CapturedEpicNotification(
        string Key,
        int Ordinal,
        string ProductId,
        string SandboxId,
        string GameName,
        string Timestamp);

    private sealed record CapturedEpicPlayerState(
        string Name,
        string SandboxId,
        string ProductId,
        string GameName,
        bool Unlocked,
        double Progress,
        string UnlockDate,
        int Xp);
}
