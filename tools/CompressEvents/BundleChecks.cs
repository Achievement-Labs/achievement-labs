using System.IO.Compression;

internal static class BundleChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "labs-event-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
            var output = Path.Combine(root, "events.zip");
            var file = Path.Combine(source, "index.json");
            File.WriteAllText(file, "original"); File.WriteAllText(Path.Combine(source, "ignore-backup.json"), "backup");
            BundlePacker.Pack(source, output);
            using (var packed = ZipFile.OpenRead(output))
                Check(packed.Entries.Count == 1 && packed.GetEntry("index.json.br") != null, "Backup file included.");
            var stamp = File.GetLastWriteTimeUtc(output); var bytes = File.ReadAllBytes(output);
            BundlePacker.Pack(source, output);
            Check(stamp == File.GetLastWriteTimeUtc(output) && bytes.SequenceEqual(File.ReadAllBytes(output)), "Unchanged output rewritten.");
            File.WriteAllText(file, "modified"); File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch);
            BundlePacker.Pack(source, output); Verify(output, source);
            Check(!bytes.SequenceEqual(File.ReadAllBytes(output)), "Old-timestamp edit missed.");
            var added = Path.Combine(source, "new.json"); File.WriteAllText(added, "new");
            BundlePacker.Pack(source, output); Verify(output, source);
            File.Delete(added); BundlePacker.Pack(source, output); Verify(output, source);
            File.WriteAllText(Path.Combine(other, "index.json"), "other");
            File.SetLastWriteTimeUtc(Path.Combine(other, "index.json"), DateTime.UnixEpoch);
            BundlePacker.Pack(other, output); Verify(output, other);
            try { Verify(output, source); throw new Exception("Stale source accepted."); }
            catch (InvalidDataException) { }
            // A truncated archive must fail verification.
            File.WriteAllBytes(output, File.ReadAllBytes(output)[..10]);
            try { Verify(output, other); throw new Exception("Corrupt bundle accepted."); }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS: deterministic compression, edits, additions, deletions, source switches, backup exclusion and verification rejection.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Verify(string bundle, string source) { using var input = File.OpenRead(bundle); BundlePacker.Verify(input, source); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
