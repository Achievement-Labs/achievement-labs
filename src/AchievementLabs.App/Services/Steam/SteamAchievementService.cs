using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using SAM.API;
using SAM.Game;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Steam;

public interface ISteamAchievementService
{
    string GetSteamInstallPath();
    string GetConverterPath();
    bool IsSteamRunning();
    IReadOnlyList<SteamGameItem> GetInstalledGames();
    SteamProfileSummary GetProfileSummary();
    string GetSteamMetadataStatus();
    Task RefreshSteamAppNameCacheAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<long, string>> RefreshSteamStoreNamesAsync(IEnumerable<long> appIds, CancellationToken cancellationToken);
    Task<int> GetAchievementDefinitionCountAsync(long appId, CancellationToken cancellationToken);
    Task<(int Unlocked, int Total)> GetCommunityAchievementProgressAsync(long appId, CancellationToken cancellationToken);
    Task<(int Unlocked, int Total)> GetAchievementProgressAsync(long appId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SteamAchievementItem>> LoadAchievementsAsync(long appId, CancellationToken cancellationToken);
    Task<int> StoreAchievementsAsync(long appId, IEnumerable<SteamAchievementItem> achievements, CancellationToken cancellationToken);
    Task ResetAsync(long appId, bool includeAchievements, CancellationToken cancellationToken);
    Task<SteamSpoofSessionInfo> StartSpoofSessionAsync(long appId, CancellationToken cancellationToken);
    void StopSpoofSession();
    SteamSpoofSessionInfo GetSpoofSessionInfo();
    Task<SteamResolvedProfile> ResolveSteamProfileAsync(string input, CancellationToken cancellationToken);
    Task<IReadOnlyList<SteamTargetAchievementUnlock>> LoadTargetAchievementUnlocksAsync(string steamId, long appId, CancellationToken cancellationToken);
}

public sealed record SteamSpoofSessionInfo(bool IsActive, long AppId, string AppName, DateTime? StartedAt, string Status);
public sealed record SteamResolvedProfile(string SteamId, string DisplayName);
public sealed record SteamTargetAchievementUnlock(string Id, DateTime UnlockTime);

public sealed class SteamAchievementService : ISteamAchievementService
{
    private static readonly Regex ManifestAppIdRegex = new("\"appid\"\\s+\"(?<value>\\d+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestNameRegex = new("\"name\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManifestInstallDirRegex = new("\"installdir\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LibraryPathRegex = new("\"path\"\\s+\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LoginUserBlockRegex = new("\"(?<steamid>\\d{12,})\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MostRecentRegex = new("\"MostRecent\"\\s+\"1\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PersonaNameRegex = new("\"PersonaName\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AccountNameRegex = new("\"AccountName\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SteamLevelRegex = new("friendPlayerLevelNum[^>]*>\\s*(?<value>\\d+)\\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SteamLevelJsonRegex = new("\"steamlevel\"\\s*:\\s*(?<value>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SteamLevelClassRegex = new("class=\"friendPlayerLevel[^>]*>\\s*<span[^>]*>\\s*(?<value>\\d+)\\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BadgeCountRegex = new("profile_count_link_total[^>]*>\\s*(?<value>[\\d,]+)\\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SteamVanityUrlRegex = new("steamcommunity\\.com/id/(?<value>[^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SteamProfileUrlRegex = new("steamcommunity\\.com/profiles/(?<value>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static string _metadataStatus = "Steam metadata cache has not been refreshed.";
    private static readonly string AppNameCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs",
        "steam-app-name-cache.json");
    private static readonly string StoreNameCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs",
        "steam-store-name-cache.json");
    private readonly object _spoofLock = new();
    private Process? _spoofProcess;
    private long _spoofAppId;
    private string _spoofAppName = string.Empty;
    private DateTime? _spoofStartedAt;

    public string GetSteamInstallPath()
    {
        try
        {
            return SAM.API.Steam.GetInstallPath() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public bool IsSteamRunning()
    {
        try
        {
            return Process.GetProcessesByName("steam").Any();
        }
        catch
        {
            return false;
        }
    }

    public string GetConverterPath()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            var direct = Path.Combine(root, "Steam to Xbox save converter");
            if (Directory.Exists(direct))
                return direct;

            var nested = Path.Combine(root, "AchievementLabs", "Steam to Xbox save converter");
            if (Directory.Exists(nested))
                return nested;
        }

        return string.Empty;
    }

    public IReadOnlyList<SteamGameItem> GetInstalledGames()
    {
        var steamPath = GetSteamInstallPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            return Array.Empty<SteamGameItem>();

        var names = LoadSteamAppNameCache();
        var games = new Dictionary<long, SteamGameItem>();
        foreach (var libraryPath in GetLibraryPaths(steamPath))
        {
            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamAppsPath))
                continue;

            foreach (var manifestPath in Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf"))
            {
                var manifest = File.ReadAllText(manifestPath);
                var appId = ReadLong(ManifestAppIdRegex, manifest);
                if (appId <= 0)
                    continue;

                var name = ReadText(ManifestNameRegex, manifest);
                var installDir = ReadText(ManifestInstallDirRegex, manifest);
                var installPath = string.IsNullOrWhiteSpace(installDir)
                    ? string.Empty
                    : Path.Combine(steamAppsPath, "common", installDir);

                games[appId] = new SteamGameItem
                {
                    AppId = appId,
                    Name = FirstNonEmpty(name, names.GetValueOrDefault(appId), $"App {appId}"),
                    InstallPath = installPath,
                    SchemaPath = GetSchemaPath(steamPath, appId),
                    Source = "Library",
                    ImageUrl = GetLocalLibraryImageUrl(steamPath, appId)
                };
            }
        }

        var schemaRoot = Path.Combine(steamPath, "appcache", "stats");
        if (Directory.Exists(schemaRoot))
        {
            foreach (var schemaPath in Directory.EnumerateFiles(schemaRoot, "UserGameStatsSchema_*.bin"))
            {
                var fileName = Path.GetFileNameWithoutExtension(schemaPath);
                var rawId = fileName.Replace("UserGameStatsSchema_", "", StringComparison.OrdinalIgnoreCase);
                if (!long.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                    continue;

                if (!games.ContainsKey(appId))
                {
                    games[appId] = new SteamGameItem
                    {
                        AppId = appId,
                        Name = FirstNonEmpty(names.GetValueOrDefault(appId), $"App {appId}"),
                        SchemaPath = schemaPath,
                        Source = "Schema cache",
                        ImageUrl = GetLocalLibraryImageUrl(steamPath, appId)
                    };
                }
            }
        }

        EnrichGameNamesFromSteamClient(games);
        EnrichGameNamesFromSteamStore(games, fetchMissing: false);
        EnrichMissingGameNamesFromSteamAppList(games);
        ScrubBadSteamNames(games);

        return games.Values
            .OrderBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetSteamMetadataStatus()
    {
        return _metadataStatus;
    }

    public async Task RefreshSteamAppNameCacheAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppNameCachePath)!);
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        try
        {
            _metadataStatus = "Downloading Steam app metadata...";
            var json = await httpClient.GetStringAsync("https://api.steampowered.com/ISteamApps/GetAppList/v0002/?format=json", cancellationToken);
            await File.WriteAllTextAsync(AppNameCachePath, json, cancellationToken);
            var count = LoadSteamAppNameCache(forceNoDownload: true).Count;
            _metadataStatus = $"Steam app metadata cache ready: {count} titles.";
        }
        catch (Exception ex)
        {
            _metadataStatus = $"Steam app metadata cache failed: {ex.Message}";
            throw;
        }
    }

