using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Ubisoft;

public interface IUbisoftAchievementService
{
    bool IsUbisoftConnectRunning();
    IReadOnlyList<UbisoftGameItem> GetInstalledGames();
    UbisoftServiceConfig LoadConfig();
    void SaveConfig(UbisoftServiceConfig config);
    IReadOnlyList<UbisoftAchievementItem> LoadAchievementPlaceholders(UbisoftGameItem? game);
    IReadOnlyList<UbisoftStatItem> LoadStatPlaceholders(UbisoftGameItem? game);
    IReadOnlyList<UbisoftChallengeItem> LoadChallengePlaceholders(UbisoftGameItem? game);
    IReadOnlyList<UbisoftCrossProgressionItem> LoadCrossProgressionCandidates();
    IReadOnlyList<UbisoftGameItem> LoadCapturedGames();
    string GetCaptureRoot();
}

public sealed class UbisoftAchievementService : IUbisoftAchievementService
{
    private static readonly Regex GameIdRegex = new(@"(?<id>\d{2,})", RegexOptions.Compiled);
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs",
        "ubisoft-service-config.json");
    private static readonly string CaptureRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs",
        "UbisoftCaptures");

    public bool IsUbisoftConnectRunning()
    {
        try
        {
            return Process.GetProcessesByName("UbisoftConnect").Any() ||
                   Process.GetProcessesByName("upc").Any() ||
                   Process.GetProcessesByName("Uplay").Any();
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<UbisoftGameItem> GetInstalledGames()
    {
        var games = new Dictionary<string, UbisoftGameItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in ReadRegistryGames().Concat(ReadCacheGames()).Concat(LoadCapturedGames()))
        {
            var key = FirstNonEmpty(game.GameId, game.InstallPath, game.DisplayName);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (games.TryGetValue(key, out var existing))
            {
                existing.DisplayName = FirstNonEmpty(existing.DisplayName, game.DisplayName);
                existing.InstallPath = FirstNonEmpty(existing.InstallPath, game.InstallPath);
                existing.LaunchUri = FirstNonEmpty(existing.LaunchUri, game.LaunchUri);
                existing.Source = MergeSource(existing.Source, game.Source);
            }
            else
            {
                games[key] = game;
            }
        }

        return games.Values
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public UbisoftServiceConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new UbisoftServiceConfig { CaptureFolder = CaptureRoot };

            var config = JsonConvert.DeserializeObject<UbisoftServiceConfig>(File.ReadAllText(ConfigPath)) ?? new UbisoftServiceConfig();
            if (string.IsNullOrWhiteSpace(config.CaptureFolder))
                config.CaptureFolder = CaptureRoot;
            config.Status = config.HasMinimumConfig ? "Configured locally" : "Saved, but missing credentials/session data";
            return config;
        }
        catch (Exception ex)
        {
            return new UbisoftServiceConfig { CaptureFolder = CaptureRoot, Status = $"Config read failed: {ex.Message}" };
        }
    }

    public void SaveConfig(UbisoftServiceConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
    }

    public IReadOnlyList<UbisoftAchievementItem> LoadAchievementPlaceholders(UbisoftGameItem? game)
    {
        if (game is null)
            return Array.Empty<UbisoftAchievementItem>();

        var captured = LoadCapturedAchievements(game);
        if (captured.Count > 0)
            return captured;

        return Array.Empty<UbisoftAchievementItem>();
    }

    public IReadOnlyList<UbisoftStatItem> LoadStatPlaceholders(UbisoftGameItem? game)
    {
        if (game is null)
            return Array.Empty<UbisoftStatItem>();

        var captured = LoadCapturedStats(game);
        if (captured.Count > 0)
            return captured;

        return new[]
        {
            new UbisoftStatItem
            {
                Id = "profile-stats",
                Name = "Ubisoft Connect captured stats",
                Value = "No captured stat records found yet",
                Source = "Ubisoft capture parser",
                Status = "Capture needed"
            },
            new UbisoftStatItem
            {
                Id = "local-save-stats",
                Name = "Local save/stat files",
                Value = string.IsNullOrWhiteSpace(game.InstallPath) ? "Install path unavailable" : game.InstallPath,
                Source = "Local files",
                Status = "Inspectable"
            },
            new UbisoftStatItem
            {
                Id = "write-support",
                Name = "Stat editing",
                Value = "Official/test environment required",
                Source = "Guardrail",
                Status = "Disabled"
            }
        };
    }

    public IReadOnlyList<UbisoftChallengeItem> LoadChallengePlaceholders(UbisoftGameItem? game)
    {
        if (game is null)
            return Array.Empty<UbisoftChallengeItem>();

        var captured = LoadCapturedChallenges(game);
        if (captured.Count > 0)
            return captured;

        return new[]
        {
            new UbisoftChallengeItem
            {
                Id = "core-challenges",
                Name = "Core challenges",
                Description = "No captured challenge records found yet. Capture Ubisoft Connect traffic while viewing challenges.",
                Progress = "Capture needed"
            },
            new UbisoftChallengeItem
            {
                Id = "time-limited-challenges",
                Name = "Time-limited challenges",
                Description = "Track active challenge windows and completion state from official or captured read endpoints.",
                Progress = "Pending"
            },
            new UbisoftChallengeItem
            {
                Id = "challenge-editing",
                Name = "Challenge editing",
                Description = "Production challenge completion writes are disabled unless backed by authorized developer/test credentials.",
                Progress = "Disabled",
                Status = "Write disabled"
            }
        };
    }

    public IReadOnlyList<UbisoftCrossProgressionItem> LoadCrossProgressionCandidates()
    {
        var captured = LoadCapturedCrossProgressionCandidates();
        if (captured.Count > 0)
            return captured;

        return GetInstalledGames()
            .Select(game => new UbisoftCrossProgressionItem
            {
                Title = game.Title,
                GameId = game.GameId,
                DetectedPlatforms = "PC",
                Evidence = FirstNonEmpty(game.Source, game.InstallPath),
                Status = LooksLikeModernConnectTitle(game) ? "Possible Ubisoft Connect cloud/cross-progression candidate" : "Local evidence only"
            })
            .ToList();
    }

    private static string ConfiguredCaptureRoot()
    {
        try { return File.Exists(ConfigPath) && JsonConvert.DeserializeObject<UbisoftServiceConfig>(File.ReadAllText(ConfigPath))?.CaptureFolder is { Length: > 0 } folder ? folder : CaptureRoot; }
        catch { return CaptureRoot; }
    }

    public string GetCaptureRoot() => CaptureRoot;

    public IReadOnlyList<UbisoftGameItem> LoadCapturedGames()
    {
        var games = new Dictionary<string, UbisoftGameItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in ReadCapturedGraphQlGames())
        {
            var key = FirstNonEmpty(game.GameId, game.DisplayName, game.Title);
            if (!string.IsNullOrWhiteSpace(key) && !games.ContainsKey(key))
                games[key] = game;
        }

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var obj in Descendants(payload).OfType<JObject>())
            {
                var title = ReadJsonString(obj, "name", "title", "displayName", "gameName", "productName");
                var id = ReadJsonString(obj, "gameId", "productId", "spaceId", "applicationId", "appId", "id");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id) || !LooksLikeGameObject(obj))
                    continue;

                var key = FirstNonEmpty(id, title);
                if (string.IsNullOrWhiteSpace(key) || games.ContainsKey(key))
                    continue;

                games[key] = new UbisoftGameItem
                {
                    GameId = id,
                    DisplayName = title,
                    Source = "Ubisoft captured response",
                    AchievementSummary = "Captured Ubisoft Connect metadata",
                    Progress = 0
                };
            }
        }

        return games.Values.ToList();
    }

    private static IEnumerable<UbisoftGameItem> ReadRegistryGames()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var rootName in new[] { "Games", "Installs" })
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey($@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\{rootName}") ??
                                baseKey.OpenSubKey($@"SOFTWARE\Ubisoft\Launcher\{rootName}");
                if (key is null)
                    continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey is null)
                        continue;

                    var installDir = ReadRegistryValue(subKey, "InstallDir", "InstallPath", "Directory");
                    var displayName = ReadRegistryValue(subKey, "DisplayName", "Name", "GameName");
                    var gameId = ReadRegistryValue(subKey, "GameId", "ProductId", "UplayId");
                    gameId = FirstNonEmpty(gameId, GameIdRegex.Match(subKeyName).Groups["id"].Value);

                    yield return new UbisoftGameItem
                    {
                        GameId = gameId,
                        DisplayName = displayName,
                        InstallPath = installDir,
                        Source = $"Registry {view}",
                        LaunchUri = string.IsNullOrWhiteSpace(gameId) ? string.Empty : $"uplay://launch/{gameId}/0"
                    };
                }
            }
        }
    }

    private IReadOnlyList<UbisoftStatItem> LoadCapturedStats(UbisoftGameItem game)
    {
        var stats = new Dictionary<string, UbisoftStatItem>(StringComparer.OrdinalIgnoreCase);
        var gameNeedle = FirstNonEmpty(game.GameId, game.Title);

        foreach (var graphGame in ReadCapturedGraphQlGameObjects().Where(item => MatchesCapturedGame(item, gameNeedle)))
        {
            var viewerMeta = graphGame["viewer"]?["meta"] as JObject;
            if (viewerMeta is null)
                continue;

            AddStat(stats, "playTime", "Play Time", ReadJsonString(viewerMeta, "playTime"), "Ubisoft GraphQL game.viewer.meta");
            AddStat(stats, "completionPercentage", "Completion Percentage", ReadJsonString(viewerMeta, "completionPercentage"), "Ubisoft GraphQL game.viewer.meta");
            AddStat(stats, "lastPlayedDate", "Last Played", ReadJsonString(viewerMeta, "lastPlayedDate"), "Ubisoft GraphQL game.viewer.meta");

            var achievements = viewerMeta["achievements"] as JObject;
            if (achievements is not null)
            {
                AddStat(stats, "achievements.totalCount", "Achievements Total", ReadJsonString(achievements, "totalCount"), "Ubisoft GraphQL achievements");
                AddStat(stats, "achievements.completedCount", "Achievements Completed", ReadJsonString(achievements, "completedCount"), "Ubisoft GraphQL achievements");
            }

            var classicChallenges = viewerMeta["classicChallenges"] as JObject;
            if (classicChallenges is not null)
                AddStat(stats, "classicChallenges.totalCount", "Classic Challenges Total", ReadJsonString(classicChallenges, "totalCount"), "Ubisoft GraphQL challenges");

            var periodicChallenges = viewerMeta["periodicChallenges"] as JObject;
            if (periodicChallenges is not null)
                AddStat(stats, "periodicChallenges.totalCount", "Periodic Challenges Total", ReadJsonString(periodicChallenges, "totalCount"), "Ubisoft GraphQL challenges");

            var rewards = viewerMeta["rewards"] as JObject;
            if (rewards is not null)
                AddStat(stats, "rewards.total", "Rewards Captured", ExtractNodes(rewards).Count.ToString(), "Ubisoft GraphQL rewards");
        }

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var obj in Descendants(payload).OfType<JObject>())
            {
                if (!MatchesGameContext(obj, gameNeedle))
                    continue;

                if (!LooksLikeStatObject(obj))
                    continue;

                var id = ReadJsonString(obj, "statId", "statName", "name", "key", "id");
                var name = ReadJsonString(obj, "displayName", "localizedName", "name", "statName", "key");
                var value = ReadJsonString(obj, "value", "currentValue", "progress", "amount", "total", "count");
                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                    continue;

                var key = FirstNonEmpty(id, name);
                if (stats.ContainsKey(key))
                    continue;

                stats[key] = new UbisoftStatItem
                {
                    Id = id,
                    Name = FirstNonEmpty(name, id),
                    Value = value,
                    Source = "Ubisoft captured response",
                    Status = "Read-only captured"
                };
            }
        }

        return stats.Values.ToList();
    }

    private IReadOnlyList<UbisoftAchievementItem> LoadCapturedAchievements(UbisoftGameItem game)
    {
        var achievements = new Dictionary<string, UbisoftAchievementItem>(StringComparer.OrdinalIgnoreCase);
        var gameNeedle = FirstNonEmpty(game.GameId, game.Title);

        foreach (var graphGame in ReadCapturedGraphQlGameObjects().Where(item => MatchesCapturedGame(item, gameNeedle)))
        {
            var meta = graphGame["viewer"]?["meta"] as JObject;
            var connection = meta?["achievements"] as JObject;
            if (connection is null)
                continue;

            foreach (var node in ExtractNodes(connection).OfType<JObject>())
            {
                var id = ReadJsonString(node, "achievementId", "id");
                var title = ReadJsonString(node, "title", "name");
                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(title))
                    continue;

                var viewerMeta = node["viewer"]?["meta"] as JObject;
                var completed = ReadJsonString(viewerMeta ?? new JObject(), "isCompleted");
                var completionDate = ReadJsonString(viewerMeta ?? new JObject(), "completionDate");
                var key = FirstNonEmpty(id, title);

                achievements[key] = new UbisoftAchievementItem
                {
                    Id = id,
                    Name = title,
                    Description = ReadJsonString(node, "description"),
                    State = IsTruthy(completed) ? FirstNonEmpty(completionDate, "Completed") : "Locked",
                    Unlocked = bool.TryParse(completed, out var isCompleted) ? isCompleted : completed == "1" ? true : completed == "0" ? false : null,
                    ImageUrl = ReadJsonString(node, "imageUrl", "iconUrl", "thumbnailUrl"),
                    Progress = IsTruthy(completed) ? 100 : 0
                };
            }
        }

        return achievements.Values.ToList();
    }

    private IReadOnlyList<UbisoftChallengeItem> LoadCapturedChallenges(UbisoftGameItem game)
    {
        var challenges = new Dictionary<string, UbisoftChallengeItem>(StringComparer.OrdinalIgnoreCase);
        var gameNeedle = FirstNonEmpty(game.GameId, game.Title);

        foreach (var graphGame in ReadCapturedGraphQlGameObjects().Where(item => MatchesCapturedGame(item, gameNeedle)))
        {
            var meta = graphGame["viewer"]?["meta"] as JObject;
            if (meta is null)
                continue;

            foreach (var connectionName in new[] { "classicChallenges", "periodicChallenges", "rewards" })
            {
                var connection = meta[connectionName] as JObject;
                if (connection is null)
                    continue;

                var total = ReadJsonString(connection, "totalCount");
                if (!string.IsNullOrWhiteSpace(total))
                {
                    var totalKey = $"{connectionName}.totalCount";
                    challenges[totalKey] = new UbisoftChallengeItem
                    {
                        Id = totalKey,
                        Name = $"{SplitCamel(connectionName)} total",
                        Description = "Count returned by Ubisoft Connect GraphQL.",
                        Progress = total,
                        Status = "Read-only captured"
                    };
                }

                foreach (var node in ExtractNodes(connection).OfType<JObject>())
                {
                    var id = ReadJsonString(node, "rewardId", "challengeId", "id");
                    var name = ReadJsonString(node, "name", "title", "displayName");
                    if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                        continue;

                    var viewerMeta = node["viewer"]?["meta"] as JObject;
                    var status = BuildRewardStatus(viewerMeta);
                    var key = FirstNonEmpty(id, name);

                    challenges[key] = new UbisoftChallengeItem
                    {
                        Id = id,
                        Name = name,
                        Description = FirstNonEmpty(ReadJsonString(node, "description"), ReadJsonString(node, "instructionHTML", "conditionHTML")),
                        Progress = FirstNonEmpty(ReadJsonString(viewerMeta ?? new JObject(), "redeemedCount"), ReadJsonString(node, "count")),
                        Status = FirstNonEmpty(status, "Read-only captured")
                    };
                }
            }
        }

        foreach (var payload in ReadCapturedJsonPayloads())
        {
            foreach (var obj in Descendants(payload).OfType<JObject>())
            {
                if (!MatchesGameContext(obj, gameNeedle))
                    continue;

                if (!LooksLikeChallengeObject(obj))
                    continue;

                var id = ReadJsonString(obj, "challengeId", "id", "name", "slug");
                var name = ReadJsonString(obj, "title", "displayName", "name", "localizedName");
                var description = ReadJsonString(obj, "description", "shortDescription", "longDescription");
                var progress = FirstNonEmpty(
                    ReadJsonString(obj, "progress", "completion", "currentProgress"),
                    BuildProgressText(obj));

                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                    continue;

                var key = FirstNonEmpty(id, name);
                if (challenges.ContainsKey(key))
                    continue;

                challenges[key] = new UbisoftChallengeItem
                {
                    Id = id,
                    Name = FirstNonEmpty(name, id),
                    Description = description,
                    Progress = progress,
                    Status = "Read-only captured"
                };
            }
        }

        return challenges.Values.ToList();
    }

    private static IEnumerable<UbisoftGameItem> ReadCapturedGraphQlGames()
    {
        foreach (var game in ReadCapturedGraphQlGameObjects())
        {
            var id = ReadJsonString(game, "id", "spaceId");
            var name = ReadJsonString(game, "name");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                continue;

            var platform = game["platform"] as JObject;
            var viewerMeta = game["viewer"]?["meta"] as JObject;
            var completed = ReadJsonString(viewerMeta?["achievements"] as JObject ?? new JObject(), "completedCount");
            var total = ReadJsonString(viewerMeta?["achievements"] as JObject ?? new JObject(), "totalCount");
            var playTime = ReadJsonString(viewerMeta ?? new JObject(), "playTime");
            var lastPlayed = ReadJsonString(viewerMeta ?? new JObject(), "lastPlayedDate");

            var summaryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(completed) || !string.IsNullOrWhiteSpace(total))
                summaryParts.Add($"{FirstNonEmpty(completed, "0")}/{FirstNonEmpty(total, "?")} achievements");
            if (!string.IsNullOrWhiteSpace(playTime))
                summaryParts.Add($"Play time: {playTime}");
            if (!string.IsNullOrWhiteSpace(lastPlayed))
                summaryParts.Add($"Last played: {lastPlayed}");

            yield return new UbisoftGameItem
            {
                GameId = id,
                DisplayName = name,
                ImageUrl = FirstNonEmpty(ReadJsonString(game, "lowThumbnailUrl", "avatarUrl", "lowBoxArtUrl"), "pack://application:,,,/Assets/achievement-labs-icon.png"),
                Source = $"Ubisoft GraphQL capture / {ReadJsonString(platform ?? new JObject(), "name", "type")}",
                AchievementSummary = summaryParts.Count == 0 ? "Captured Ubisoft Connect metadata" : string.Join(" / ", summaryParts),
                Progress = TryPercent(completed, total)
            };
        }
    }

    private static IReadOnlyList<UbisoftCrossProgressionItem> LoadCapturedCrossProgressionCandidates()
    {
        var candidates = new Dictionary<string, UbisoftCrossProgressionItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in ReadCapturedGraphQlGameObjects())
        {
            var id = ReadJsonString(game, "id", "spaceId");
            var title = ReadJsonString(game, "name");
            var platform = game["platform"] as JObject;
            var nodes = game["viewer"]?["meta"]?["ownedCrossplayPlatforms"]?["nodes"] as JArray;
            var platformNames = new List<string>();

            var primary = ReadJsonString(platform ?? new JObject(), "name", "type");
            if (!string.IsNullOrWhiteSpace(primary))
                platformNames.Add(primary);

            if (nodes is not null)
            {
                foreach (var node in nodes.OfType<JObject>())
                {
                    var name = ReadJsonString(node, "name", "type");
                    if (!string.IsNullOrWhiteSpace(name))
                        platformNames.Add(name);
                }
            }

            var key = FirstNonEmpty(id, title);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            candidates[key] = new UbisoftCrossProgressionItem
            {
                Title = title,
                GameId = id,
                DetectedPlatforms = string.Join(", ", platformNames.Distinct(StringComparer.OrdinalIgnoreCase)),
                Evidence = "Ubisoft GraphQL viewer.meta.ownedCrossplayPlatforms",
                Status = nodes is not null && nodes.Count > 0 ? "Cross-platform ownership/progression evidence captured" : "Single captured platform or no crossplay nodes"
            };
        }

        return candidates.Values.ToList();
    }

    private static IEnumerable<JObject> ReadCapturedGraphQlGameObjects()
    {
        foreach (var payload in ReadCapturedJsonPayloads())
        {
            IEnumerable<JToken> responses = payload is JArray array ? array.Children() : new[] { payload };
            foreach (var response in responses.OfType<JObject>())
            {
                var data = response["data"] as JObject;
                if (data is null)
                    continue;

                if (data["game"] is JObject game)
                    yield return game;

                if (data["games"] is JArray games)
                {
                    foreach (var item in games.OfType<JObject>())
                        yield return item;
                }
            }
        }
    }

    private static IReadOnlyList<JToken> ExtractNodes(JObject connection)
    {
        if (connection["nodes"] is JArray nodes)
            return nodes.Children().ToList();

        if (connection["edges"] is JArray edges)
            return edges.Children()
                .Select(edge => edge["node"] ?? edge)
                .Where(node => node is not null)
                .ToList()!;

        return Array.Empty<JToken>();
    }

    private static bool MatchesCapturedGame(JObject graphGame, string gameNeedle)
    {
        if (string.IsNullOrWhiteSpace(gameNeedle))
            return true;

        return ReadJsonString(graphGame, "id", "spaceId", "name").Contains(gameNeedle, StringComparison.OrdinalIgnoreCase) ||
               graphGame.ToString(Formatting.None).Contains(gameNeedle, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddStat(IDictionary<string, UbisoftStatItem> stats, string id, string name, string value, string source)
    {
        if (string.IsNullOrWhiteSpace(value) || stats.ContainsKey(id))
            return;

        stats[id] = new UbisoftStatItem
        {
            Id = id,
            Name = name,
            Value = value,
            Source = source,
            Status = "Read-only captured"
        };
    }

    private static string BuildRewardStatus(JObject? viewerMeta)
    {
        if (viewerMeta is null)
            return string.Empty;

        var isRedeemed = ReadJsonString(viewerMeta, "isRedeemed");
        var isLocked = ReadJsonString(viewerMeta, "isLocked");
        if (IsTruthy(isRedeemed))
            return "Redeemed";
        if (IsTruthy(isLocked))
            return "Locked";
        return "Available";
    }

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase);

    private static double TryPercent(string completed, string total)
    {
        if (double.TryParse(completed, out var done) && double.TryParse(total, out var all) && all > 0)
            return Math.Max(0, Math.Min(100, done / all * 100));

        return 0;
    }

    private static string SplitCamel(string value) =>
        Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");

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

    private static IEnumerable<JToken> Descendants(JToken token)
    {
        yield return token;
        foreach (var child in token.Children())
        {
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static bool LooksLikeGameObject(JObject obj)
    {
        var text = string.Join(" ", obj.Properties().Select(prop => prop.Name));
        return text.Contains("game", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("product", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("space", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStatObject(JObject obj)
    {
        var text = string.Join(" ", obj.Properties().Select(prop => prop.Name));
        return text.Contains("stat", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("statistics", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("progression", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeChallengeObject(JObject obj)
    {
        var text = string.Join(" ", obj.Properties().Select(prop => prop.Name));
        return text.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("objective", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("reward", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesGameContext(JObject obj, string gameNeedle)
    {
        if (string.IsNullOrWhiteSpace(gameNeedle))
            return true;

        var objectText = obj.ToString(Formatting.None);
        return objectText.Contains(gameNeedle, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildProgressText(JObject obj)
    {
        var current = ReadJsonString(obj, "current", "currentValue", "amount");
        var target = ReadJsonString(obj, "target", "targetValue", "threshold");
        if (!string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(target))
            return $"{current}/{target}";

        return FirstNonEmpty(current, target);
    }

    private static IEnumerable<UbisoftGameItem> ReadCacheGames()
    {
        foreach (var root in EnumerateUbisoftRoots())
        {
            var dataGames = Path.Combine(root, "data", "games");
            if (Directory.Exists(dataGames))
            {
                foreach (var gameDir in Directory.EnumerateDirectories(dataGames))
                {
                    var id = Path.GetFileName(gameDir);
                    yield return new UbisoftGameItem
                    {
                        GameId = id,
                        DisplayName = $"Ubisoft game {id}",
                        Source = "Launcher data cache",
                        LaunchUri = $"uplay://launch/{id}/0"
                    };
                }
            }

            foreach (var jsonPath in Directory.Exists(root)
                         ? Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Take(250)
                         : Enumerable.Empty<string>())
            {
                UbisoftGameItem? item = null;
                try
                {
                    var json = JObject.Parse(File.ReadAllText(jsonPath));
                    var displayName = ReadJsonString(json, "name", "displayName", "title");
                    var gameId = ReadJsonString(json, "gameId", "productId", "spaceId", "id");
                    var installPath = ReadJsonString(json, "installPath", "installDir", "directory");
                    if (!string.IsNullOrWhiteSpace(displayName) || !string.IsNullOrWhiteSpace(installPath))
                    {
                        item = new UbisoftGameItem
                        {
                            GameId = gameId,
                            DisplayName = displayName,
                            InstallPath = installPath,
                            Source = "Launcher JSON cache",
                            LaunchUri = string.IsNullOrWhiteSpace(gameId) ? string.Empty : $"uplay://launch/{gameId}/0"
                        };
                    }
                }
                catch
                {
                    // Ubisoft cache folders include non-game JSON and partial files.
                }

                if (item is not null)
                    yield return item;
            }
        }
    }

    private static IEnumerable<string> EnumerateUbisoftRoots()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ubisoft", "Ubisoft Game Launcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ubisoft")
        };

        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadRegistryValue(RegistryKey key, params string[] names)
    {
        foreach (var name in names)
        {
            var value = key.GetValue(name)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string ReadJsonString(JObject json, params string[] names)
    {
        foreach (var name in names)
        {
            if (json.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var value))
                return value?.ToString() ?? string.Empty;
        }

        return string.Empty;
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

    private static bool LooksLikeModernConnectTitle(UbisoftGameItem game)
    {
        var text = $"{game.Title} {game.InstallPath} {game.Source}";
        return text.Contains("Connect", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Ubisoft Game Launcher", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(game.GameId);
    }
}
