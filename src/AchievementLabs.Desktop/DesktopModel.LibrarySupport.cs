using System.Text.Json;
using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly SharedUnlockMethods sharedUnlockMethods = SharedUnlockMethods.Bundled();
    private readonly SemaphoreSlim sharedMetadataRefresh = new(1, 1);
    private Dictionary<string, bool>? libraryUnlockMethods;
    private Dictionary<string, LibraryEventSupport>? libraryEventSupport;
    private string supportMetadataStatus = "Support labels use catalog metadata and previously confirmed results. Unknown types remain unclassified.";
    private Dictionary<string, bool> LibraryUnlockMethods
    {
        get
        {
            if (libraryUnlockMethods != null) return libraryUnlockMethods;
            libraryUnlockMethods = new();
            if (!OfflineChecks)
                try
                {
                    var path = AchievementLabsPaths.LocalFile("library-unlock-methods.json");
                    if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024)
                        libraryUnlockMethods = SharedUnlockMethods.Parse("{\"schemaVersion\":1,\"methods\":" + File.ReadAllText(path) + "}");
                }
                catch { }
            return libraryUnlockMethods;
        }
    }
    public string SupportMetadataStatus { get => supportMetadataStatus; private set { supportMetadataStatus=value; Changed(); } }
    public async Task ExportConfirmedSupportAsync(string path)
    {
        await File.WriteAllTextAsync(path, SharedUnlockMethods.Export(LibraryUnlockMethods), lifetime.Token);
        Notice = $"Exported {LibraryUnlockMethods.Count} confirmed unlock methods.";
    }
    private LibrarySupportBadge SupportFor(Game game)
    {
        if (UsesLegacyEndpoint(game)) return LibrarySupportBadge.Resolve(true, null, false, false, false, 0, game.Total);
        try
        {
            libraryEventSupport ??= eventCatalog.GetLibrarySupport();
            libraryEventSupport.TryGetValue(game.Id, out var entry);
            bool? method = LibraryUnlockMethods.TryGetValue(game.Id, out var local) ? local : entry != null ? null : sharedUnlockMethods.Find(game.Id);
            var badge = LibrarySupportBadge.Resolve(false, method, entry != null, entry?.Available == true, entry?.FullySupported == true, entry?.Mapped ?? 0, game.Total, entry?.PartiallySupported == true);
            return entry?.Note is { } note ? badge with { Detail = badge.Detail + " " + note } : badge;
        }
        catch { return LibrarySupportBadge.Unknown; }
    }
    private void CacheLibraryUnlockMethod(string title, bool events)
    {
        LibraryUnlockMethods[title] = events;
        if (OfflineChecks) return;
        try
        {
            var path = AchievementLabsPaths.LocalFile("library-unlock-methods.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(LibraryUnlockMethods));
            File.Move(path + ".tmp", path, true);
        }
        catch { SupportMetadataStatus = "Support results updated, but their local cache could not be saved."; }
    }
    private void RememberLibraryUnlockMethod(string title, bool events) { CacheLibraryUnlockMethod(title, events); RefreshLibraryPresentation(); }
    private void RefreshLibrarySupport() { libraryEventSupport = null; RefreshLibraryPresentation(); }
}
