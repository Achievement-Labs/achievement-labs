using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;



public class XboxApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    private readonly HttpClient _eventBasedClient; // Dumb, but needed for events for now

    private readonly HttpClient _spooferClient;

    // User specifics
    private readonly string _xauth;
    private readonly string _requestedResponseLanguage;
    public string LastAchievementsRawResponse { get; private set; } = string.Empty;
    public string LastGameStatsRawResponse { get; private set; } = string.Empty;
    public string LastTitleHubStatsRawResponse { get; private set; } = string.Empty;

    public XboxApiClient(string xauth, bool regionOverride = false)
    {
        _xauth = xauth;
        _requestedResponseLanguage = regionOverride ? "en-GB" : System.Globalization.CultureInfo.CurrentCulture.Name;
        var handler = new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler);

        var spooferHandler = new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _spooferClient = new HttpClient(spooferHandler);

        var insecureEventsHandler = new HttpClientHandler()
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            //This is an absolutely terrible idea but the stupid fucking events API just cries about SSL errors
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _eventBasedClient = new HttpClient(insecureEventsHandler);
    }

    public void Dispose() { _httpClient.Dispose(); _eventBasedClient.Dispose(); _spooferClient.Dispose(); }

    // Transport injection for deterministic offline checks of the real request path.
    public XboxApiClient(string xauth, HttpMessageHandler transport, bool regionOverride = false) : this(xauth, regionOverride)
    {
        _httpClient.Dispose();
        _httpClient = new HttpClient(transport);
    }

    private void SetDefaultHeaders()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Authorization, _xauth);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.AcceptLanguage, _requestedResponseLanguage);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.AcceptEncoding, HeaderValues.AcceptEncoding);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Accept, HeaderValues.Accept);


#if DEBUG
        Console.WriteLine("Headers in _httpClient:");
        foreach (var header in _httpClient.DefaultRequestHeaders)
        {
            if (header.Key == "Authorization") continue;
            Console.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }
#endif
    }

    private void SetDefaultSpooferHeaders()
    {
        _spooferClient.DefaultRequestHeaders.Clear();
        _spooferClient.DefaultRequestHeaders.Add(HeaderNames.Authorization, _xauth);
        _spooferClient.DefaultRequestHeaders.Add(HeaderNames.AcceptLanguage, _requestedResponseLanguage);
        _spooferClient.DefaultRequestHeaders.Add(HeaderNames.AcceptEncoding, HeaderValues.AcceptEncoding);
        _spooferClient.DefaultRequestHeaders.Add(HeaderNames.Accept, HeaderValues.Accept);

#if DEBUG
        Console.WriteLine("Headers in _spooferClient:");
        foreach (var header in _spooferClient.DefaultRequestHeaders)
        {
            if (header.Key == "Authorization") continue;
            Console.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }
#endif
    }

    private void SetDefaultEventBasedHeaders()
    {
        _eventBasedClient.DefaultRequestHeaders.Clear();
        _eventBasedClient.DefaultRequestHeaders.Add("user-agent", "MSDW");
        _eventBasedClient.DefaultRequestHeaders.Add("cache-control", "no-cache");
        _eventBasedClient.DefaultRequestHeaders.Add(HeaderNames.Accept, HeaderValues.Accept);
        _eventBasedClient.DefaultRequestHeaders.Add(HeaderNames.AcceptEncoding, HeaderValues.AcceptEncoding);
        _eventBasedClient.DefaultRequestHeaders.Add("reliability-mode", "standard");
        _eventBasedClient.DefaultRequestHeaders.Add("client-version", "EUTC-Windows-C++-no-10.0.22621.3296.amd64fre.ni_release.220506-1250-no");
        _eventBasedClient.DefaultRequestHeaders.Add("apikey", "0890af88a9ed4cc886a14f5e174a2827-9de66c5e-f867-43a8-a7b8-e0ddd481cca4-7548,95c1f21d6cb047a09e7b423c1cb2222e-9965f07b-54fa-498e-9727-9e8d24dec39e-7027");
        _eventBasedClient.DefaultRequestHeaders.Add("Client-Id", "NO_AUTH");
        _eventBasedClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.Telemetry);
        _eventBasedClient.DefaultRequestHeaders.Add(HeaderNames.Connection, "close");
        ;
        var authxtoken = Regex.Replace(_xauth, @"XBL3\.0 x=\d+;", "XBL3.0 x=-;");
        _eventBasedClient.DefaultRequestHeaders.Add("authxtoken", authxtoken);

