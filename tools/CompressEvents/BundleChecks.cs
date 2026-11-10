using System.IO.Compression;
using System.Text.Json;

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
            void Index(string folder, params string[] ids)
            {
                var index = new Dictionary<string, object>();
                foreach (var id in ids)
                {
                    index[id] = new { Enabled = true };
                    Directory.CreateDirectory(Path.Combine(folder, id));
                    File.WriteAllText(Path.Combine(folder,id,"Data.json"),"{}");
                    File.WriteAllText(Path.Combine(folder,id,id+".json"),"template " + id);
                }
                index["SupportedTitleIDs"] = ids;
                File.WriteAllText(Path.Combine(folder,"index.json"),JsonSerializer.Serialize(index));
            }
            Index(source,"7");
            foreach (var unused in new[]{"ignore-backup.json","XboxTitleIDs.json","research.json"}) File.WriteAllText(Path.Combine(source,unused),"not runtime");
            File.WriteAllText(Path.Combine(source,"7","notes.json"),"not runtime");
            BundlePacker.Pack(source, output);
            using (var packed = ZipFile.OpenRead(output))
                Check(packed.Entries.Count == 3 && packed.GetEntry("7/7.json.br") != null, "Only runtime catalog files should be included.");
            var stamp = File.GetLastWriteTimeUtc(output); var bytes = File.ReadAllBytes(output);
            BundlePacker.Pack(source, output);
            Check(stamp == File.GetLastWriteTimeUtc(output) && bytes.SequenceEqual(File.ReadAllBytes(output)), "Unchanged output rewritten.");
            var template=Path.Combine(source,"7","7.json");
            File.WriteAllText(template,"modified");File.SetLastWriteTimeUtc(template,DateTime.UnixEpoch);
            BundlePacker.Pack(source, output); Verify(output, source);
            Check(!bytes.SequenceEqual(File.ReadAllBytes(output)), "Old-timestamp edit missed.");
            Index(source,"7","8");BundlePacker.Pack(source,output);Verify(output,source);
            Index(source,"7");BundlePacker.Pack(source,output);Verify(output,source);
            using(var packed=ZipFile.OpenRead(output)) Check(packed.GetEntry("8/8.json.br")==null,"Removed title leaked into bundle.");
            Index(other,"9");BundlePacker.Pack(other,output);Verify(output,other);
            try { Verify(output, source); throw new Exception("Stale source accepted."); } catch (InvalidDataException) { }
            File.WriteAllBytes(output, File.ReadAllBytes(output)[..10]);
            try { Verify(output, other); throw new Exception("Corrupt bundle accepted."); } catch (InvalidDataException) { }
            File.Delete(Path.Combine(other,"9","9.json"));
            try { BundlePacker.Pack(other,output);throw new Exception("Missing title template accepted."); } catch (InvalidDataException) { }
            File.WriteAllText(Path.Combine(other,"index.json"),"{\"../unsafe\":{},\"SupportedTitleIDs\":[]}");
            try { BundlePacker.Pack(other,output);throw new Exception("Path traversal accepted."); } catch (InvalidDataException) { }
            Console.WriteLine("PASS: runtime allowlist, deterministic compression, edits, additions, removals, source changes, missing template/path rejection and archive verification.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Verify(string bundle, string source) { using var input = File.OpenRead(bundle); BundlePacker.Verify(input, source); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
