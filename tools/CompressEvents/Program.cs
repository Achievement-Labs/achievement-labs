using System.IO.Compression;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CompressEvents <sourceDirectory> <outputBundlePath>");
    return 1;
}

string sourceDir = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);

if (!Directory.Exists(sourceDir))
{
    Console.Error.WriteLine($"Source directory does not exist: {sourceDir}");
    return 1;
}

var allFiles = Directory.GetFiles(sourceDir, "*.json", SearchOption.AllDirectories);
var filesToPack = new List<(string RelPath, string FullPath)>();
DateTime newestSource = DateTime.MinValue;

foreach (var file in allFiles)
{
    if (file.Contains("backup", StringComparison.OrdinalIgnoreCase)) continue;
    var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
    filesToPack.Add((rel, file));
    var wt = File.GetLastWriteTimeUtc(file);
    if (wt > newestSource) newestSource = wt;
}

if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= newestSource)
{
    Console.WriteLine($"[CompressEvents] {outputPath} is up to date ({filesToPack.Count} files).");
    return 0;
}

string? outDir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

string tempPath = outputPath + ".tmp";
using (var outStream = File.Create(tempPath))
using (var writer = new BinaryWriter(outStream, Encoding.UTF8))
{
    writer.Write(new char[] { 'L', 'E', 'V', '1' });
    writer.Write(filesToPack.Count);

    foreach (var (rel, fullPath) in filesToPack)
    {
        byte[] uncompressedBytes = File.ReadAllBytes(fullPath);
        using var compMs = new MemoryStream();
        using (var def = new DeflateStream(compMs, CompressionLevel.Optimal, true))
        {
            def.Write(uncompressedBytes);
        }
        byte[] compressedBytes = compMs.ToArray();

        writer.Write(rel);
        writer.Write(uncompressedBytes.Length);
        writer.Write(compressedBytes.Length);
        writer.Write(compressedBytes);
    }
    writer.Flush();
}

File.Move(tempPath, outputPath, true);
var fi = new FileInfo(outputPath);
Console.WriteLine($"[CompressEvents] Successfully compressed {filesToPack.Count} files into {outputPath} ({fi.Length:N0} bytes / {fi.Length / (1024.0 * 1024.0):F2} MB).");
return 0;
