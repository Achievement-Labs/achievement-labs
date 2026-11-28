using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Globalization;
using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

internal static class Windows8Achievements
{
    internal static Task<AchievementVerificationResult> VerifyAsync(HttpClient http, string authorization, string xuid,
        string titleId, string achievementId, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        AchievementUnlockVerifier.VerifyAsync(async token =>
        {
            var response = await ReadQueueAsync(http, authorization, xuid, titleId, token);
            var matches = response.achievements.Where(row => row.id == achievementId).ToArray();
            return matches.Length == 1 ? new AchievementProgressSnapshot(matches[0].progressState, "Xbox state: " + matches[0].progressState) : null;
        }, cancellationToken, delay);

    internal static Task<AchievementsResponse> ReadQueueAsync(HttpClient http, string authorization, string xuid, string titleId, CancellationToken cancellationToken) =>
        ReadQueueWithCatalogAsync(http, authorization, xuid, xuid, titleId, cancellationToken);

    internal static Task<AchievementsResponse> ReadReferenceQueueAsync(HttpClient http, string authorization, string ownerXuid,
        string referenceXuid, string titleId, CancellationToken cancellationToken) =>
        ReadQueueWithCatalogAsync(http, authorization, ownerXuid, referenceXuid, titleId, cancellationToken);

    internal static async Task<AchievementsResponse> ReadCatalogQueueAsync(HttpClient http, string authorization, string ownerXuid,
        string titleId, CancellationToken cancellationToken) =>
        new() { achievements = await ReadQueueEndpointAsync(http, authorization, ownerXuid, titleId, true, cancellationToken) };

    private static async Task<AchievementsResponse> ReadQueueWithCatalogAsync(HttpClient http, string authorization,
        string catalogXuid, string historyXuid, string titleId, CancellationToken cancellationToken)
    {
        List<OneCoreAchievementResponse> catalog;
        try { catalog = await ReadQueueEndpointAsync(http, authorization, catalogXuid, titleId, true, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { catalog = []; }
        List<OneCoreAchievementResponse> history;
        try { history = await ReadQueueEndpointAsync(http, authorization, historyXuid, titleId, false, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { history = []; }
        var merged = catalog.ToDictionary(row => row.id, StringComparer.Ordinal);
        foreach (var row in history)
        {
            if (merged.TryGetValue(row.id, out var definition))
            {
                if (row.progressState == "Achieved")
                {
                    definition.progressState = "Achieved";
                    if (!string.IsNullOrWhiteSpace(row.progression?.timeUnlocked)) definition.progression = row.progression;
                }
            }
            else merged[row.id] = row;
        }
        return new AchievementsResponse { achievements = merged.Values.ToList() };
    }

    private static async Task<List<OneCoreAchievementResponse>> ReadQueueEndpointAsync(HttpClient http, string authorization, string xuid, string titleId, bool catalog, CancellationToken cancellationToken)
    {
        var rows = new List<OneCoreAchievementResponse>();
        string? continuation = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var host = catalog ? "progress.xboxlive.com" : "achievements.xboxlive.com";
            var route = catalog ? "progress/titleachievements" : "achievements";
            var url = $"https://{host}/users/xuid({Uri.EscapeDataString(xuid)})/{route}?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000";
            if (continuation != null) url += "&continuationToken=" + Uri.EscapeDataString(continuation);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("achievements", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Windows 8 achievement data was not returned.");
            foreach (var item in array.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idValue) ? idValue.ToString() : "";
                if (!uint.TryParse(id, out _)) continue;
                if (item.TryGetProperty("titleId", out var rowTitle) && rowTitle.ToString() != titleId)
                    throw new InvalidDataException("Windows 8 achievement data belongs to a different title.");
                var name = item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() ?? id : id;
                var unlocked = item.TryGetProperty("unlocked", out var unlockedValue) && unlockedValue.ValueKind == JsonValueKind.True;
                unlocked |= item.TryGetProperty("progressState", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "Achieved";
                if (item.TryGetProperty("unlocked", out var explicitFlag) && explicitFlag.ValueKind == JsonValueKind.False) unlocked = false;
                else if (!catalog) unlocked = true;
                var time = item.TryGetProperty("timeUnlocked", out var timeValue) && timeValue.ValueKind == JsonValueKind.String ? timeValue.GetString() : null;
                if (time == null && item.TryGetProperty("progression", out var progression) && progression.ValueKind == JsonValueKind.Object && progression.TryGetProperty("timeUnlocked", out var nestedTime) && nestedTime.ValueKind == JsonValueKind.String)
                    time = nestedTime.GetString();
                var description = item.TryGetProperty("description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() : null;
                var score = item.TryGetProperty("gamerscore", out var scoreValue) && int.TryParse(scoreValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedScore) ? parsedScore : 0;
                rows.Add(new OneCoreAchievementResponse { id = id, name = name, description = description, gamerscore = score,
                    progressState = unlocked ? "Achieved" : "NotStarted", progression = new AchievementProgression { timeUnlocked = unlocked ? time : null },
                    titleAssociations = [new TitleAssociation { id = titleId }] });
            }
            continuation = document.RootElement.TryGetProperty("pagingInfo", out var paging) && paging.TryGetProperty("continuationToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
            if (!string.IsNullOrEmpty(continuation) && !seen.Add(continuation)) throw new InvalidDataException("Windows 8 achievement data repeated a page token.");
        } while (!string.IsNullOrEmpty(continuation));
        return rows;
    }

    internal static async Task<Achievement[]> ReadAsync(HttpClient http, string authorization, string xuid, string titleId, CancellationToken cancellationToken)
    {
        Achievement[] catalog;
        try { catalog = await ReadEndpointAsync(http, authorization, xuid, titleId, true, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { catalog = []; }
        Achievement[] history;
        try { history = await ReadEndpointAsync(http, authorization, xuid, titleId, false, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { history = []; }
        var merged = catalog.ToDictionary(row => row.Id, StringComparer.Ordinal);
        foreach (var row in history)
        {
            if (merged.TryGetValue(row.Id, out var definition))
                merged[row.Id] = definition with { Unlocked = definition.Unlocked || row.Unlocked };
            else merged[row.Id] = row;
        }
        return merged.Values.OrderBy(row => uint.TryParse(row.Id, out var id) ? id : uint.MaxValue).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    private static async Task<Achievement[]> ReadEndpointAsync(HttpClient http, string authorization, string xuid, string titleId, bool catalog, CancellationToken cancellationToken)
    {
        var rows = new List<Achievement>();
        string? continuation = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var host = catalog ? "progress.xboxlive.com" : "achievements.xboxlive.com";
            var route = catalog ? "progress/titleachievements" : "achievements";
            var url = $"https://{host}/users/xuid({Uri.EscapeDataString(xuid)})/{route}?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000";
            if (continuation != null) url += "&continuationToken=" + Uri.EscapeDataString(continuation);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("achievements", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Windows 8 achievement data was not returned.");
            foreach (var item in array.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idValue) ? idValue.ToString() : "";
                if (id.Length == 0) continue;
                var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? id : id;
                var description = item.TryGetProperty("description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() ?? "" : "";
                var score = item.TryGetProperty("gamerscore", out var scoreValue) && scoreValue.ValueKind == JsonValueKind.Number && scoreValue.TryGetInt32(out var parsedScore) ? parsedScore : 0;
                var unlocked = item.TryGetProperty("unlocked", out var unlockedValue) && unlockedValue.ValueKind == JsonValueKind.True;
                unlocked |= item.TryGetProperty("progressState", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "Achieved";
                rows.Add(new Achievement(id, name, description, score, unlocked));
            }
            continuation = document.RootElement.TryGetProperty("pagingInfo", out var paging) && paging.TryGetProperty("continuationToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
            if (!string.IsNullOrEmpty(continuation) && !seen.Add(continuation)) throw new InvalidDataException("Windows 8 achievement data repeated a page token.");
        } while (!string.IsNullOrEmpty(continuation));
        return rows.ToArray();
    }
}
