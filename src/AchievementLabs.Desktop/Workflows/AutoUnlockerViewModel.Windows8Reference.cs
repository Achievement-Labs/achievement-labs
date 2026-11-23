using System.Net;
using AchievementLabs.Core;
using AchievementLabs.Models;
using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop.Workflows;

public sealed record Windows8ReferenceImport(string Gamertag, string TitleId,
    IReadOnlyList<Xbox360ReferenceUnlock> Unlocks, IReadOnlyList<ExophaseAwardIdentity> AwardIdentities);

public partial class AutoUnlockerViewModel
{
    public Func<string, string, CancellationToken, Task<AchievementsResponse?>>? ReadWindows8ReferenceAsync { get; set; }
    public Func<string, CancellationToken, Task<AchievementsResponse?>>? ReadWindows8CatalogAsync { get; set; }
    public Func<Exophase360Client> CreateWindows8PublicClient { get; set; } = () => new();
    public Windows8ReferenceImport? ImportedWindows8Reference { get; set; }
    public bool Windows8ReferenceEditionConfirmed { get; set; }

    public async Task ImportWindows8ReferenceAsync(string path)
    {
        if (IsRunning) throw new InvalidOperationException("Pause Auto Unlock before importing reference dates.");
        if (!Windows8ReferenceEditionConfirmed)
            throw new InvalidOperationException("Confirm the saved achievement page is for this exact game, edition, and reference user.");
        if (!DesktopModel.IsKnownWindows8Title(TitleId) || string.IsNullOrWhiteSpace(ReferenceGamertag))
            throw new InvalidOperationException("Enter a Windows 8 title ID and reference gamertag first.");
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Reference files must be 2 MB or smaller.");
        var text = await File.ReadAllTextAsync(path);
        var unlocks = Xbox360TimestampPreview.Parse(text);
        ImportedWindows8Reference = new Windows8ReferenceImport(ReferenceGamertag.Trim(),
            DesktopModel.ResolveWindows8TitleId(TitleId), unlocks, Xbox360TimestampPreview.ParseAwardIdentities(text));
        StatusText = $"Imported {unlocks.Count} reference unlock dates. Build the queue to match them with Xbox's title catalog.";
    }

    private async Task<AchievementsResponse> ReadWindows8ReferenceForQueueAsync(string referenceXuid, string gamertag,
        string titleId, CancellationToken cancellation)
    {
        if (ImportedWindows8Reference != null)
            return await ReadImportedWindows8ReferenceAsync(titleId, cancellation);
        try
        {
            var read = ReadWindows8ReferenceAsync ?? ReadAchievementsForAccountAsync;
            return await (read?.Invoke(referenceXuid, titleId, cancellation)
                ?? throw new InvalidOperationException("Windows 8 reference reading is unavailable."))
                ?? throw new InvalidDataException("Xbox returned no Windows 8 reference achievements.");
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden)
        {
            try { return await ReadWindows8PublicReferenceAsync(gamertag, titleId, cancellation); }
            catch (Exception publicError) when (publicError is not OperationCanceledException)
            {
                throw new InvalidOperationException("Xbox denied this reference player's Windows 8 history (403), and public Exophase lookup failed: " +
                    publicError.Message + " Save the earned achievements page or JSON and use Import reference dates.", publicError);
            }
        }
    }

    private async Task<AchievementsResponse> ReadWindows8PublicReferenceAsync(string gamertag, string titleId,
        CancellationToken cancellation)
    {
        var title = XboxTitleCatalog.Find(titleId)
            ?? throw new InvalidDataException("The Windows 8 title is missing from the local title catalog. Import reference dates for this title.");
        StatusText = "Xbox denied reference history. Looking up public Exophase dates...";
        using var publicClient = CreateWindows8PublicClient();
        var games = await publicClient.FindWindows8GamesAsync(gamertag, true, cancellation);
        var matches = games.Where(game => TitleSearchText.Key(game.Name) == title.SearchKey).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(matches.Length == 0
                ? $"Exophase has no exact Windows 8 game match for {title.Name}."
                : $"Exophase has multiple Windows 8 game matches for {title.Name}.");
        var dates = await publicClient.LoadDatesAsync(gamertag, true, matches[0].Id, cancellation);
        var catalog = await ReadWindows8CatalogForReferenceAsync(titleId, cancellation);
        var result = Windows8ReferenceDates.Match(catalog, dates);
        ReferenceInfo += " · Exophase dates";
        return result;
    }

    private async Task<AchievementsResponse> ReadImportedWindows8ReferenceAsync(string titleId, CancellationToken cancellation)
    {
        var imported = ImportedWindows8Reference ?? throw new InvalidOperationException("Import a reference achievement file first.");
        if (!Windows8ReferenceEditionConfirmed || imported.Gamertag != ReferenceGamertag.Trim() || imported.TitleId != titleId)
            throw new InvalidOperationException("The imported dates belong to another reference player or title. Import the matching file again.");
        var catalog = await ReadWindows8CatalogForReferenceAsync(titleId, cancellation);
        return Windows8ReferenceDates.Match(catalog, imported.Unlocks, imported.AwardIdentities);
    }

    private async Task<AchievementsResponse> ReadWindows8CatalogForReferenceAsync(string titleId, CancellationToken cancellation) =>
        await (ReadWindows8CatalogAsync?.Invoke(titleId, cancellation)
            ?? ReadQueueAchievementsAsync?.Invoke(titleId, cancellation)
            ?? throw new InvalidOperationException("Windows 8 title catalog reading is unavailable."))
        ?? throw new InvalidDataException("Xbox returned no Windows 8 title catalog.");

    public async Task AcquireWindows8TokenAsync(string? receiptPath = null)
    {
#if WINDOWS
        var owner = account.XUIDOnly;
        if (IsRunning || !uint.TryParse(TitleId, out _) || !ulong.TryParse(owner, out _) ||
            !DesktopModel.IsKnownWindows8Title(TitleId))
        { StatusText = "Connect an Xbox account and enter a Windows 8 title ID first."; return; }
        IsConfigEnabled = false;
        try
        {
            StatusText = receiptPath == null ? "Getting Windows 8 license and token..." : "Getting token from selected pop.bin...";
            var title = DesktopModel.ResolveWindows8TitleId(TitleId);
            var held = receiptPath == null
                ? await Windows8LicenseClient.AcquireAsync(title, owner, CancellationToken.None)
                : await Windows8LicenseClient.AcquireFromReceiptAsync(title, owner, receiptPath, CancellationToken.None);
            if (account.XUIDOnly != owner || DesktopModel.ResolveWindows8TitleId(TitleId) != title)
                throw new InvalidOperationException("The account or Windows 8 title changed during token acquisition.");
            StatusText = $"Windows 8 token ready until {held.ExpiresAt.ToLocalTime():t}." +
                (held.SettingsMetadataChanged ? " Windows updated settings metadata; private before/after copies were retained." : "");
        }
        catch (Exception error) { StatusText = error.Message; }
        finally { IsConfigEnabled = true; }
#else
        StatusText = "Windows 8 token acquisition requires Windows.";
#endif
    }
}
