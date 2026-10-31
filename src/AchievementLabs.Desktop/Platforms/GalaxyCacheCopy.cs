namespace AchievementLabs.Desktop.Platforms;

// Some SQLite versions need a writable shared-memory sidecar even for read-only WAL queries.
// Copy only the database and WAL to a private temporary directory, never the launcher's sidecar.
internal sealed class GalaxyCacheCopy : IDisposable
{
    private readonly string directory;
    public string DatabasePath { get; }
    public GalaxyCacheCopy(string source, CancellationToken token)
    {
        directory = Path.Combine(Path.GetTempPath(), "AchievementLabs-Galaxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "galaxy.db");
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var before = Stamp(source);
                var walBefore = Stamp(source + "-wal");
                CopyShared(source, DatabasePath);
                if (walBefore.Exists) CopyShared(source + "-wal", DatabasePath + "-wal");
                else if (File.Exists(DatabasePath + "-wal")) File.Delete(DatabasePath + "-wal");
                if (before == Stamp(source) && walBefore == Stamp(source + "-wal")) return;
                Thread.Sleep(50);
            }
            throw new IOException("Galaxy is updating its cache. Wait for its sync to finish, then refresh.");
        }
        catch { Dispose(); throw; }
    }
    private static (bool Exists, long Length, long Stamp) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (true, info.Length, info.LastWriteTimeUtc.Ticks) : (false, 0, 0);
    }
    private static void CopyShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
    public void Dispose()
    {
        // Cleanup is restricted to the exact temporary directory created by this instance.
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("AchievementLabs-Galaxy-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); } catch { /* A locked temp file can be cleaned by Windows later. */ }
    }
}
