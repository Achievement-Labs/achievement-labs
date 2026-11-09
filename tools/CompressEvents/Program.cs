using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

if (args is ["--self-test"]) { BundleChecks.Run(); return; }
if (args is ["--verify", var assembly, var source])
{
    using var pe = new PEReader(File.OpenRead(assembly));
    var metadata = pe.GetMetadataReader();
    var resource = metadata.ManifestResources.Select(metadata.GetManifestResource)
        .Single(r => metadata.GetString(r.Name) == "AchievementLabs.LocalEvents.zip");
    if (!resource.Implementation.IsNil) throw new InvalidDataException("Event bundle must be embedded.");
    var reader = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress)
        .GetReader(checked((int)resource.Offset), checked(pe.PEHeaders.CorHeader.ResourcesDirectory.Size - (int)resource.Offset));
    using var stream = new MemoryStream(reader.ReadBytes(reader.ReadInt32()), writable: false);
    BundlePacker.Verify(stream, source);
    return;
}
if (args is [var directory, var output]) { BundlePacker.Pack(directory, output); return; }
throw new ArgumentException("Usage: CompressEvents <source> <output> | --verify <Core.dll> <source> | --self-test");

internal static class BundlePacker
{
    internal static SortedDictionary<string, string> Files(string source)
    {
        source = Path.GetFullPath(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (!relative.Contains("backup", StringComparison.OrdinalIgnoreCase)) files.Add(relative, file);
        }
        if (files.Count == 0) throw new InvalidDataException("Cannot bundle an empty event source.");
        return files;
    }

    internal static void Pack(string source, string output)
    {
        var files = Files(source);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (var (name, path) in files)
                {
                    // Brotli beats the outer single-file Deflate compression on these repetitive JSON files.
                    // ZIP supplies a standard directory; store the already-compressed entries without Deflate.
                    var entry = archive.CreateEntry(name + ".br", CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var input = File.OpenRead(path);
                    using var target = entry.Open();
                    using var compressed = new BrotliStream(target, CompressionLevel.Optimal);
                    input.CopyTo(compressed);
                }
            // Content, file additions/deletions and source-directory changes all matter.
            // Keep the output timestamp stable only when the entire deterministic bundle matches.
            bool unchanged;
            using (var staged = File.OpenRead(temporary))
            using (var existing = File.Exists(output) ? File.OpenRead(output) : null)
                unchanged = existing != null && staged.Length == existing.Length && SHA256.HashData(staged).SequenceEqual(SHA256.HashData(existing));
            if (!unchanged) File.Move(temporary, output, overwrite: true);
            Console.WriteLine($"Event bundle: {files.Count} files, {new FileInfo(output).Length:N0} bytes ({(unchanged ? "unchanged" : "updated")}).");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Verify(Stream bundle, string source)
    {
        var files = Files(source);
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != files.Count) throw new InvalidDataException("Event bundle file count differs from source.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".br", StringComparison.Ordinal) || !names.Add(entry.FullName)
                || !files.TryGetValue(entry.FullName[..^3], out var path))
                throw new InvalidDataException("Unexpected or duplicate bundled event: " + entry.FullName);
            using var expected = File.OpenRead(path);
            using var stored = entry.Open();
            using var actual = new BrotliStream(stored, CompressionMode.Decompress);
            if (!SHA256.HashData(actual).SequenceEqual(SHA256.HashData(expected)))
                throw new InvalidDataException("Bundled event differs from source: " + entry.FullName);
        }
        Console.WriteLine($"Verified {files.Count} embedded event files byte-for-byte against source.");
    }
}
