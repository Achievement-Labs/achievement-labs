using AchievementLabs.Core;
using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private string xbox360ExophaseName = "", xbox360ExophaseKind = "Xbox Live";
    private string? xbox360ExophaseLookupKey;
    private Exophase360Game[] xbox360ExophaseGames = [];
    private Exophase360Game? selectedXbox360ExophaseGame;
    public string[] Xbox360ExophaseKinds { get; } = ["Xbox Live", "Exophase profile", "Xbox gamertag (Exophase)"];
    public string Xbox360ExophaseName { get => xbox360ExophaseName; set { xbox360ExophaseName = value ?? ""; ClearXbox360ExophaseLookup(); Changed(); } }
    public string Xbox360ExophaseKind { get => xbox360ExophaseKind; set { xbox360ExophaseKind = value; ClearXbox360ExophaseLookup(); Changed(); } }
    public Exophase360Game[] Xbox360ExophaseGames { get => xbox360ExophaseGames; private set { xbox360ExophaseGames = value; Changed(); Changed(nameof(HasXbox360ExophaseGames)); } }
    public bool HasXbox360ExophaseGames => Xbox360ExophaseGames.Length != 0;
    public Exophase360Game? SelectedXbox360ExophaseGame { get => selectedXbox360ExophaseGame; set { selectedXbox360ExophaseGame = value; ResetXbox360Preview(); Changed(); } }
    private void ClearXbox360ExophaseLookup() { xbox360ExophaseLookupKey = null; Xbox360ExophaseGames = []; SelectedXbox360ExophaseGame = null; }
    // Handler injection keeps tests independent from real accounts and website availability.
    public Func<Exophase360Client> Xbox360ExophaseClientFactory { get; set; } = () => new();
    public Func<string,string,CancellationToken,Task<Xbox360AchievementResponse?>>? Xbox360ReferenceFetcher { get; set; }
    private async Task<Xbox360AchievementResponse?> ReadXbox360ReferenceAsync(string gamertag,string hexTitleId,CancellationToken cancellation)
    {
        if (gamertag.Length is < 1 or > 64 || gamertag.Any(char.IsControl)) throw new InvalidDataException("Enter a reference gamertag.");
        var numeric = Convert.ToUInt32(hexTitleId,16).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Xbox360ReferenceFetcher != null) return await Xbox360ReferenceFetcher(gamertag,numeric,cancellation);
        await EnsureAuthorizationFreshAsync(cancellation);
        var connected = session ?? throw new InvalidOperationException("Attach your Xbox account to load reference dates.");
        var generation = sessionGeneration;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var api = new XboxApiClient(connected.Authorization,RegionOverride);
        var profile = await api.GetGamertagProfileAsync(Uri.EscapeDataString(gamertag)).WaitAsync(timeout.Token);
        var xuid = profile?["profileUsers"]?.FirstOrDefault()?["id"]?.ToString();
        if (string.IsNullOrEmpty(xuid) || xuid.Length>20 || !xuid.All(char.IsDigit)) throw new InvalidDataException("Xbox could not resolve the reference gamertag.");
        var response = await api.GetAchievementsFor360TitleAsync(xuid,numeric).WaitAsync(timeout.Token);
        if (generation != sessionGeneration || session?.Xuid != connected.Xuid) throw new InvalidOperationException("The connected account changed. Load dates again.");
        return response;
    }
    public async Task LoadXbox360ExophaseDatesAsync()
    {
        if (busy) return;
        Busy(true);
        try
        {
            var fingerprint = ValidateXbox360PreviewProfile();
            var path = loadedXbox360Profile; var title = Xbox360EditorTitleId;
            var selected = Xbox360Choices.Where(c => c.Selected).Select(c => c.Achievement.Id).ToHashSet();
            if (selected.Count == 0) throw new InvalidOperationException("Select achievements first.");
            var name = Xbox360ExophaseName.Trim(); var kind = Xbox360ExophaseKind;
            var key = name + "|" + kind + "|" + title;
            ResetXbox360Preview();
            if (kind == "Xbox Live")
            {
                Xbox360PreviewStatus = "Loading reference dates from Xbox...";
                var response = await ReadXbox360ReferenceAsync(name,title,lifetime.Token);
                if (path != loadedXbox360Profile || title != Xbox360EditorTitleId || name != Xbox360ExophaseName.Trim() || kind != Xbox360ExophaseKind ||
                    !selected.SetEquals(Xbox360Choices.Where(c => c.Selected).Select(c => c.Achievement.Id)) || fingerprint != ValidateXbox360PreviewProfile())
                    throw new IOException("The profile or selection changed. Load dates again.");
                var local = ProfileReader.ReadAchievements(path,title);
                if (!local.SequenceEqual(Xbox360Achievements)) throw new IOException("The profile changed. Reload it.");
                Xbox360PreviewRows = Xbox360TimestampPreview.MatchXbox(local,response?.achievements ?? [],selected,Convert.ToUInt32(title,16)).ToArray();
                xbox360PreviewFingerprint = fingerprint; Xbox360QuickUnlockMode = "Imported dates";
                Xbox360PreviewStatus = $"{Xbox360PreviewRows.Count(r => r.Result == "Ready for review")} dates matched from Xbox. Click Unlock selected to apply.";
                return;
            }
            Xbox360PreviewStatus = "Looking up Exophase...";
            using var api = Xbox360ExophaseClientFactory();
            if (key != xbox360ExophaseLookupKey)
            {
                var games = await api.FindGamesAsync(name,kind == "Xbox gamertag (Exophase)",lifetime.Token);
                var exact = games.Where(g => TitleSearchText.Key(g.Name) == TitleSearchText.Key(Xbox360EditorTitleName)).ToArray();
                Xbox360ExophaseGames = exact.Length > 0 ? exact : games.ToArray();
                SelectedXbox360ExophaseGame = exact.Length == 1 ? exact[0] : null;
                xbox360ExophaseLookupKey = key;
                if (SelectedXbox360ExophaseGame == null)
                { Xbox360PreviewStatus = games.Count == 0 ? "No Xbox 360 games found on this profile." : "Choose the matching Xbox 360 game, then load dates."; return; }
            }
            var game = SelectedXbox360ExophaseGame ?? throw new InvalidOperationException("Choose the matching game first.");
            if (!Xbox360ExophaseGames.Contains(game)) throw new InvalidOperationException("Look up the profile again.");
            var reference = await api.LoadDatesAsync(name,kind == "Xbox gamertag (Exophase)",game.Id,lifetime.Token);
            if (path != loadedXbox360Profile || title != Xbox360EditorTitleId || name != Xbox360ExophaseName.Trim() || kind != Xbox360ExophaseKind ||
                !selected.SetEquals(Xbox360Choices.Where(c => c.Selected).Select(c => c.Achievement.Id)) || fingerprint != ValidateXbox360PreviewProfile())
                throw new IOException("The profile or selection changed. Load the reference dates again.");
            var originals = ProfileReader.ReadAchievements(path,title);
            if (!originals.SequenceEqual(Xbox360Achievements)) throw new IOException("The achievement data changed. Reload the profile.");
            Xbox360PreviewRows = Xbox360TimestampPreview.Match(originals,reference,selected).ToArray();
            xbox360PreviewFingerprint = fingerprint;
            Xbox360QuickUnlockMode = "Imported dates";
            Xbox360PreviewStatus = $"{Xbox360PreviewRows.Count(r => r.Result == "Ready for review")} dates matched · {game.Name}. Click Unlock selected to apply.";
        }
        catch (OperationCanceledException) { ResetXbox360Preview(); Xbox360PreviewStatus = "Reference lookup timed out or was cancelled."; }
        catch (Exception error) { ResetXbox360Preview(); Xbox360PreviewStatus = "Lookup stopped: " + error.Message; }
        finally { Busy(false); }
    }
}
