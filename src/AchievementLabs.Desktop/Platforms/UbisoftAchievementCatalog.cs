using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AchievementLabs.Core;

namespace AchievementLabs.Desktop.Platforms;

internal static class UbisoftAchievementCatalog
{
    internal static async Task<PlatformAchievementRow[]> ReadAsync(PlatformGameRow game, CancellationToken ct)
    {
        if (!ulong.TryParse(game.Id, out _)) return [];
        var directories = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ubisoft", "Ubisoft Game Launcher", "cache", "achievements"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher", "cache", "achievements") };
        string? bundle = null;
        foreach (var directory in directories.Where(Directory.Exists))
        {
            bundle = Directory.EnumerateFiles(directory, game.Id + "_*").Where(p => Regex.IsMatch(Path.GetFileName(p), @"^\d+_[a-fA-F0-9]{32}$")).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (bundle != null) break;
        }
        byte[] bytes;
        if (bundle != null)
        {
            if (new FileInfo(bundle).Length > 16 * 1024 * 1024) throw new InvalidDataException("Ubisoft achievement bundle was too large.");
            bytes = await File.ReadAllBytesAsync(bundle, ct);
        }
        else
        {
            if (!Regex.IsMatch(game.AchievementCatalogKey, @"^\d+_[a-fA-F0-9]{32}$")) return [];
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
            bytes = await http.GetByteArrayAsync("https://ubistatic3-a.akamaihd.net/orbit/uplay_launcher_3_0/achievements/" + game.AchievementCatalogKey, ct);
        }
        var art = AchievementLabsPaths.LocalFile(Path.Combine("achievement-art", "Ubisoft", game.Id, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
        return ParseBundle(bytes, art, ct);
    }
    internal static PlatformAchievementRow[] ParseBundle(byte[] bytes, string imageDirectory, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, false); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 5000 || zip.Entries.Sum(e => e.Length) > 32 * 1024 * 1024) throw new InvalidDataException("Ubisoft achievement bundle exceeded its supported size.");
        var localization = zip.GetEntry("en-US_loc.txt") ?? zip.GetEntry("en-GB_loc.txt") ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith("_loc.txt", StringComparison.Ordinal));
        var definitions = zip.GetEntry("achievements.dat");
        if (localization == null || definitions == null) throw new InvalidDataException("Ubisoft achievement bundle was incomplete.");
        var names = new Dictionary<string, (string Name, string Description)>();
        using (var reader = new StreamReader(localization.Open(), Encoding.UTF8, true))
        {
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t', 3);
                if (parts.Length >= 2 && ulong.TryParse(parts[0], out _)) names[parts[0]] = (parts[1], parts.Length == 3 ? parts[2] : "");
            }
        }
        using var data = new MemoryStream(); using (var input = definitions.Open()) input.CopyTo(data);
        var result = new List<PlatformAchievementRow>();
        foreach (var item in Proto.Fields(data.ToArray()).Where(f => f.Tag == 1))
        {
            ct.ThrowIfCancellationRequested();
            var id = Proto.Get(item.Bytes, 1).Number.ToString();
            var nameKey = Proto.Get(item.Bytes, 2).Number.ToString(); var descriptionKey = Proto.Get(item.Bytes, 3).Number.ToString();
            var icon = Proto.Get(item.Bytes, 4).Number.ToString() + ".png";
            var url = "";
            if (zip.GetEntry(icon) is { Length: > 0 and < 2 * 1024 * 1024 } image)
            {
                Directory.CreateDirectory(imageDirectory);
                var path = Path.Combine(imageDirectory, icon);
                if (!File.Exists(path))
                {
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { using (var file = new FileStream(temporary, FileMode.CreateNew)) using (var input = image.Open()) input.CopyTo(file); File.Move(temporary, path, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                url = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            }
            result.Add(new(id, names.TryGetValue(nameKey, out var title) ? title.Name : "Achievement " + id, names.TryGetValue(descriptionKey, out var description) ? description.Description : "", ImageUrl: url, Source: "Ubisoft achievement catalog"));
        }
        return result.GroupBy(r => r.Id).Select(g => g.First()).ToArray();
    }
}
