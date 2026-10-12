using System.Text;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using AchievementLabs.Services.LegacyXbox;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private string platformFilter = "All", titleLookup = "";
    private readonly ConcurrentDictionary<string, bool> gfwlTitleCache = new();
    private int legacyTabIndex;
    private Game? selectedGfwlTitle;
    public string[] PlatformFilters { get; } = ["All", "Xbox One/Series", "PC", "Xbox 360", "GFWL", "Win32", "Windows 8/Legacy", "Incomplete Games"];
    public string PlatformFilter { get => platformFilter; set { platformFilter = value ?? "All"; Changed(); Changed(nameof(VisibleGames)); } }
    public string TitleLookup { get => titleLookup; set { titleLookup = value ?? ""; Changed(); } }
    public int LegacyTabIndex { get => legacyTabIndex; set { legacyTabIndex = value; Changed(); } }
    public Game? SelectedGfwlTitle { get => selectedGfwlTitle; private set { selectedGfwlTitle = value; Changed(); } }
    public bool CanQuery => !QueueActive && !busy && session != null;
    public bool CanExport => achievements.Length > 0;
    public static bool HasDevice(Game game, string device) => game.Platform.Split('/', StringSplitOptions.TrimEntries).Contains(device, StringComparer.OrdinalIgnoreCase);
    public static bool UsesLegacyEndpoint(Game game) => new[] { "Xbox360", "GFWL", "Mobile", "WindowsPhone", "Win8", "Windows8" }.Any(d => HasDevice(game, d));
    public static bool MatchesPlatform(Game game, string filter) => filter switch
    {
        "Xbox One/Series" => HasDevice(game, "XboxOne") || HasDevice(game, "XboxSeries"),
        "PC" => HasDevice(game, "PC") || HasDevice(game, "GFWL"), "Xbox 360" => HasDevice(game, "Xbox360"), "GFWL" => HasDevice(game, "GFWL"), "Win32" => HasDevice(game, "Win32"),
        "Windows 8/Legacy" => UsesLegacyEndpoint(game), "Incomplete Games" => game.ProgressKnown && game.Completed < game.Total, _ => true
    };
    public static bool IsKnownGfwlTitle(string titleId)
    {
        if (!uint.TryParse(titleId, out var id)) return false;
        var hex = id.ToString("X8");
        return hex == "4E4D0FA1" || (id & 0xFFFF) is >= 0x0FA0 and <= 0x0FFF;
    }
    // Xbox's PC title history can use a modern alias while legacy achievements use the original title ID.
    private static readonly Dictionary<string, string> Windows8ModernAliases = new(StringComparer.Ordinal)
    {
        ["2024867567"] = "1297292152", // Cold Alley
        ["1791554324"] = "1297292148", // Fishdom 3
        ["1941986744"] = "1297292157", // Halo: Spartan Assault
        ["2122543949"] = "1297292194", // Halo: Spartan Strike
        ["1679995514"] = "1297292136"  // TY the Tasmanian Tiger
    };
    public static string ResolveWindows8TitleId(string titleId) => Windows8ModernAliases.TryGetValue(titleId, out var legacyId) ? legacyId : titleId;
    public static bool IsKnownWindows8Title(string titleId)
    {
        var legacyId = ResolveWindows8TitleId(titleId);
        return LegacyXboxTitleCatalog.KnownTitles.Any(title => title.Platform == "Windows 8" &&
            (title.ProductionTitleIdDecimal == legacyId || title.PartnerTitleIdDecimal == legacyId));
    }
    private static string ClassifyXboxPlatform(string titleId, IEnumerable<string> devices)
    {
        if (IsKnownWindows8Title(titleId)) return "Windows8";
        var platform = string.Join(" / ", devices);
        return IsKnownGfwlTitle(titleId) || devices.Contains("GFWL", StringComparer.OrdinalIgnoreCase) ||
            devices.Contains("PC", StringComparer.OrdinalIgnoreCase) && devices.Contains("Xbox360", StringComparer.OrdinalIgnoreCase)
            ? "GFWL" : platform;
    }
    public async Task<bool> IsGfwlTitleAsync(Game game)
    {
        if (HasDevice(game, "GFWL") || IsKnownGfwlTitle(game.Id)) return true;
        if (!HasDevice(game, "Xbox360")) return false;
        if (gfwlTitleCache.TryGetValue(game.Id, out var cached)) return cached;
        if (!uint.TryParse(game.Id, out var numeric)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            await using var stream = await http.GetStreamAsync($"https://dbox.tools/api/title_ids/{numeric:X8}", lifetime.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: lifetime.Token);
            var isPc = document.RootElement.TryGetProperty("systems", out var systems) && systems.ValueKind == JsonValueKind.Array &&
                systems.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "PC");
            gfwlTitleCache[game.Id] = isPc;
            return isPc;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { return false; }
    }
    public void OpenGfwlTitle(Game game)
    {
        var classified = game with { Platform = "GFWL" };
        SelectedGfwlTitle = classified;
        Games = Games.Select(item => item.Id == classified.Id ? classified : item).ToArray();
        OpenGfwlWorkspace();
        Notice = $"{game.Name} is a Games for Windows LIVE title. Use the GFWL workflow with the PC game running.";
    }
    public async Task LookupTitleAsync()
    {
        if (!CanQuery) return;
        if (!uint.TryParse(TitleLookup.Trim(), out var id) || id == 0) { Notice = "Enter a numeric Xbox title ID."; return; }
        var key = id.ToString();
        var game = Games.FirstOrDefault(g => g.Id == key) ?? new Game(key, "Title " + key, "XboxOne", 0, 0, 0, false);
        if (await IsGfwlTitleAsync(game)) { OpenGfwlTitle(game); return; }
        await SelectGameAsync(game);
    }
    public async Task RefreshAchievementsAsync()
    {
        if (CanQuery && SelectedGame is { } game) await SelectGameAsync(game);
    }
    public async Task RefreshLibraryAsync()
    {
        if (!CanQuery) return;
        Busy(true);
        try
        {
            await requests.WaitAsync(lifetime.Token);
            try
            {
                if (client == null || session == null) return;
                var api = client; var xuid = session.Xuid;
                var result = await Task.Run(() => api.GetGamesListAsync(xuid), lifetime.Token) ?? throw new InvalidDataException();
                lifetime.Token.ThrowIfCancellationRequested();
                Games = result.Titles.Where(t => t.TitleId != null).Select(t => new Game(t.TitleId!, t.Name ?? t.TitleId!, ClassifyXboxPlatform(t.TitleId!, t.Devices), t.Achievement?.CurrentAchievements ?? 0, t.Achievement?.TotalAchievements ?? 0, t.Achievement?.CurrentGamerscore ?? 0, t.Achievement != null, ResolveTitleImage(t.DisplayImage, t.Images))).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                Notice = $"Refreshed {Games.Length} titles.";
            }
            finally { requests.Release(); }
        }
        catch (OperationCanceledException) { }
        catch { Notice = "Could not refresh the library. Your previous list is still available."; }
        finally { Busy(false); }
    }
    public async Task ExportCsvAsync(string path)
    {
        static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var rows = new List<string> { "Title ID,Title,Achievement ID,Achievement,Description,Status,Gamerscore" };
        foreach (var a in VisibleAchievements)
            rows.Add(string.Join(",", new[] { SelectedGame?.Id ?? "", SelectedGame?.Name ?? "", a.Id, a.Name, a.Description, a.Status, a.ScoreKnown ? a.Score.ToString() : "" }.Select(Quote)));
        await File.WriteAllLinesAsync(path, rows, new UTF8Encoding(true), lifetime.Token);
        Notice = $"Exported {rows.Count - 1} visible achievements.";
    }
}
