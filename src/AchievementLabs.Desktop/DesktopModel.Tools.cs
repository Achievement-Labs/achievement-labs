using Newtonsoft.Json.Linq;
using System.ComponentModel;
using AchievementLabs.Core;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
namespace AchievementLabs.Desktop;
public sealed record TitleSearchResult(string TitleId, string Name, string Platform, string? ImageUrl, bool? HasDboxAchievements = null, bool? HasXboxAchievements = null) : INotifyPropertyChanged
{
    public string Monogram => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0])));
    public bool CanAddXbox360Gpd => Platform.Contains("Xbox 360", StringComparison.OrdinalIgnoreCase);
    public int? CatalogAchievementCount { get; init; }
    public string? AchievementType { get; init; }
    public string? StatType { get; init; }
    private LibrarySupportBadge support = LibrarySupportBadge.Unknown;
    public LibrarySupportBadge Support { get => support; init => support = value; }
    public void RefreshSupport(LibrarySupportBadge value)
    {
        support = value;
        PropertyChanged?.Invoke(this, new(nameof(Support)));
        SetSupportDetails("Catalogue updated. Load mapping coverage to inspect the current entries.");
    }
    public string DefinitionPlatform { get; init; } = "";
    private string supportDetails = "Load the current mapping list to see which achievements are covered.";
    private bool loadingSupportDetails;
    public string SupportDetails => supportDetails;
    public bool CanLoadSupportDetails => !loadingSupportDetails;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetSupportDetails(string text, bool loading = false)
    {
        supportDetails = text;
        loadingSupportDetails = loading;
        PropertyChanged?.Invoke(this, new(nameof(SupportDetails)));
        PropertyChanged?.Invoke(this, new(nameof(CanLoadSupportDetails)));
    }
    private HashSet<string>? platformKeys;
    public HashSet<string> PlatformKeys=>platformKeys??=AchievementLabs.Core.TitleSearchPlatforms.Classify(Platform.Split([',','/'],StringSplitOptions.TrimEntries));
    public bool HasVerifiedAchievements => CatalogAchievementCount>0 || HasDboxAchievements == true || HasXboxAchievements == true;
    public string AchievementCatalogStatus => (CatalogAchievementCount is {} count ? $"Catalog: {count} achievements" : "Definition total unknown")
        + " | Achievements: " + (AchievementType??"Unknown") + " | Stats: " + (StatType??"Unknown");
}
public sealed partial class DesktopModel
{
    private string titleSearch = "", gamertagSearch = "", lookupOutput = "", lookupXuid = "";
    private TitleSearchResult[] titleSearchResults = [];
    private TitleSearchResult[] allTitleSearchResults = [];
    private bool onlyVerifiedAchievementTitles = false;
    private TitlePlatformFilter[]? titleSearchPlatforms;
    private string titleSearchWarning = "";
    public TitlePlatformFilter[] TitleSearchPlatforms => titleSearchPlatforms ??= AchievementLabs.Core.TitleSearchPlatforms.Choices
        .Select(x => new TitlePlatformFilter(x.Key, x.Label, ApplyTitleSearchFilters)).ToArray();
    public void SelectAllTitleSearchPlatforms(bool selected)
    {
        foreach (var platform in TitleSearchPlatforms) platform.Selected = selected;
        ApplyTitleSearchFilters();
    }
    private void ApplyTitleSearchFilters()
    {
        var selected = TitleSearchPlatforms.Where(x => x.Selected).Select(x => x.Key).ToHashSet();
        TitleSearchResults = allTitleSearchResults.Where(item => (!OnlyVerifiedAchievementTitles || item.HasVerifiedAchievements) &&
            item.PlatformKeys.Overlaps(selected)).Take(300).ToArray();
        if (allTitleSearchResults.Length > 0)
            LookupOutput = $"Showing {TitleSearchResults.Length} of {allTitleSearchResults.Length} titles; {allTitleSearchResults.Count(item => !item.HasVerifiedAchievements)} have no total in the snapshot." +
                (selected.Count == 0 ? " Select at least one platform." : "") + titleSearchWarning;
    }
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
            ApplyTitleSearchFilters();
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
        if(busy || string.IsNullOrWhiteSpace(TitleSearch))return;
        Busy(true);
        try
        {
            var query=NormalizeLookalikes(TitleSearch.Trim());
            var entries=await Task.Run(()=>AchievementLabs.Core.XboxTitleCatalog.SearchEntries(query),lifetime.Token);
            var artwork=Games.GroupBy(g=>g.Id).ToDictionary(g=>g.Key,g=>g.First().ImageUrl);
            allTitleSearchResults=entries.Select(t=>new TitleSearchResult(t.TitleId,t.Name,string.Join(", ",t.Platforms.Select(FormatXboxPlatform)),artwork.GetValueOrDefault(t.TitleId) ?? (t.ModernTitleId is { } alias ? artwork.GetValueOrDefault(alias) : null))
                {CatalogAchievementCount=t.TotalAchievements,AchievementType=t.AchievementsType??(t.SourceVersion==1 ? "Legacy" : null),StatType=t.StatsType,
                    DefinitionPlatform=string.Join(" / ",t.Devices.Length>0?t.Devices:t.Platforms),
                    Support=SupportFor(new Game(t.TitleId,t.Name,string.Join(" / ",t.Devices.Length>0?t.Devices:t.Platforms),0,t.TotalAchievements??0,0,false))}).ToArray();
            titleSearchWarning=" Local database snapshot; no Xbox or DBox verification requests. Up to 300 filtered results are shown.";
            ApplyTitleSearchFilters();
            if(allTitleSearchResults.Length==0)LookupOutput="No matching titles in the local database snapshot.";
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { LookupOutput="Title search failed: "+error.Message; }
        finally {Busy(false);}
    }
    private void RefreshTitleSearchSupport()
    {
        if (allTitleSearchResults.Length == 0) return;
        foreach (var title in allTitleSearchResults)
            title.RefreshSupport(SupportFor(new Game(title.TitleId,title.Name,title.DefinitionPlatform,0,title.CatalogAchievementCount??0,0,false)));
        ApplyTitleSearchFilters();
    }
    public async Task LoadTitleSupportDetailsAsync(TitleSearchResult title)
    {
        if (!title.CanLoadSupportDetails) return;
        title.SetSupportDetails("Reading the current event catalogue…", true);
        try
        {
            var support = eventCatalog.GetLibrarySupport().GetValueOrDefault(title.TitleId);
            if (support == null || !support.Available)
            {
                title.SetSupportDetails(title.Support.Detail);
                return;
            }
            var mapping = await eventCatalog.GetTitleAsync(title.TitleId, lifetime.Token);
            var mapped = mapping.AchievementIds.ToHashSet(StringComparer.Ordinal);
            var catalogue = AchievementLabs.MultiSelect.SharedDlcCatalogue.Current.Find(title.TitleId, title.DefinitionPlatform);
            var definitions = catalogue?.Packs.SelectMany(pack => pack.Achievements).DistinctBy(a => a.Id).ToArray() ?? [];
            bool incompleteNames = definitions.Length == 0 || title.CatalogAchievementCount > definitions.Length;
            string definitionNotice = "";
            if (incompleteNames && session != null && !OfflineChecks && !QueueActive && !busy)
            {
                Busy(true);
                try
                {
                    await EnsureAuthorizationFreshAsync(lifetime.Token);
                    await requests.WaitAsync(lifetime.Token);
                    try
                    {
                        var response = await ReadQueueAchievementsAsync(title.TitleId, lifetime.Token);
                        var live = response?.achievements.Where(a => !string.IsNullOrWhiteSpace(a.id))
                            .Select(a => new AchievementLabs.MultiSelect.SharedDlcCatalogue.Definition(a.id, a.name ?? a.id))
                            .DistinctBy(a => a.Id).ToArray();
                        if (live is { Length: > 0 } && live.Length >= definitions.Length) definitions = live;
                    }
                    finally { requests.Release(); }
                }
                catch (OperationCanceledException) { throw; }
                catch { definitionNotice = "Xbox definitions are unavailable. The list below uses the local named catalogue."; }
                finally { Busy(false); }
            }
            if (definitions.Length == 0)
            {
                title.SetSupportDetails($"{mapped.Count} mapped achievement IDs. A complete named achievement list is unavailable in the local catalogue. Open achievements to inspect this game's live list.");
                return;
            }
            var missing = definitions.Where(a => !mapped.Contains(a.Id)).ToArray();
            var lines = new List<string> {
                $"{definitions.Count(a => mapped.Contains(a.Id))} / {definitions.Length} named achievements mapped in the current catalogue.",
                title.Support.Detail
            };
            if (definitionNotice.Length > 0) lines.Add(definitionNotice);
            lines.Add(missing.Length == 0 ? "All named achievements have mappings." : $"Missing mappings ({missing.Length})");
            lines.AddRange(missing.Select(a => $"• {a.Name} · ID {a.Id}"));
            if (title.CatalogAchievementCount > definitions.Length)
                lines.Add($"{title.CatalogAchievementCount - definitions.Length} additional definitions are missing from the local named list. Connect Xbox and load coverage again to identify them.");
            title.SetSupportDetails(string.Join(Environment.NewLine, lines));
        }
        catch (OperationCanceledException) { title.SetSupportDetails("Mapping lookup cancelled."); }
        catch { title.SetSupportDetails("Could not read mapping coverage. Check the event catalogue status in Settings → Sync."); }
    }

    internal static string NormalizeLookalikes(string value)=>AchievementLabs.Core.TitleSearchText.NormalizeLookalikes(value);
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
        "WINDOWS8" or "WIN8" => "Windows 8",
        "GFWL" => "GFWL",
        "WIN32" => "Win32",
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
            var target = destination switch { "Discord" => "https://discord.gg/EY6AJpNfVu", "GitHub" => "https://github.com/Achievement-Labs/achievement-labs/", "Original XAU" => "https://github.com/Fumo-Unlockers/Xbox-Achievement-Unlocker", "Steam converter" => steam.GetConverterPath(), _ => throw new InvalidOperationException() };
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { Notice = "Could not open the requested link or workspace."; }
    }
}