#if DEBUG
        Console.WriteLine("Headers in _eventBasedClient:");
        foreach (var header in _eventBasedClient.DefaultRequestHeaders)
        {
            if (header.Key == "authxtoken") continue;
            Console.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }
#endif
    }

    public Task<BasicProfile?> GetBasicProfileAsync() => GetBasicProfileAsync(CancellationToken.None);

    public async Task<BasicProfile?> GetBasicProfileAsync(CancellationToken cancellationToken)
    {
        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.Profile);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);
        var response = await _httpClient.GetStringAsync(BasicXboxAPIUris.GamertagUrl, cancellationToken);
        return JsonConvert.DeserializeObject<BasicProfile>(response);
    }

    public async Task<Profile?> GetProfileAsync(string xuid)
    {
        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion5);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.PeopleHub);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);
        var responseString = await _httpClient.GetStringAsync(string.Format(InterpolatedXboxAPIUrls.ProfileUrl, xuid));
        return JsonConvert.DeserializeObject<Profile>(responseString);
    }

    public async Task<GameTitle?> GetGameTitleAsync(string xuid, string titleId)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(titleId))
        {
            // Don't send a request if we don't have the details
            return null;
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        var gameTitleRequest = new GameTitleRequest()
        {
            Pfns = null,
            TitleIds = new List<string>() { titleId }
        };

        var gameTitleResponse = await (await _httpClient.PostAsync(string.Format(InterpolatedXboxAPIUrls.TitleUrl, xuid), new StringContent(JsonConvert.SerializeObject(gameTitleRequest), Encoding.UTF8, HeaderValues.Accept))).Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<GameTitle>(gameTitleResponse);
    }

    public async Task<(int StatusCode, string Body)> GetTitleHubStatsRawAsync(string xuid, string titleId)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(titleId))
        {
            return (-1, "Missing XUID or Title ID");
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.TitleHub);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);

        var request = new GameTitleRequest
        {
            Pfns = null,
            TitleIds = new List<string> { titleId }
        };

        var response = await _httpClient.PostAsync(
            string.Format(InterpolatedXboxAPIUrls.TitleUrl, xuid),
            new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, HeaderValues.Accept));
        var body = await response.Content.ReadAsStringAsync();
        LastTitleHubStatsRawResponse = body;
        WriteTitleHubStatsDebugLog(titleId, body);
        return ((int)response.StatusCode, body);
    }

    private static void WriteTitleHubStatsDebugLog(string titleId, string responseBody)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AchievementLabs", "Debug", "TitleHubStats");
            Directory.CreateDirectory(root);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            File.WriteAllText(Path.Combine(root, $"{timestamp}_{titleId}_raw.json"), responseBody);
        }
        catch
        {
            // Diagnostic logging only - never let a logging failure break the actual call.
        }
    }

    public async Task<Gamepass?> GetGamepassMembershipAsync(string xuid)
    {
        if (string.IsNullOrWhiteSpace(xuid))
        {
            // Don't send a request if we don't have the details
            return null;
        }

        SetDefaultHeaders();
        var gpuResponse = await (await _httpClient.GetAsync(string.Format(InterpolatedXboxAPIUrls.GamepassMembershipUrl, xuid))).Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<Gamepass>(gpuResponse);
    }

    public async Task<TitlesList?> GetGamesListAsync(string xuid, IProgress<AchievementLabs.Core.LibraryRefreshProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xuid))
        {
            // Don't send a request if we don't have the details
            return null;
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.TitleHub);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);
        using var response = await _httpClient.GetAsync(string.Format(InterpolatedXboxAPIUrls.TitlesUrl, xuid), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > 128L * 1024 * 1024) throw new InvalidDataException("Library response exceeds the size limit.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var received = new MemoryStream(); var buffer = new byte[65536];
        progress?.Report(new(0, length));
        while (true) {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (received.Length + count > 128L * 1024 * 1024) throw new InvalidDataException("Library response exceeds the size limit.");
            received.Write(buffer,0,count); progress?.Report(new(received.Length,length));
        }
        var responseString = Encoding.UTF8.GetString(received.ToArray());
        WriteGamesListDebugLog(responseString);
        var result = JsonConvert.DeserializeObject<TitlesList>(responseString);
        progress?.Report(new(received.Length, length, result?.Titles?.Count ?? 0));
        return result;
    }

    private void WriteGamesListDebugLog(string responseBody)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AchievementLabs", "Debug", "GamesList");
            Directory.CreateDirectory(root);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            File.WriteAllText(Path.Combine(root, $"{timestamp}_raw.json"), responseBody);
        }
        catch
        {
            // Diagnostic logging only - never let a logging failure break the actual call.
        }
    }

    public async Task<JObject?> GetGamertagProfileAsync(string gamertag)
    {
        if (string.IsNullOrWhiteSpace(gamertag))
        {
            // Don't send a request if we don't have the details
            return null;
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.Profile);

        string url = string.Format(InterpolatedXboxAPIUrls.GamertagSearch, gamertag);
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var jsonResponse = await response.Content.ReadAsStringAsync();
        return JObject.Parse(jsonResponse);
    }

    public async Task<GameStatsResponse?> GetGameStatsAsync(string xuid, string titleId)
    {
        return await GetGameStatsAsync(xuid, titleId, new List<string> { "MinutesPlayed" });
    }

    public async Task<GameStatsResponse?> GetGameStatsAsync(string xuid, string titleId, List<string> statNames)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);

        var stats = statNames.Select(name => new GameStat { Name = name, TitleId = titleId }).ToList();
        var gameStatsRequest = new GameStatsRequest()
        {
            Xuids = new List<string>() { xuid },
            Stats = stats
        };
        var response = await (await _httpClient
                .PostAsync(BasicXboxAPIUris.UserStatsUrl, new StringContent(JsonConvert.SerializeObject(gameStatsRequest), Encoding.UTF8, HeaderValues.Accept))).Content
                .ReadAsStringAsync();
        LastGameStatsRawResponse = response;
        return JsonConvert.DeserializeObject<GameStatsResponse>(response);
    }

    public async Task<(int StatusCode, string Body)> GetAllGameStatsRawAsync(string xuid, string serviceConfigId)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(serviceConfigId))
        {
            return (-1, "Missing XUID or service config id");
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);

        var url = $"https://userstats.xboxlive.com/users/xuid({xuid})/scids/{serviceConfigId}/stats";
        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body);
    }

    public async Task<string?> GetTitleServiceConfigIdAsync(string xuid, string titleId)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        var gamesList = await GetGamesListAsync(xuid);
        return gamesList?.Titles?
            .FirstOrDefault(title => string.Equals(title.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
            ?.ServiceConfigId;
    }

    public async Task<EventUnlockResult> WriteTitleStatWithDiagnosticsAsync(
        string xuid,
        string serviceConfigId,
        string statName,
        JToken statValue)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(serviceConfigId) || string.IsNullOrWhiteSpace(statName))
        {
            return new EventUnlockResult
            {
                StatusCode = 0,
                ReasonPhrase = "Bad Request",
                RequestUri = string.Empty,
                ResponseBody = "Missing XUID, service config id, or stat name"
            };
        }

        if (!Guid.TryParse(serviceConfigId, out var scid) || scid == Guid.Empty
            || statValue.Type is not (JTokenType.Integer or JTokenType.Float or JTokenType.String or JTokenType.Boolean))
            return new EventUnlockResult { StatusCode = 400, ReasonPhrase = "Invalid stat write input", ResponseBody = "A valid SCID and scalar stat value are required." };

        var revisionInfo = await ReadTitleStatRevisionAsync(xuid, serviceConfigId, statName);
        if (revisionInfo.StatusCode is not (>= 200 and < 300) && revisionInfo.StatusCode != 404)
            return new EventUnlockResult { StatusCode = revisionInfo.StatusCode, ReasonPhrase = "Stat read failed; write not sent", RequestUri = revisionInfo.RequestUri, ResponseBody = revisionInfo.Body };

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion4);

        var attempts = BuildTitleStatWriteAttempts(xuid, serviceConfigId, statName, statValue, revisionInfo.PreviousRevision);
        var attemptLog = new JArray();
        EventUnlockResult? lastResult = null;

        foreach (var attempt in attempts)
        {
            var requestBody = attempt.Body.ToString(Formatting.None);
            using var request = new HttpRequestMessage(new HttpMethod("PATCH"), attempt.Uri)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, HeaderValues.Accept)
            };

            var sw = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request);
            sw.Stop();

            var responseBody = await response.Content.ReadAsStringAsync();
            lastResult = new EventUnlockResult
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? string.Empty,
                RequestUri = request.RequestUri?.ToString() ?? string.Empty,
                RequestBody = requestBody,
                ResponseBody = responseBody,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                RequestHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
                ContentHeaders = request.Content.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
                ResponseHeaders = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value))
            };

            attemptLog.Add(new JObject
            {
                ["name"] = attempt.Name,
                ["uri"] = attempt.Uri,
                ["revisionRead"] = new JObject
                {
                    ["statusCode"] = revisionInfo.StatusCode,
                    ["requestUri"] = revisionInfo.RequestUri,
                    ["previousRevision"] = revisionInfo.PreviousRevision,
                    ["bodyPreview"] = TruncateForLog(revisionInfo.Body, 1000)
                },
                ["request"] = attempt.Body,
                ["statusCode"] = lastResult.StatusCode,
                ["reasonPhrase"] = lastResult.ReasonPhrase,
                ["responseBody"] = lastResult.ResponseBody
            });

            if (lastResult.StatusCode >= 200 && lastResult.StatusCode < 300)
            {
                lastResult.ResponseBody = new JObject
                {
                    ["successfulAttempt"] = attempt.Name,
                    ["responseBody"] = responseBody,
                    ["attempts"] = attemptLog
                }.ToString(Formatting.Indented);
                return lastResult;
            }

            if (lastResult.StatusCode == 429)
            {
                lastResult.ResponseBody = new JObject
                {
                    ["rateLimitedAttempt"] = attempt.Name,
                    ["responseBody"] = responseBody,
                    ["attempts"] = attemptLog
                }.ToString(Formatting.Indented);
                return lastResult;
            }
        }

        lastResult ??= new EventUnlockResult
        {
            StatusCode = 0,
            ReasonPhrase = "No Attempts",
            ResponseBody = "No statswrite attempts were generated"
        };
        lastResult.ResponseBody = new JObject
        {
            ["lastResponseBody"] = lastResult.ResponseBody,
            ["attempts"] = attemptLog
        }.ToString(Formatting.Indented);
        return lastResult;
    }

    private async Task<TitleStatRevisionInfo> ReadTitleStatRevisionAsync(string xuid, string serviceConfigId, string statName)
    {
        var escapedStatName = Uri.EscapeDataString(statName);
        var url = $"https://userstats.xboxlive.com/users/xuid({xuid})/scids/{serviceConfigId}/stats/{escapedStatName}?include=value,metadata";

        try
        {
            SetDefaultHeaders();
            _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);

            using var response = await _httpClient.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            var revision = ExtractStatRevision(body);
            return new TitleStatRevisionInfo
            {
                StatusCode = (int)response.StatusCode,
                RequestUri = url,
                Body = body,
                PreviousRevision = revision
            };
        }
        catch (Exception ex)
        {
            return new TitleStatRevisionInfo
            {
                StatusCode = 0,
                RequestUri = url,
                Body = ex.ToString(),
                PreviousRevision = 0
            };
        }
    }

    public async Task<(int StatusCode, string Body)> GetTitleStatRawAsync(string xuid, string serviceConfigId, string statName)
    {
        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        var url = $"https://userstats.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/scids/{Uri.EscapeDataString(serviceConfigId)}/stats/{Uri.EscapeDataString(statName)}?include=value,metadata";
        using var response = await _httpClient.GetAsync(url);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static long ExtractStatRevision(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return 0;

        try
        {
            var root = JToken.Parse(responseBody);
            var tokens = root is JContainer container
                ? container.DescendantsAndSelf()
                : new[] { root };

            foreach (var propertyName in new[] { "previousRevision", "PreviousRevision", "revision", "Revision" })
            {
                foreach (var obj in tokens.OfType<JObject>())
                {
                    if (TryReadLong(obj[propertyName], out var value))
                        return value;
                }
            }
        }
        catch
        {
            // Keep the write path available even if the read payload shape changes.
        }

        return 0;
    }

    private static bool TryReadLong(JToken? token, out long value)
    {
        value = 0;
        if (token == null || token.Type == JTokenType.Null)
            return false;

        if (token.Type == JTokenType.Integer)
        {
            value = token.Value<long>();
            return true;
        }

        return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static IReadOnlyList<(string Name, string Uri, JObject Body)> BuildTitleStatWriteAttempts(
        string xuid,
        string serviceConfigId,
        string statName,
        JToken statValue,
        long previousRevision)
    {
        var uri = $"https://statswrite.xboxlive.com/stats/users/{xuid}/scids/{serviceConfigId}";
        var schema = "http://stats.xboxlive.com/2017-1/schema#";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var revision = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), checked(previousRevision + 1));
        var writeValue = statValue.Type == JTokenType.String
            ? statValue.Value<string>() ?? string.Empty
            : statValue.ToString(Formatting.None);

        return new List<(string Name, string Uri, JObject Body)>
        {
            (
                "XauPluginTitlePatch",
                uri,
                new JObject
                {
                    ["$schema"] = schema,
                    ["previousRevision"] = previousRevision,
                    ["revision"] = revision,
                    ["timestamp"] = timestamp,
                    ["stats"] = new JObject
                    {
                        ["title"] = new JObject
                        {
                            [statName] = new JObject
                            {
                                ["value"] = writeValue
                            }
                        }
                    }
                }
            )
        };
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }

    private sealed class TitleStatRevisionInfo
    {
        public int StatusCode { get; init; }
        public string RequestUri { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public long PreviousRevision { get; init; }
    }

    public async Task<(int StatusCode, string Body)> SendHeartbeatAsync(string xuid, string spoofedTitleId, bool useFakeSignature = false)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(spoofedTitleId))
        {
            return (-1, "Missing XUID or Title ID");
        }

        SetDefaultSpooferHeaders();
        _spooferClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion3);
        var heartbeatRequest = new HeartbeatRequest()
        {
            titles = new List<TitleRequest>()
            {
                new TitleRequest()
                {
                    id = spoofedTitleId
                }
            }
        };
        var requestBody = JsonConvert.SerializeObject(heartbeatRequest);
        var requestUrl = string.Format(InterpolatedXboxAPIUrls.HeartbeatUrl, xuid);
        var sw = Stopwatch.StartNew();
        var response = await _spooferClient.PostAsync(
            requestUrl,
            new StringContent(requestBody, Encoding.UTF8, HeaderValues.Accept));
        var body = await response.Content.ReadAsStringAsync();
        sw.Stop();

        WriteHeartbeatDebugLog("POST", spoofedTitleId, xuid, requestUrl, requestBody,
            (int)response.StatusCode, response.ReasonPhrase, body, sw.ElapsedMilliseconds,
            response.Headers);

