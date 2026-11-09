using System.IO.Compression;

namespace AchievementLabs.Core;

/// <summary>Shared reader for compressed and legacy bundled event/stat files. Never writes local catalogs.</summary>
public static class BundledEventFiles
{
    // ZIP indexes independently Brotli-compressed JSON files. Synchronize its shared seekable resource stream.
    private static readonly Lazy<ZipArchive?> Bundle = new(() =>
    {
        var stream = typeof(BundledEventFiles).Assembly.GetManifestResourceStream("AchievementLabs.LocalEvents.zip");
        return stream == null ? null : new ZipArchive(stream, ZipArchiveMode.Read);
    });
    private static string ResourceName(string path) => "AchievementLabs.LocalEvents." + path.Replace('/', '.').Replace('\\', '.');
    private static string? FindResource(string path) => typeof(BundledEventFiles).Assembly.GetManifestResourceNames()
        .FirstOrDefault(name => name.Replace('/', '.').Replace('\\', '.') == ResourceName(path));
    public static bool Contains(string path)
    {
        if (Bundle.Value is { } bundle)
            lock (bundle) if (bundle.GetEntry(path.Replace('\\', '/') + ".br") != null) return true;
        return FindResource(path) != null;
    }
    public static string? Read(string path)
    {
        if (Bundle.Value is { } bundle)
            lock (bundle)
                if (bundle.GetEntry(path.Replace('\\', '/') + ".br") is { } entry)
                {
                    using var content = entry.Open();
                    using var decompressed = new BrotliStream(content, CompressionMode.Decompress);
                    using var text = new StreamReader(decompressed);
                    return text.ReadToEnd();
                }
        var name = FindResource(path);
        if (name == null) return null;
        using var stream = typeof(BundledEventFiles).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
