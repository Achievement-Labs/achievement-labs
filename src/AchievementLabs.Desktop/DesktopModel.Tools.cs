using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
namespace AchievementLabs.Desktop;
public sealed record TitleSearchResult(string TitleId, string Name, string Platform, string? ImageUrl, bool? HasDboxAchievements = null, bool? HasXboxAchievements = null)
{
    public string Monogram => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0])));
    public bool CanAddXbox360Gpd => Platform.Contains("Xbox 360", StringComparison.OrdinalIgnoreCase);
    public bool HasVerifiedAchievements => HasDboxAchievements == true || HasXboxAchievements == true;
    public string AchievementCatalogStatus => HasDboxAchievements == true ? "DBox achievements found" : HasXboxAchievements switch
    {
        true => "Xbox achievements found",
        false when HasDboxAchievements == false => "No achievement records found",
        _ => "Achievement check unavailable"
    };
}
public sealed partial class DesktopModel
{
    private string titleSearch = "", gamertagSearch = "", lookupOutput = "", lookupXuid = "";
    private TitleSearchResult[] titleSearchResults = [];
    private TitleSearchResult[] allTitleSearchResults = [];
    private readonly ConcurrentDictionary<string, bool?> dboxAchievementCache = new(StringComparer.OrdinalIgnoreCase);
    private bool onlyVerifiedAchievementTitles = true;
    private bool excludeZeroScore, exclude360;
    private string converterStatus = "Not scanned.";
    public bool IsTools => page == "Tools";
    public bool IsAbout => page == "About";
    public string TitleSearch { get => titleSearch; set { titleSearch = value; Changed(); } }
    public string GamertagSearch { get => gamertagSearch; set { gamertagSearch = value; Changed(); } }
    public string LookupOutput { get => lookupOutput; private set { lookupOutput = value; Changed(); } }
    public TitleSearchResult[] TitleSearchResults { get => titleSearchResults; private set { titleSearchResults = value; Changed(); } }
    public bool OnlyVerifiedAchievementTitles
    {
        get => onlyVerifiedAchievementTitles;
        set
        {
            onlyVerifiedAchievementTitles = value;
            Changed();
            TitleSearchResults = value ? allTitleSearchResults.Where(item => item.HasVerifiedAchievements).ToArray() : allTitleSearchResults;
            if (allTitleSearchResults.Length > 0)
                LookupOutput = $"Showing {TitleSearchResults.Length} of {allTitleSearchResults.Length} titles; {allTitleSearchResults.Count(item => !item.HasVerifiedAchievements)} remain unverified.";
        }
    }
    public bool ExcludeZeroScore { get => excludeZeroScore; set { excludeZeroScore = value; Changed(); } }
    public bool Exclude360 { get => exclude360; set { exclude360 = value; Changed(); } }
    public string ConverterStatus { get => converterStatus; private set { converterStatus = value; Changed(); } }
    public void RefreshConverterStatus()
    {
        var path = steam.GetConverterPath();
        ConverterStatus = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? "Converter workspace found: " + path
            : "Converter workspace was not found. The original converter workflow is still a folder-only beta.";
    }
    public async Task SearchTitlesAsync()
    {
        if (busy || string.IsNullOrWhiteSpace(TitleSearch)) return; Busy(true);
        try
        {
            using var http = new HttpClient();
            var found = new Dictionary<string, TitleSearchResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var query in SearchSpellings(TitleSearch))
            {
                var json = JObject.Parse(await http.GetStringAsync("https://dbox.tools/api/title_ids/?name=" + Uri.EscapeDataString(query) + "&limit=100&offset=0", lifetime.Token));
                foreach (var item in json["items"] as JArray ?? [])
                {
                    var hexadecimalId = item["title_id"]?.ToString() ?? item["titleId"]?.ToString() ?? "";
                    var decimalId = ConvertTitleIdToDecimal(hexadecimalId);
                    var name = NormalizeLookalikes(item["name"]?.ToString() ?? "Unknown title");
                    var systems = (item["systems"] as JArray ?? []).Values<string>().ToArray();
                    var platform = IsKnownWindows8Title(decimalId) ? "Windows 8" :
                        IsKnownGfwlTitle(decimalId) || systems.Contains("PC") && systems.Contains("XBOX360")
                        ? "GFWL" : string.Join(", ", systems.Select(FormatXboxPlatform).Distinct());
                    var image = Games.FirstOrDefault(game => game.Id == decimalId)?.ImageUrl;
                    found.TryAdd(decimalId, new TitleSearchResult(decimalId, name, string.IsNullOrWhiteSpace(platform) ? "Platform unavailable" : platform, image));
                }
                if (found.Values.Any(item => string.Equals(item.Name, NormalizeLookalikes(TitleSearch), StringComparison.OrdinalIgnoreCase))) break;
            }
            var candidates = found.Values
                .OrderBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(result => result.Platform, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            LookupOutput = $"Checking achievement catalogs for {candidates.Length} titles…";
            using var limit = new SemaphoreSlim(8);
            var checks = candidates.Select(async item =>
            {
                await limit.WaitAsync(lifetime.Token);
                try
                {
                    var id = uint.TryParse(item.TitleId, out var numeric) ? numeric.ToString("X8") : "";
                    if (id.Length == 0) return item;
                    if (!dboxAchievementCache.TryGetValue(id, out var hasAchievements))
                    {
                        hasAchievements = await HasDboxAchievementsAsync(http, id, lifetime.Token);
                        if (hasAchievements.HasValue) dboxAchievementCache[id] = hasAchievements;
                    }
                    bool? xboxAchievements = null;
                    if (hasAchievements != true && session is { } connected)
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(12));
                            xboxAchievements = await HasXboxAchievementsAsync(http, connected.Authorization, connected.Xuid, item, timeout.Token);
                            if (xboxAchievements != true && item.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                                xboxAchievements = (await Windows8Achievements.ReadAsync(http, connected.Authorization, connected.Xuid, ResolveWindows8TitleId(item.TitleId), timeout.Token)).Length > 0;
                        }
                        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException) { }
                    }
                    return item with { HasDboxAchievements = hasAchievements, HasXboxAchievements = xboxAchievements };
                }
                finally { limit.Release(); }
            });
            allTitleSearchResults = await Task.WhenAll(checks);
            TitleSearchResults = OnlyVerifiedAchievementTitles
                ? allTitleSearchResults.Where(item => item.HasVerifiedAchievements).ToArray()
                : allTitleSearchResults;
            LookupOutput = $"Showing {TitleSearchResults.Length} of {allTitleSearchResults.Length} titles; {allTitleSearchResults.Count(item => !item.HasVerifiedAchievements)} remain unverified.";
        }
        catch (Exception error) { LookupOutput = $"Title search failed: {error.Message}"; }
        finally { Busy(false); }
    }
    private static async Task<bool?> HasXboxAchievementsAsync(HttpClient http, string authorization, string xuid, TitleSearchResult item, CancellationToken cancellationToken)
    {
        var route = item.CanAddXbox360Gpd ? "titleachievements" : "achievements";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://achievements.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/{route}?titleId={Uri.EscapeDataString(item.TitleId)}&maxItems=1000");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "2");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return json.RootElement.TryGetProperty("achievements", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() > 0 : null;
    }
    internal static string NormalizeLookalikes(string value)
    {
        if (!value.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) return value;
        const string lookalikes = "АаВЕеКкМНОоРрСсТтуУХх";
        const string latin =      "AaBEeKkMHOoPpCcTtyYXx";
        return new string(value.Select(c => { var index = lookalikes.IndexOf(c); return index < 0 ? c : latin[index]; }).ToArray());
    }
    internal static IEnumerable<string> SearchSpellings(string value)
    {
        yield return value;
        var canonical = NormalizeLookalikes(value);
        if (canonical != value) yield return canonical;
        var substitutions = new Dictionary<char, char> { ['C'] = 'С', ['c'] = 'с', ['A'] = 'А', ['a'] = 'а', ['E'] = 'Е', ['e'] = 'е', ['O'] = 'О', ['o'] = 'о', ['P'] = 'Р', ['p'] = 'р', ['X'] = 'Х', ['x'] = 'х' };
        var count = 0;
        foreach (var i in Enumerable.Range(0, value.Length).OrderBy(i => value[i] is 'C' or 'c' ? 0 : 1))
        {
            if (count >= 6) break;
            if (substitutions.TryGetValue(value[i], out var replacement))
            {
                var chars = value.ToCharArray(); chars[i] = replacement;
                yield return new string(chars);
                count++;
            }
        }
    }
    private static async Task<bool?> HasDboxAchievementsAsync(HttpClient http, string titleId, CancellationToken cancellationToken)
    {
        var unknown = false;
        foreach (var version in new[] { "v2", "v1" })
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await http.GetAsync(
                    $"https://dbox.tools/api/achievements/{version}/{titleId}",
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) { unknown = true; continue; }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                if (json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0)
                    return true;
                if (json.RootElement.ValueKind != JsonValueKind.Array) unknown = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { unknown = true; }
            catch (HttpRequestException) { unknown = true; }
            catch (JsonException) { unknown = true; }
        }
        return unknown ? null : false;
    }
    internal static string ConvertTitleIdToDecimal(string hexadecimalId) =>
        uint.TryParse(hexadecimalId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : hexadecimalId;

    private static string FormatXboxPlatform(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "XBOXONE" => "Xbox One",
        "XBOXSERIES" or "SCARLETT" => "Xbox Series X|S",
        "XBOX360" => "Xbox 360",
        "PC" or "WINDOWS" => "Windows",
        "MOBILE" => "Mobile",
        "IOS" => "iOS",
        "ANDROID" => "Android",
        "SWITCH" => "Nintendo Switch",
        var other when !string.IsNullOrWhiteSpace(other) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.ToLowerInvariant()),
        _ => ""
    };
    public async Task SearchGamertagAsync()
    {
        if (!CanQuery || string.IsNullOrWhiteSpace(GamertagSearch)) return;
        lookupXuid = "";
        await WithAccountAsync(async (api, _) =>
        {
            var profile = await api.GetGamertagProfileAsync(GamertagSearch);
            var user = profile?["profileUsers"]?.FirstOrDefault() ?? throw new InvalidDataException();
            lookupXuid = user["id"]?.ToString() ?? "";
            LookupOutput = user.ToString();
        });
    }
    public async Task ExportProfileGamesAsync(string path)
    {
        if (lookupXuid.Length == 0) { Notice = "Look up a gamertag first."; return; }
        await WithAccountAsync(async (api, _) =>
        {
            static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var titles = await api.GetGamesListAsync(lookupXuid) ?? throw new InvalidDataException();
            var lines = new List<string> { "Title ID,Title,CurrentAchievements,Gamerscore,Progress,Devices,Genres" };
            foreach (var title in titles.Titles)
            {
                if (ExcludeZeroScore && (title.Achievement?.TotalGamerscore ?? 0) == 0 || Exclude360 && title.Devices.Contains("Xbox360")) continue;
                lines.Add(string.Join(",", new[] { title.TitleId ?? "", title.Name ?? "", title.Achievement?.CurrentAchievements.ToString() ?? "", $"{title.Achievement?.CurrentGamerscore}/{title.Achievement?.TotalGamerscore}", title.Achievement?.ProgressPercentage.ToString() ?? "", string.Join(", ", title.Devices), string.Join(", ", title.Detail?.Genres ?? []) }.Select(Csv)));
            }
            await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), lifetime.Token); Notice = $"Exported {lines.Count - 1} games.";
        });
    }
    public void OpenUtilityLink(string destination)
    {
        try
        {
            if (destination == "Steam converter") RefreshConverterStatus();
            var target = destination switch { "Discord" => "https://discord.gg/EY6AJpNfVu", "GitHub" => "https://github.com/ethanwp28/achievement-labs/", "Original XAU" => "https://github.com/Fumo-Unlockers/Xbox-Achievement-Unlocker", "Steam converter" => steam.GetConverterPath(), _ => throw new InvalidOperationException() };
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { Notice = "Could not open the requested link or workspace."; }
    }
}
