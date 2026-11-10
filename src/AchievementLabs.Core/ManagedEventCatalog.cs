using System.Security.Cryptography;
using System.Text;

namespace AchievementLabs.Core;

/// <summary>Refreshes only the app-managed catalog; custom catalog paths remain user-owned.</summary>
public static class ManagedEventCatalog
{
    public static bool EnsureCurrent(string directory)
    {
        var managed = Path.GetFullPath(new DesktopPreferences().EventsDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).Equals(managed.TrimEnd(Path.DirectorySeparatorChar), comparison)) return false;
        using var bundle = typeof(BundledEventFiles).Assembly.GetManifestResourceStream("AchievementLabs.LocalEvents.zip")
            ?? throw new InvalidDataException("Bundled event catalog is missing.");
        var digest = Convert.ToHexString(SHA256.HashData(bundle));
        return Install(managed, digest, BundledEventFiles.Read);
    }

    // The separate receipt never becomes a runtime catalog property.
    internal static bool Install(string directory, string digest, Func<string, string?> read)
    {
        directory = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);
        using var gate = new FileStream(directory + ".update.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var receipt = directory + ".bundle.sha256";
        if (File.Exists(receipt) && File.ReadAllText(receipt) == digest && File.Exists(Path.Combine(directory, "index.json"))) return false;
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The managed Events folder must not be a link. Choose a custom Events path for linked catalogs.");
        var token = Guid.NewGuid().ToString("N");
        var stage = directory + ".staging-" + token;
        var backup = directory + ".backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + token;
        var pendingReceipt = receipt + "." + token;
        var moved = false;
        try
        {
            Directory.CreateDirectory(stage);
            void Write(string name)
            {
                var text = read(name) ?? throw new InvalidDataException("Missing bundled catalog file: " + name);
                var path = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
            }
            Write("index.json");
            var index = new EventCatalogStorage(stage).Index;
            foreach (var id in index.Properties().Where(p => EventCatalogStorage.ValidId(p.Name)).Select(p => p.Name))
            {
                Write(id + "/Data.json");
                Write(id + "/" + id + ".json");
            }
            Write("Stats.json");
            var bundledStats = File.ReadAllText(Path.Combine(stage, "Stats.json"));
            // Validate every installed title with the actual consumer, including index/summary agreement.
            var storage = new EventCatalogStorage(stage);
            foreach (var id in index.Properties().Where(p => EventCatalogStorage.ValidId(p.Name)).Select(p => p.Name))
                storage.ReadTitleAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            // Local stats are user-owned. Preserve both standalone and legacy embedded stat recipes.
            var legacy = Path.Combine(directory, "Data.json");
            var stats = Path.Combine(directory, "Stats.json");
            if (File.Exists(stats)) File.Copy(stats, Path.Combine(stage, "Stats.json"), true);
            if (File.Exists(legacy))
            {
                if (!File.Exists(stats)) File.WriteAllText(Path.Combine(stage, "Stats.json"), "{}");
                File.Copy(legacy, Path.Combine(stage, "Data.json"));
                StatCatalogStore.MigrateLegacy(stage);
                File.Delete(Path.Combine(stage, "Data.json"));
                foreach (var file in Directory.EnumerateFiles(stage, "*.bak")) File.Delete(file);
            }
            var mergedStats = EventCatalogStorage.Parse(bundledStats);
            mergedStats.Merge(EventCatalogStorage.Parse(File.ReadAllText(Path.Combine(stage, "Stats.json"))),
                new Newtonsoft.Json.Linq.JsonMergeSettings { MergeArrayHandling = Newtonsoft.Json.Linq.MergeArrayHandling.Replace });
            StatCatalogStore.Replace(Path.Combine(stage, "Stats.json"), mergedStats);
            File.WriteAllText(pendingReceipt, digest);
            if (Directory.Exists(directory)) { Directory.Move(directory, backup); moved = true; }
            try { Directory.Move(stage, directory); }
            catch { if (moved) Directory.Move(backup, directory); throw; }
            File.Move(pendingReceipt, receipt, true);
            return true;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (File.Exists(pendingReceipt)) File.Delete(pendingReceipt);
        }
    }
}