#if DEBUG
        Console.WriteLine($"Heartbeat POST response: {(int)response.StatusCode} {response.StatusCode} - {body}");
#endif
        return await AchievementLabs.MultiSelect.PresenceDiagnostics.Observe(
            Task.FromResult(((int)response.StatusCode, body)), this, spoofedTitleId);
    }

    public async Task<(int StatusCode, string Body)> StopHeartbeatAsync(string xuid, bool useFakeSignature = false)
    {
        if (string.IsNullOrWhiteSpace(xuid))
        {
            return (-1, "Missing XUID");
        }

        SetDefaultSpooferHeaders();
        _spooferClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion3);
        var requestUrl = string.Format(InterpolatedXboxAPIUrls.HeartbeatUrl, xuid);
        var sw = Stopwatch.StartNew();
        var response = await _spooferClient.DeleteAsync(requestUrl);
        var body = await response.Content.ReadAsStringAsync();
        sw.Stop();

        WriteHeartbeatDebugLog("DELETE", "stop", xuid, requestUrl, null,
            (int)response.StatusCode, response.ReasonPhrase, body, sw.ElapsedMilliseconds,
            response.Headers);

#if DEBUG
        Console.WriteLine($"Heartbeat DELETE response: {(int)response.StatusCode} {response.StatusCode} - {body}");
