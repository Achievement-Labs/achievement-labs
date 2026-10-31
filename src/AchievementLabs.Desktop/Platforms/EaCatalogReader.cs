using Microsoft.Win32;
using System.Xml.Linq;

namespace AchievementLabs.Desktop.Platforms;

internal static class EaCatalogReader
{
    public static PlatformGameRow[] Read(CancellationToken token)
    {
        var games = new Dictionary<string, PlatformGameRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = OpenRoot(hive, view);
            if (root == null) continue;
            foreach (var path in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Origin Games", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var folder = OpenKey(root, path);
                if (folder == null) continue;
                foreach (var child in SubKeys(folder))
                {
                    token.ThrowIfCancellationRequested();
                    using var key = OpenKey(folder, child);
                    if (key == null) continue;
                    var publisher = Value(key, "Publisher");
                    if (path.EndsWith("Uninstall") && !publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) && !publisher.Equals("EA Games", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Value(key, "DisplayName", "Name");
                    if (name.Length == 0 || name.Contains("anti", StringComparison.OrdinalIgnoreCase) || name is "EA app" or "Origin") continue;
                    var install = Value(key, "InstallLocation", "Install Dir", "InstallDir", "InstallPath");
                    var offer = Value(key, "OfferId", "ContentId");
                    if (Directory.Exists(install) && File.Exists(Path.Combine(install, "__Installer", "installerdata.xml")))
                    {
                        try
                        {
                            using var reader = System.Xml.XmlReader.Create(Path.Combine(install, "__Installer", "installerdata.xml"), new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = 4_194_304 });
                            var xml = XDocument.Load(reader);
                            offer = xml.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("contentID", StringComparison.OrdinalIgnoreCase))?.Value ?? offer;
                        }
                        catch { /* Registry title remains available if an installer manifest is stale. */ }
                    }
                    // Keep identifiers from the launcher; never guess a service offer from a game name.
                    var id = offer.Length > 0 ? offer : child;
                    var item = new PlatformGameRow("EA", id, name, install, LaunchUri: offer.Length > 0 ? $"origin2://game/launch?offerIds={Uri.EscapeDataString(offer)}" : "", Source: "EA installation registry");
                    var duplicate = games.Values.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (duplicate != null)
                    {
                        if (duplicate.CanLaunch || !item.CanLaunch && Directory.Exists(duplicate.InstallPath)) continue;
                        games.Remove(duplicate.Id);
                    }
                    games[id] = item;
                }
            }
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string Value(RegistryKey key, params string[] names) => names.Select(n => ReadValue(key, n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static RegistryKey? OpenRoot(RegistryHive hive, RegistryView view) { try { return RegistryKey.OpenBaseKey(hive, view); } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static RegistryKey? OpenKey(RegistryKey parent, string name) { try { return parent.OpenSubKey(name); } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static string[] SubKeys(RegistryKey key) { try { return key.GetSubKeyNames(); } catch (System.Security.SecurityException) { return []; } catch (UnauthorizedAccessException) { return []; } }
    private static string? ReadValue(RegistryKey key, string name) { try { return key.GetValue(name) as string; } catch (System.Security.SecurityException) { return null; } catch (UnauthorizedAccessException) { return null; } }
}