    public SteamProfileSummary GetProfileSummary()
    {
        var steamPath = GetSteamInstallPath();
        var isRunning = IsSteamRunning();
        var games = GetInstalledGames();
        var localProfile = ReadLocalLoginProfile(steamPath);
        var steamId = localProfile.SteamId;
        var isLoggedIn = isRunning && !string.IsNullOrWhiteSpace(steamId) && steamId != "Unknown";
        var communityProfile = SteamCommunityProfile.Empty;

        if (!string.IsNullOrWhiteSpace(steamId) && steamId != "Unknown")
            communityProfile = ReadSteamCommunityProfile(steamId);

        return new SteamProfileSummary
        {
            IsSteamRunning = isRunning,
            IsLoggedIn = isLoggedIn,
            SteamId = string.IsNullOrWhiteSpace(steamId) ? "Unknown" : steamId,
            AccountName = string.IsNullOrWhiteSpace(localProfile.AccountName) ? "Unknown" : localProfile.AccountName,
            PersonaName = FirstNonEmpty(communityProfile.PersonaName, localProfile.PersonaName, "Unknown"),
            SteamLevel = FirstNonEmpty(communityProfile.Level, "Profile private/unavailable"),
            BadgeCount = FirstNonEmpty(communityProfile.BadgeCount, "Profile private/unavailable"),
            AvatarUrl = FirstNonEmpty(communityProfile.AvatarUrl, "pack://application:,,,/Assets/achievement-labs-icon.png"),
            GameCount = games.Count,
            SteamPath = steamPath
        };
    }

    public Task<IReadOnlyList<SteamAchievementItem>> LoadAchievementsAsync(long appId, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<SteamAchievementItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var definitions = LoadDefinitions(appId, "english");
            Client? client = null;
            try
            {
                client = CreateClient(appId);
                var language = client.SteamApps008?.GetCurrentGameLanguage();
                if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                    definitions = LoadDefinitions(appId, language);

                PrepareStats(client);
            }
            catch
            {
                client?.Dispose();
                client = null;
            }

            var communityStates = new Dictionary<string, SteamCommunityAchievementState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var steamId = ReadLocalLoginProfile(GetSteamInstallPath()).SteamId;
                if (!string.IsNullOrWhiteSpace(steamId) && steamId != "Unknown")
                    communityStates = LoadCommunityAchievementStates(steamId, appId);
            }
            catch
            {
                // Keep schema-only rows when the profile is private/offline.
            }

            var achievements = new List<SteamAchievementItem>();
            foreach (var definition in definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isAchieved = false;
                uint unlockTime = 0;

                if (communityStates.TryGetValue(definition.Id, out var communityState))
                {
                    isAchieved = communityState.IsUnlocked;
                    unlockTime = communityState.UnlockTime;
                }
                else if (client is not null && !client.SteamUserStats.GetAchievementAndUnlockTime(definition.Id, out isAchieved, out unlockTime))
                {
                    isAchieved = false;
                    unlockTime = 0;
                }

                achievements.Add(new SteamAchievementItem
                {
                    Id = definition.Id,
                    Name = string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name,
                    Description = definition.Description,
                    IsUnlocked = isAchieved,
                    DesiredUnlocked = isAchieved,
                    UnlockTime = isAchieved && unlockTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                        : null,
                    Permission = definition.Permission
                });
            }