#endif
        return ((int)response.StatusCode, body);
    }

    private void WriteHeartbeatDebugLog(string method, string titleId, string xuid, string url,
        string? requestBody, int statusCode, string? reasonPhrase, string responseBody,
        long elapsedMs, System.Net.Http.Headers.HttpResponseHeaders responseHeaders)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AchievementLabs", "Debug", "Heartbeat");
            Directory.CreateDirectory(root);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var logFile = Path.Combine(root, $"{timestamp}_{method}_{titleId}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"Method: {method}");
            sb.AppendLine($"URL: {url}");
            sb.AppendLine($"TitleId: {titleId}");
            sb.AppendLine($"XUID: {xuid}");
            sb.AppendLine($"Timestamp (UTC): {DateTime.UtcNow:O}");
            sb.AppendLine();

            if (requestBody != null)
            {
                sb.AppendLine("=== REQUEST BODY ===");
                sb.AppendLine(requestBody);
                sb.AppendLine();
            }

            sb.AppendLine("=== RESPONSE ===");
            sb.AppendLine($"HTTP {statusCode} {reasonPhrase}");
            sb.AppendLine($"Elapsed: {elapsedMs} ms");
            sb.AppendLine();

            sb.AppendLine("=== RESPONSE HEADERS ===");
            foreach (var header in responseHeaders)
            {
                sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            }
            sb.AppendLine();

            sb.AppendLine("=== RESPONSE BODY ===");
            sb.AppendLine(responseBody);

            File.WriteAllText(logFile, sb.ToString());
        }
        catch
        {
            // Debug logging should never break the actual functionality
        }
    }

    public async Task<int> GetAchievementTotalAsync(string xuid, string titleId, CancellationToken ct, bool legacy = false)
        => (await GetAchievementCountsAsync(xuid, titleId, ct, legacy)).Total;
    public async Task<AchievementLabs.Core.AchievementTotals.Counts> GetAchievementCountsAsync(string xuid, string titleId, CancellationToken ct, bool legacy = false)
    {
        if (!ulong.TryParse(xuid, out _) || !uint.TryParse(titleId, out _)) throw new ArgumentException("Invalid Xbox identifiers.");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _xauth);
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-xbl-contract-version", legacy ? "3" : "4");
        return await AchievementLabs.Core.AchievementTotals.MeasureAsync(async continuation =>
        {
            var url = string.Format(legacy ? InterpolatedXboxAPIUrls.QueryAchievements360Url : InterpolatedXboxAPIUrls.QueryAchievementsUrl, xuid, titleId);
            if (!string.IsNullOrEmpty(continuation)) url += "&continuationToken=" + Uri.EscapeDataString(continuation);
            return await AchievementLabs.Core.AchievementTotals.ReadPageAsync(http, url, ct);
        }, ct);
    }

    public async Task<AchievementsResponse?> GetAchievementsForTitleAsync(string xuid, string titleId)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(titleId))
        {
            // Don't send a request if we don't have the details
            return null;
        }
        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion4);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.Achievements);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);

        var response = await (await _httpClient.GetAsync(string.Format(InterpolatedXboxAPIUrls.QueryAchievementsUrl, xuid, titleId))).Content.ReadAsStringAsync();
        LastAchievementsRawResponse = response;
        WriteAchievementsDebugLog(titleId, response);
        var achievements = JsonConvert.DeserializeObject<AchievementsResponse>(response);
        return achievements;
    }

    private static void WriteAchievementsDebugLog(string titleId, string responseBody)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AchievementLabs", "Debug", "Achievements");
            Directory.CreateDirectory(root);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            File.WriteAllText(Path.Combine(root, $"{timestamp}_{titleId}_raw.json"), responseBody);
        }
        catch
        {
            // Diagnostic logging only - never let a logging failure break the actual call.
        }
    }

    public async Task<Xbox360AchievementResponse?> GetAchievementsFor360TitleAsync(string xuid, string titleId)
    {
        if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(titleId))
        {
            // Don't send a request if we don't have the details
            return null;
        }
        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion3);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.Achievements);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);
        using var httpResponse = await _httpClient.GetAsync(string.Format(InterpolatedXboxAPIUrls.QueryAchievements360Url, xuid, titleId));
        httpResponse.EnsureSuccessStatusCode();
        var response = await httpResponse.Content.ReadAsStringAsync();
        var achievements = JsonConvert.DeserializeObject<Xbox360AchievementResponse>(response);
        return achievements;
    }

    public async Task UnlockTitleBasedAchievementAsync(string serviceConfigId, string titleId, string xuid, string achievementId, bool useFakeSignature = false)
    {
        // only unlock the specified achievement
        await UnlockTitleBasedAchievementsAsync(serviceConfigId, titleId, xuid, new List<string>() { achievementId }, useFakeSignature);
    }

    public async Task UnlockTitleBasedAchievementsAsync(string serviceConfigId, string titleId, string xuid, List<string> achievementIds, bool useFakeSignature = false)
    {
        if (string.IsNullOrWhiteSpace(serviceConfigId) || string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(xuid) || achievementIds.Count == 0)
        {
            // Don't send a request if we don't have the details
            return;
        }

        SetDefaultHeaders();
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.ContractVersion, HeaderValues.ContractVersion2);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Host, Hosts.Achievements);
        _httpClient.DefaultRequestHeaders.Add(HeaderNames.Connection, HeaderValues.KeepAlive);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "XboxServicesAPI/2021.10.20211005.0 c");

        if (useFakeSignature)
        {
            _httpClient.DefaultRequestHeaders.Add(HeaderNames.Signature, HeaderValues.Signature);
        }

        // Split the requests into 50 achievements each. Anything over 100 seems to BadRequest. TODO: look into
        // headers and see if we can send long data or w/e
        const int chunkSize = 50;
        for (int i = 0; i < achievementIds.Count; i += chunkSize)
        {
            var chunk = achievementIds.Skip(i).Take(chunkSize).ToList();

            var unlockRequest = new UnlockTitleBasedAchievementRequest
            {
                titleId = titleId,
                serviceConfigId = serviceConfigId,
                userId = xuid,
                achievements = chunk.Select(id => new AchievementsArrayEntry { id = id, percentComplete = "100" }).ToList()
            };

            var unlockBodyStr = JsonConvert.SerializeObject(unlockRequest);
            var bodyconverted = new StringContent(unlockBodyStr, Encoding.UTF8, HeaderValues.Accept);

            var response = await _httpClient.PostAsync(
                string.Format(InterpolatedXboxAPIUrls.UpdateAchievementsUrl, xuid, serviceConfigId), bodyconverted);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"Failed to unlock achievement(s) for title {titleId} with status code {response.StatusCode}");
            }
        }
    }

    // TODO: see if we can handle the actual request body building
    public async Task<EventUnlockResult> UnlockEventBasedAchievementWithDiagnostics(string eventsToken, string requestBody)
    {
        if (string.IsNullOrWhiteSpace(eventsToken))
        {
            // Don't send a request if we don't have the details
            return new EventUnlockResult
            {
                StatusCode = 0,
                ReasonPhrase = "No events token provided",
                ResponseBody = "No events token provided",
                RequestUri = BasicXboxAPIUris.TelemetryUrl
            };
        }

        SetDefaultEventBasedHeaders();
        _eventBasedClient.DefaultRequestHeaders.Add("tickets", $"\"1\"=\"{eventsToken}\"");
        var requestHeaders = _eventBasedClient.DefaultRequestHeaders.ToDictionary(
            header => header.Key,
            header => string.Join(", ", header.Value));

        var content = new StringContent(requestBody, Encoding.UTF8, "application/x-json-stream");
        var contentHeaders = content.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(", ", header.Value));

        var stopwatch = Stopwatch.StartNew();
        var response = await _eventBasedClient.PostAsync(BasicXboxAPIUris.TelemetryUrl, content);
        stopwatch.Stop();

        var responseBody = await response.Content.ReadAsStringAsync();
        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value));

        AchievementLabs.Core.EventCollectorResponse.EnsureNoRejection((int)response.StatusCode, responseBody);

        return new EventUnlockResult
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase ?? string.Empty,
            ResponseBody = responseBody,
            RequestUri = BasicXboxAPIUris.TelemetryUrl,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            RequestHeaders = requestHeaders,
            ContentHeaders = contentHeaders,
            ResponseHeaders = responseHeaders
        };
    }

    public async Task<(int StatusCode, string ResponseBody)> UnlockEventBasedAchievement(string eventsToken, StringContent requestBody)
    {
        var body = await requestBody.ReadAsStringAsync();
        var result = await UnlockEventBasedAchievementWithDiagnostics(eventsToken, body);
        return (result.StatusCode, result.ResponseBody);
    }

    public async Task<GamePassProducts?> GetTitleIdsFromGamePass(string prodId)
    {
        if (string.IsNullOrWhiteSpace(prodId))
        {
            // Don't send a request if we don't have the details
            return null;
        }

        SetDefaultHeaders();
        GamepassProductsRequest gamepassProducts = new GamepassProductsRequest()
        {
            Products = new List<string>() { prodId }
        };
        var titleIDsResponse = await (await _httpClient.PostAsync(
                    BasicXboxAPIUris.GamepassCatalogUrl,
                    new StringContent(JsonConvert.SerializeObject(gamepassProducts)))).Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<GamePassProducts>(titleIDsResponse);
    }
}

public class EventUnlockResult
{
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public string RequestUri { get; set; } = string.Empty;
    public long ElapsedMilliseconds { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new();
    public Dictionary<string, string> ContentHeaders { get; set; } = new();
    public Dictionary<string, string> ResponseHeaders { get; set; } = new();
}