            client?.Dispose();
            return achievements;
        }, cancellationToken);
    }

    public async Task<(int Unlocked, int Total)> GetAchievementProgressAsync(long appId, CancellationToken cancellationToken)
    {
        var achievements = await LoadAchievementsAsync(appId, cancellationToken);
        return (achievements.Count(achievement => achievement.IsUnlocked), achievements.Count);
    }

    public Task<(int Unlocked, int Total)> GetCommunityAchievementProgressAsync(long appId, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamId = ReadLocalLoginProfile(GetSteamInstallPath()).SteamId;
            if (string.IsNullOrWhiteSpace(steamId) || steamId == "Unknown")
                throw new InvalidOperationException("Steam profile was not found locally.");

            var states = LoadCommunityAchievementStates(steamId, appId);
            if (states.Count == 0)
                throw new InvalidOperationException("Steam Community did not return achievement state.");

            return (states.Count(pair => pair.Value.IsUnlocked), states.Count);
        }, cancellationToken);
    }

    public Task<int> GetAchievementDefinitionCountAsync(long appId, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LoadDefinitions(appId, "english").Count;
        }, cancellationToken);
    }

    private static Dictionary<string, SteamCommunityAchievementState> LoadCommunityAchievementStates(string steamId, long appId)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        var url = $"https://steamcommunity.com/profiles/{steamId}/stats/{appId.ToString(CultureInfo.InvariantCulture)}/achievements?xml=1";
        var xml = httpClient.GetStringAsync(url).GetAwaiter().GetResult();
        var document = XDocument.Parse(xml);
        var achievements = document.Root?.Element("achievements")?.Elements("achievement") ?? Enumerable.Empty<XElement>();

        var states = new Dictionary<string, SteamCommunityAchievementState>(StringComparer.OrdinalIgnoreCase);
        foreach (var achievement in achievements)
        {
            var id = achievement.Element("apiname")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var closed = achievement.Attribute("closed")?.Value ?? "0";
            var unlocked = closed == "1";
            var rawTime = achievement.Element("unlockTimestamp")?.Value ?? string.Empty;
            var unlockTime = uint.TryParse(rawTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTime)
                ? parsedTime
                : 0;

            states[id] = new SteamCommunityAchievementState(unlocked, unlockTime);
        }

        return states;
    }

    public Task<SteamResolvedProfile> ResolveSteamProfileAsync(string input, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            input = (input ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(input))
                throw new InvalidOperationException("Enter a Steam ID64, profile URL, vanity URL, or vanity name.");

            if (Regex.IsMatch(input, "^\\d{15,}$"))
                return ResolveSteamProfileFromXml($"https://steamcommunity.com/profiles/{input}/?xml=1", input, cancellationToken);

            var profileMatch = SteamProfileUrlRegex.Match(input);
            if (profileMatch.Success)
            {
                var steamId = profileMatch.Groups["value"].Value;
                return ResolveSteamProfileFromXml($"https://steamcommunity.com/profiles/{steamId}/?xml=1", steamId, cancellationToken);
            }

            var vanityMatch = SteamVanityUrlRegex.Match(input);
            var vanityName = vanityMatch.Success ? vanityMatch.Groups["value"].Value : input;
            return ResolveSteamProfileFromXml($"https://steamcommunity.com/id/{Uri.EscapeDataString(vanityName)}/?xml=1", string.Empty, cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SteamTargetAchievementUnlock>> LoadTargetAchievementUnlocksAsync(string steamId, long appId, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<SteamTargetAchievementUnlock>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var states = LoadCommunityAchievementStates(steamId, appId);
            return states
                .Where(pair => pair.Value.IsUnlocked && pair.Value.UnlockTime > 0)
                .Select(pair => new SteamTargetAchievementUnlock(
                    pair.Key,
                    DateTimeOffset.FromUnixTimeSeconds(pair.Value.UnlockTime).LocalDateTime))
                .OrderBy(unlock => unlock.UnlockTime)
                .ToList();
        }, cancellationToken);
    }

    private static SteamResolvedProfile ResolveSteamProfileFromXml(string xmlUrl, string fallbackSteamId, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        var xml = httpClient.GetStringAsync(xmlUrl, cancellationToken).GetAwaiter().GetResult();
        var document = XDocument.Parse(xml);
        var error = document.Descendants("error").FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"Steam profile lookup failed: {error}");

        var steamId = FirstNonEmpty(
            document.Descendants("steamID64").FirstOrDefault()?.Value,
            fallbackSteamId);
        if (string.IsNullOrWhiteSpace(steamId))
            throw new InvalidOperationException("Steam profile lookup did not return a Steam ID64.");

        var displayName = FirstNonEmpty(
            document.Descendants("steamID").FirstOrDefault()?.Value,
            steamId);

        return new SteamResolvedProfile(steamId.Trim(), displayName.Trim());
    }

    public Task<int> StoreAchievementsAsync(long appId, IEnumerable<SteamAchievementItem> achievements, CancellationToken cancellationToken)
    {
        var requested = achievements.Where(achievement => achievement.IsModified).ToList();
        if (requested.Count == 0)
            return Task.FromResult(0);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateClient(appId);
            PrepareStats(client);

            foreach (var achievement in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.SteamUserStats.SetAchievement(achievement.Id, achievement.DesiredUnlocked))
                    throw new InvalidOperationException($"Steam refused to set achievement '{achievement.Id}'.");
            }

            if (!client.SteamUserStats.StoreStats())
                throw new InvalidOperationException("Steam refused StoreStats for the active game.");

            return requested.Count;
        }, cancellationToken);
    }

    public Task ResetAsync(long appId, bool includeAchievements, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = CreateClient(appId);
            PrepareStats(client);

            if (!client.SteamUserStats.ResetAllStats(includeAchievements))
                throw new InvalidOperationException("Steam refused ResetAllStats for the active game.");

            if (!client.SteamUserStats.StoreStats())
                throw new InvalidOperationException("Steam refused StoreStats after reset.");
        }, cancellationToken);
    }

    public Task<SteamSpoofSessionInfo> StartSpoofSessionAsync(long appId, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var names = LoadSteamAppNameCache(forceNoDownload: true);
            var appName = FirstNonEmpty(names.GetValueOrDefault(appId), $"App {appId}");
            var helperPath = GetSteamIdleHelperPath();
            if (!File.Exists(helperPath))
                throw new FileNotFoundException("Steam idle helper was not found. Rebuild or publish Achievement Labs, then try again.", helperPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = appId.ToString(CultureInfo.InvariantCulture),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["SteamAppId"] = appId.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["SteamGameId"] = appId.ToString(CultureInfo.InvariantCulture);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Steam idle helper failed to start.");

            try
            {
                var ready = WaitForSteamIdleReady(process, cancellationToken);
                if (!ready)
                {
                    var error = process.StandardError.ReadToEnd();
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);

                    throw new InvalidOperationException(FirstNonEmpty(error, "Steam idle helper did not report ready."));
                }

                var startedAt = DateTime.Now;
                lock (_spoofLock)
                {
                    DisposeSpoofProcessNoLock();
                    _spoofProcess = process;
                    _spoofAppId = appId;
                    _spoofAppName = appName;
                    _spoofStartedAt = startedAt;
                }

                return new SteamSpoofSessionInfo(true, appId, appName, startedAt, "Steam idle helper is running for this App ID.");
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                throw;
            }
        }, cancellationToken);
    }

    public void StopSpoofSession()
    {
        lock (_spoofLock)
        {
            DisposeSpoofProcessNoLock();
            _spoofAppId = 0;
            _spoofAppName = string.Empty;
            _spoofStartedAt = null;
        }

        Environment.SetEnvironmentVariable("SteamAppId", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("SteamGameId", null, EnvironmentVariableTarget.Process);
    }

    public SteamSpoofSessionInfo GetSpoofSessionInfo()
    {
        lock (_spoofLock)
        {
            if (_spoofProcess is not null && _spoofProcess.HasExited)
                DisposeSpoofProcessNoLock();

            return _spoofProcess is null
                ? new SteamSpoofSessionInfo(false, 0, string.Empty, null, "No Steam spoof session is active.")
                : new SteamSpoofSessionInfo(true, _spoofAppId, _spoofAppName, _spoofStartedAt, $"Steam idle helper is running. PID {_spoofProcess.Id}.");
        }
    }

    private void DisposeSpoofProcessNoLock()
    {
        if (_spoofProcess is not null)
        {
            try
            {
                if (!_spoofProcess.HasExited)
                    _spoofProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // The helper may already be gone.
            }

            _spoofProcess.Dispose();
            _spoofProcess = null;
        }
    }

    private static string GetSteamIdleHelperPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "steam-idle", "AchievementLabs.SteamIdle.exe");
    }

    private static bool WaitForSteamIdleReady(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var readyLine = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
                return false;

            if (readyLine.Wait(TimeSpan.FromMilliseconds(250), cancellationToken) &&
                readyLine.Result?.StartsWith("READY ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static Client CreateClient(long appId)
    {
        if (appId < 0)
            throw new ArgumentOutOfRangeException(nameof(appId), "Enter a valid Steam app ID.");

        var client = new Client();
        try
        {
            if (appId > 0)
            {
                Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("SteamGameId", appId.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);
            }

            client.Initialize(appId);
            ValidateClient(client, appId);
            if (!client.SteamUser.IsLoggedIn())
                throw new InvalidOperationException("Steam is not logged in.");

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static void ValidateClient(Client client, long appId)
    {
        if (client.SteamUser is null)
            throw new InvalidOperationException($"Steam user interface was not available for app {appId}.");

        if (client.SteamUserStats is null)
            throw new InvalidOperationException($"Steam user-stats interface was not available for app {appId}. Close this app, make sure Steam is running and logged in, then try again.");

        // ISteamApps001/008 are metadata conveniences in SAM. Recent Steam client
        // contexts can omit them while user stats still work.
    }

    private static void PrepareStats(Client client)
    {
        var steamId = client.SteamUser.GetSteamId();
        if (client.SteamUserStats.RequestUserStats(steamId) == CallHandle.Invalid)
            throw new InvalidOperationException("Steam refused RequestUserStats for the active user.");

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            client.RunCallbacks(false);
            Thread.Sleep(50);
        }
    }

    private static IReadOnlyList<SteamAchievementDefinition> LoadDefinitions(long appId, string currentLanguage)
    {
        var schemaPath = GetSchemaPath(SAM.API.Steam.GetInstallPath(), appId);
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Steam schema was not found for app {appId}. Launch the game once, then try again.", schemaPath);

        var kv = KeyValue.LoadAsBinary(schemaPath)
            ?? throw new InvalidOperationException($"Steam schema for app {appId} could not be parsed.");

        var stats = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
        if (!stats.Valid || stats.Children is null)
            throw new InvalidOperationException($"Steam schema for app {appId} did not contain achievement stats.");

        var definitions = new List<SteamAchievementDefinition>();
        foreach (var stat in stats.Children)
        {
            if (!stat.Valid)
                continue;

            var type = ResolveStatType(stat);
            if (type is not SAM.API.Types.UserStatType.Achievements and not SAM.API.Types.UserStatType.GroupAchievements)
                continue;

            if (stat.Children is null)
                continue;

            foreach (var bits in stat.Children.Where(bit => string.Equals(bit.Name, "bits", StringComparison.InvariantCultureIgnoreCase)))
            {
                if (!bits.Valid || bits.Children is null)
                    continue;

                foreach (var bit in bits.Children)
                {
                    var id = bit["name"].AsString("");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    definitions.Add(new SteamAchievementDefinition(
                        id,
                        GetLocalizedString(bit["display"]["name"], currentLanguage, id),
                        GetLocalizedString(bit["display"]["desc"], currentLanguage, ""),
                        bit["display"]["icon"].AsString(""),
                        bit["display"]["icon_gray"].AsString(""),
                        bit["display"]["hidden"].AsBoolean(false),
                        bit["permission"].AsInteger(0)));
                }
            }
        }

        return definitions;
    }

    private static SAM.API.Types.UserStatType ResolveStatType(KeyValue stat)
    {
        var typeNode = stat["type"];
        if (typeNode.Valid && typeNode.Type == KeyValueType.String &&
            Enum.TryParse<SAM.API.Types.UserStatType>(typeNode.AsString(""), true, out var namedType))
        {
            return namedType;
        }

        var typeIntNode = stat["type_int"];
        var rawType = typeIntNode.Valid
            ? typeIntNode.AsInteger(0)
            : typeNode.AsInteger(0);

        return (SAM.API.Types.UserStatType)rawType;
    }

    private static string GetLocalizedString(KeyValue kv, string currentLanguage, string defaultValue)
    {
        if (!kv.Valid)
            return defaultValue;

        var name = kv[currentLanguage].AsString("");
        if (!string.IsNullOrEmpty(name))
            return name;

        name = kv["english"].AsString("");
        if (!string.IsNullOrEmpty(name))
            return name;

        name = kv.AsString("");
        return string.IsNullOrEmpty(name) ? defaultValue : name;
    }

    private static IEnumerable<string> GetLibraryPaths(string steamPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(steamPath))
            paths.Add(steamPath);

        var libraryFolders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFolders))
            return paths;

        var vdf = File.ReadAllText(libraryFolders);
        foreach (Match match in LibraryPathRegex.Matches(vdf))
        {
            var libraryPath = UnescapeVdfPath(match.Groups["value"].Value);
            if (Directory.Exists(libraryPath))
                paths.Add(libraryPath);
        }

        return paths;
    }

    private static void EnrichGameNamesFromSteamClient(Dictionary<long, SteamGameItem> games)
    {
        if (games.Count == 0)
            return;

        try
        {
            using var client = CreateClient(0);
            foreach (var pair in games.ToList())
            {
                var current = pair.Value;
                var steamName = client.SteamApps001?.GetAppData((uint)current.AppId, "name");
                var imageUrl = GetGameImageUrl(client, (uint)current.AppId);
                if (string.IsNullOrWhiteSpace(steamName))
                    steamName = current.Name;

                games[pair.Key] = new SteamGameItem
                {
                    AppId = current.AppId,
                    Name = steamName,
                    InstallPath = current.InstallPath,
                    SchemaPath = current.SchemaPath,
                    Source = current.Source,
                    ImageUrl = FirstNonEmpty(imageUrl, current.ImageUrl, "pack://application:,,,/Assets/achievement-labs-icon.png"),
                    AchievementSummary = current.AchievementSummary,
                    Progress = current.Progress
                };
            }
        }
        catch
        {
            // Steam may be closed or not logged in; manifest names are still usable.
        }
    }

    private static void EnrichMissingGameNamesFromSteamAppList(Dictionary<long, SteamGameItem> games)
    {
        var missing = games
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value.Name) || pair.Value.Name.StartsWith("App ", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToHashSet();

        if (missing.Count == 0)
            return;

        try
        {
            var names = LoadSteamAppNameCache();
            if (names.Count == 0)
                return;

            foreach (var appId in missing)
            {
                if (!names.TryGetValue(appId, out var name) || string.IsNullOrWhiteSpace(name))
                    continue;

                var current = games[appId];
                games[appId] = new SteamGameItem
                {
                    AppId = current.AppId,
                    Name = name,
                    InstallPath = current.InstallPath,
                    SchemaPath = current.SchemaPath,
                    Source = current.Source,
                    ImageUrl = current.ImageUrl,
                    AchievementSummary = current.AchievementSummary,
                    Progress = current.Progress
                };
            }
        }
        catch
        {
            // Keep App #### fallbacks if Steam's public app list is unavailable.
        }
    }

    public Task<IReadOnlyDictionary<long, string>> RefreshSteamStoreNamesAsync(IEnumerable<long> appIds, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyDictionary<long, string>>(() =>
        {
            var ids = appIds
                .Where(appId => appId > 0)
                .Distinct()
                .ToList();

            var cache = LoadSteamStoreNameCache();
            var missing = ids
                .Where(appId => !cache.TryGetValue(appId, out var name) || string.IsNullOrWhiteSpace(name))
                .ToList();

            if (missing.Count > 0)
            {
                var cacheLock = new object();
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };

                Parallel.ForEach(
                    missing,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 6,
                        CancellationToken = cancellationToken
                    },
                    appId =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = FetchSteamStoreName(httpClient, appId);
                        if (string.IsNullOrWhiteSpace(name))
                            return;

                        lock (cacheLock)
                            cache[appId] = name;
                    });

                SaveSteamStoreNameCache(cache);
            }

            return ids
                .Where(appId => cache.TryGetValue(appId, out var name) && !string.IsNullOrWhiteSpace(name))
                .ToDictionary(appId => appId, appId => cache[appId]);
        }, cancellationToken);
    }

    private static void EnrichGameNamesFromSteamStore(Dictionary<long, SteamGameItem> games, bool fetchMissing)
    {
        if (games.Count == 0)
            return;

        try
        {
            var cache = LoadSteamStoreNameCache();
            var candidates = games.Values
                .Where(game => game.HasSchema || game.Name.StartsWith("App ", StringComparison.OrdinalIgnoreCase) || IsLikelyBadSteamName(game.Name))
                .Select(game => game.AppId)
                .Distinct()
                .ToList();

            var missing = candidates
                .Where(appId => !cache.TryGetValue(appId, out var name) || string.IsNullOrWhiteSpace(name))
                .ToList();

            if (fetchMissing && missing.Count > 0)
            {
                var cacheLock = new object();
                var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };

                using (httpClient)
                {
                    Parallel.ForEach(
                        missing,
                        new ParallelOptions { MaxDegreeOfParallelism = 8 },
                        appId =>
                        {
                            var name = FetchSteamStoreName(httpClient, appId);
                            if (string.IsNullOrWhiteSpace(name))
                                return;

                            lock (cacheLock)
                                cache[appId] = name;
                        });
                }

                SaveSteamStoreNameCache(cache);
            }

            foreach (var appId in candidates)
            {
                if (!cache.TryGetValue(appId, out var name) || string.IsNullOrWhiteSpace(name))
                    continue;

                var current = games[appId];
                games[appId] = CopySteamGame(current, name);
            }
        }
        catch
        {
            // Store metadata is a convenience path. Local Steam caches still carry the list.
        }
    }

    private static string FetchSteamStoreName(HttpClient httpClient, long appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId.ToString(CultureInfo.InvariantCulture)}&filters=basic";
            var json = httpClient.GetStringAsync(url).GetAwaiter().GetResult();
            var root = JObject.Parse(json);
            var data = root[appId.ToString(CultureInfo.InvariantCulture)]?["data"];
            if (data is null)
                return string.Empty;

            var type = data["type"]?.Value<string>() ?? string.Empty;
            var name = data["name"]?.Value<string>() ?? string.Empty;
            return string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "mod", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "demo", StringComparison.OrdinalIgnoreCase)
                ? name
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Dictionary<long, string> LoadSteamStoreNameCache()
    {
        try
        {
            if (!File.Exists(StoreNameCachePath))
                return new Dictionary<long, string>();

            var json = File.ReadAllText(StoreNameCachePath);
            var obj = JObject.Parse(json);
            return obj.Properties()
                .Select(property => new
                {
                    AppId = long.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) ? appId : 0,
                    Name = property.Value.Value<string>() ?? string.Empty
                })
                .Where(item => item.AppId > 0 && !string.IsNullOrWhiteSpace(item.Name))
                .ToDictionary(item => item.AppId, item => item.Name);
        }
        catch
        {
            return new Dictionary<long, string>();
        }
    }

    private static void SaveSteamStoreNameCache(Dictionary<long, string> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreNameCachePath)!);
            var obj = new JObject();
            foreach (var pair in cache.OrderBy(pair => pair.Key))
                obj[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;

            File.WriteAllText(StoreNameCachePath, obj.ToString());
        }
        catch
        {
            // Cache writes are non-critical.
        }
    }

    private static bool IsLikelyBadSteamName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var trimmed = name.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
            return true;

        if (trimmed.StartsWith('-') || trimmed.StartsWith(':'))
            return true;

        if (Regex.IsMatch(trimmed, "^\\d+\\s+EULA$", RegexOptions.IgnoreCase))
            return true;

        if (trimmed.EndsWith(" EULA", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(trimmed, "^[a-f0-9]{20,}(_thumb)?$", RegexOptions.IgnoreCase))
            return true;

        if (trimmed.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static void ScrubBadSteamNames(Dictionary<long, SteamGameItem> games)
    {
        foreach (var pair in games.ToList())
        {
            if (!IsLikelyBadSteamName(pair.Value.Name))
                continue;

            games[pair.Key] = CopySteamGame(pair.Value, $"App {pair.Key.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static SteamGameItem CopySteamGame(SteamGameItem current, string name)
    {
        return new SteamGameItem
        {
            AppId = current.AppId,
            Name = name,
            InstallPath = current.InstallPath,
            SchemaPath = current.SchemaPath,
            Source = current.Source,
            ImageUrl = current.ImageUrl,
            AchievementSummary = current.AchievementSummary,
            Progress = current.Progress
        };
    }

    private static void EnrichMissingGameNamesFromSteamAppInfo(string steamPath, Dictionary<long, SteamGameItem> games)
    {
        var missing = games
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value.Name) || pair.Value.Name.StartsWith("App ", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        if (missing.Count == 0)
            return;

        var appInfoPath = Path.Combine(steamPath, "appcache", "appinfo.vdf");
        if (!File.Exists(appInfoPath))
            return;

        try
        {
            var bytes = File.ReadAllBytes(appInfoPath);
            foreach (var appId in missing)
            {
                var name = FindAppInfoName(bytes, appId);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var current = games[appId];
                games[appId] = new SteamGameItem
                {
                    AppId = current.AppId,
                    Name = name,
                    InstallPath = current.InstallPath,
                    SchemaPath = current.SchemaPath,
                    Source = current.Source,
                    ImageUrl = current.ImageUrl,
                    AchievementSummary = current.AchievementSummary,
                    Progress = current.Progress
                };
            }
        }
        catch
        {
            // appinfo.vdf is a binary Steam cache; ignore parse failures and keep the app ID fallback.
        }
    }

    private static string FindAppInfoName(byte[] bytes, long appId)
    {
        if (appId <= 0 || appId > uint.MaxValue)
            return string.Empty;

        var needle = BitConverter.GetBytes((uint)appId);
        var bestName = string.Empty;
        var bestScore = int.MinValue;
        var hitCount = 0;

        for (var i = 0; i <= bytes.Length - needle.Length; i++)
        {
            if (bytes[i] != needle[0] || bytes[i + 1] != needle[1] || bytes[i + 2] != needle[2] || bytes[i + 3] != needle[3])
                continue;

            hitCount++;
            if (hitCount > 250)
                break;

            var strings = ExtractAsciiStrings(bytes, i, 2400).ToList();
            var looksLikeAppRecord = strings.Any(value => string.Equals(value, "game", StringComparison.OrdinalIgnoreCase)) ||
                                     strings.Any(value => string.Equals(value, "tool", StringComparison.OrdinalIgnoreCase)) ||
                                     strings.Any(value => string.Equals(value, "demo", StringComparison.OrdinalIgnoreCase));

            for (var index = 0; index < strings.Count; index++)
            {
                var candidate = strings[index];
                var score = ScoreAppInfoNameCandidate(candidate, index, looksLikeAppRecord);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestName = candidate;
            }
        }

        return bestScore > 0 ? bestName : string.Empty;
    }

    private static IEnumerable<string> ExtractAsciiStrings(byte[] bytes, int start, int length)
    {
        var end = Math.Min(bytes.Length, start + length);
        var chars = new List<char>();
        for (var i = start; i < end; i++)
        {
            var b = bytes[i];
            if (b is >= 32 and <= 126)
            {
                chars.Add((char)b);
                continue;
            }

            if (chars.Count >= 3)
                yield return new string(chars.ToArray());

            chars.Clear();
        }

        if (chars.Count >= 3)
            yield return new string(chars.ToArray());
    }

    private static int ScoreAppInfoNameCandidate(string value, int index, bool looksLikeAppRecord)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 90)
            return int.MinValue;

        if (!value.Any(char.IsLetter))
            return int.MinValue;

        if (Regex.IsMatch(value, "^[a-f0-9]{24,}$", RegexOptions.IgnoreCase))
            return int.MinValue;

        if (value.Contains('/') || value.Contains('\\') || value.StartsWith('#') || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return int.MinValue;

        var blocked = new[]
        {
            "windows", "linux", "macos", "english", "french", "italian", "german", "spanish", "schinese", "tchinese",
            "koreana", "polish", "portuguese", "russian", "game", "tool", "demo", "config", "steamdeck", "steammachine",
            "steamui", "testresult", "header.jpg", "library", "logo.png"
        };

        if (blocked.Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase)))
            return int.MinValue;

        var score = 4;
        if (looksLikeAppRecord)
            score += 6;
        if (value.Any(char.IsUpper))
            score += 2;
        if (value.Contains(' ') || value.Contains(':') || value.Contains('-'))
            score += 2;
        if (index > 0 && index < 12)
            score += 2;
        if (index >= 12)
            score -= index / 3;
        if (value.Length < 5)
            score -= 3;

        return score;
    }

    private static string GetGameImageUrl(Client client, uint appId)
    {
        try
        {
            var currentLanguage = client.SteamApps008.GetCurrentGameLanguage();
            var candidate = client.SteamApps001.GetAppData(appId, $"small_capsule/{currentLanguage}");
            if (!string.IsNullOrWhiteSpace(candidate))
                return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{candidate}";

            if (!string.Equals(currentLanguage, "english", StringComparison.OrdinalIgnoreCase))
            {
                candidate = client.SteamApps001.GetAppData(appId, "small_capsule/english");
                if (!string.IsNullOrWhiteSpace(candidate))
                    return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{candidate}";
            }

            candidate = client.SteamApps001.GetAppData(appId, "logo");
            if (!string.IsNullOrWhiteSpace(candidate))
                return $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{candidate}.jpg";
        }
        catch
        {
            // Keep placeholder if Steam app metadata is unavailable.
        }

        return string.Empty;
    }

    private static string GetLocalLibraryImageUrl(string steamPath, long appId)
    {
        var root = Path.Combine(steamPath, "appcache", "librarycache", appId.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(root))
        {
            foreach (var pattern in new[] { "*library_600x900*.jpg", "*header*.jpg", "*.jpg", "*.png" })
            {
                var match = Directory.EnumerateFiles(root, pattern).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }
        }

        var flatRoot = Path.Combine(steamPath, "appcache", "librarycache");
        if (Directory.Exists(flatRoot))
        {
            foreach (var pattern in new[]
                     {
                         $"{appId}_library_600x900*.jpg",
                         $"{appId}_header*.jpg",
                         $"{appId}_*.jpg",
                         $"{appId}_*.png"
                     })
            {
                var match = Directory.EnumerateFiles(flatRoot, pattern).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }
        }

        return GetCdnHeaderImageUrl(appId);
    }

    private static string GetCdnHeaderImageUrl(long appId)
    {
        return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
    }

    private static Dictionary<long, string> LoadSteamAppNameCache(bool forceNoDownload = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppNameCachePath)!);
            if (!forceNoDownload && (!File.Exists(AppNameCachePath) || File.GetLastWriteTimeUtc(AppNameCachePath) < DateTime.UtcNow.AddDays(-7)))
            {
                using var httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };

                var json = httpClient.GetStringAsync("https://api.steampowered.com/ISteamApps/GetAppList/v0002/?format=json").GetAwaiter().GetResult();
                File.WriteAllText(AppNameCachePath, json);
                _metadataStatus = "Steam app metadata cache refreshed.";
            }

            if (!File.Exists(AppNameCachePath))
                return new Dictionary<long, string>();

            var cachedJson = File.ReadAllText(AppNameCachePath);
            var apps = JObject.Parse(cachedJson)["applist"]?["apps"] as JArray;
            if (apps is null)
                return new Dictionary<long, string>();

            return apps
                .Select(app => new
                {
                    AppId = app["appid"]?.Value<long>() ?? 0,
                    Name = app["name"]?.Value<string>() ?? string.Empty
                })
                .Where(app => app.AppId > 0 && !string.IsNullOrWhiteSpace(app.Name))
                .GroupBy(app => app.AppId)
                .ToDictionary(group => group.Key, group => group.First().Name);
        }
        catch
        {
            return new Dictionary<long, string>();
        }
    }

    private static (string SteamId, string AccountName, string PersonaName) ReadLocalLoginProfile(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
            return ("Unknown", "Unknown", "Unknown");

        var loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(loginUsersPath))
            return ("Unknown", "Unknown", "Unknown");

        var vdf = File.ReadAllText(loginUsersPath);
        Match? selected = null;
        foreach (Match match in LoginUserBlockRegex.Matches(vdf))
        {
            if (MostRecentRegex.IsMatch(match.Groups["body"].Value))
            {
                selected = match;
                break;
            }

            selected ??= match;
        }

        if (selected is null)
            return ("Unknown", "Unknown", "Unknown");

        var body = selected.Groups["body"].Value;
        return (
            selected.Groups["steamid"].Value,
            ReadText(AccountNameRegex, body),
            ReadText(PersonaNameRegex, body));
    }

    private static SteamCommunityProfile ReadSteamCommunityProfile(string steamId)
    {
        var profile = new SteamCommunityProfile();
        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            var xml = httpClient.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}?xml=1").GetAwaiter().GetResult();
            var document = XDocument.Parse(xml);
            profile.PersonaName = document.Root?.Element("steamID")?.Value ?? string.Empty;
            profile.AvatarUrl = document.Root?.Element("avatarFull")?.Value ?? string.Empty;

            var profileHtml = httpClient.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}").GetAwaiter().GetResult();
            var levelMatch = SteamLevelRegex.Match(profileHtml);
            if (!levelMatch.Success)
                levelMatch = SteamLevelJsonRegex.Match(profileHtml);
            if (!levelMatch.Success)
                levelMatch = SteamLevelClassRegex.Match(profileHtml);
            if (levelMatch.Success)
                profile.Level = levelMatch.Groups["value"].Value;

            var badgesHtml = httpClient.GetStringAsync($"https://steamcommunity.com/profiles/{steamId}/badges").GetAwaiter().GetResult();
            var badgeMatch = BadgeCountRegex.Match(badgesHtml);
            if (badgeMatch.Success)
                profile.BadgeCount = badgeMatch.Groups["value"].Value;
        }
        catch
        {
            // Public Steam Community pages can be private, offline, or rate-limited.
        }

        return profile;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private sealed class SteamCommunityProfile
    {
        public static SteamCommunityProfile Empty => new();

        public string PersonaName { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string BadgeCount { get; set; } = string.Empty;
    }

    private sealed record SteamCommunityAchievementState(bool IsUnlocked, uint UnlockTime);

    private static string GetSchemaPath(string steamPath, long appId)
    {
        return Path.Combine(steamPath, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");
    }

    private static long ReadLong(Regex regex, string value)
    {
        var text = ReadText(regex, value);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static string ReadText(Regex regex, string value)
    {
        var match = regex.Match(value);
        return match.Success ? UnescapeVdfPath(match.Groups["value"].Value) : string.Empty;
    }

    private static string UnescapeVdfPath(string value)
    {
        return value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        foreach (var seed in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }
}
