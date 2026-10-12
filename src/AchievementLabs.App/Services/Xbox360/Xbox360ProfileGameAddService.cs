using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Adds a catalog GPD to a backed-up local profile and verifies the signed result.</summary>
public sealed class Xbox360ProfileGameAddService(Xbox360PackageService packages, Xbox360ProfileService profiles)
{
    public static bool IsAwaitingConsoleSync(string workingCopy, string titleId)
    {
        var marker = workingCopy + ".awaiting-console-sync.txt";
        return File.Exists(marker) && File.ReadAllLines(marker)
            .Any(line => line.Equals(titleId, StringComparison.OrdinalIgnoreCase));
    }

    public string GpdDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs", "Xbox360", "GPD");

    public string? FindGpd(string titleId)
    {
        if (titleId.Length != 8 || !titleId.All(Uri.IsHexDigit)) return null;
        if (!Directory.Exists(GpdDirectory)) return null;
        return Directory.EnumerateFiles(GpdDirectory, "*.gpd", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(titleId + ".gpd", StringComparison.OrdinalIgnoreCase));
    }

    public string Add(string workingCopy, string titleId)
    {
        var gpdPath = FindGpd(titleId) ?? throw new FileNotFoundException(
            $"Place {titleId.ToUpperInvariant()}.gpd in {GpdDirectory}, then try again.");
        var profileRoot = Path.GetFullPath(profiles.ProfilesDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(workingCopy);
        if (!fullPath.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Load a backed-up Achievement Labs working profile before adding a game.");
        if (!Xbox360ConSigner.VerifyFile(fullPath))
            throw new InvalidDataException("The working profile's CON signature or metadata hash is invalid.");
        var gpd = File.ReadAllBytes(gpdPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var nonce = Guid.NewGuid().ToString("N");
        var backup = fullPath + $".before-add-{stamp}-{nonce}.bak";
        var staged = fullPath + $".{nonce}.partial";
        var rollback = fullPath + $".{nonce}.previous";
        File.Copy(fullPath, backup);
        if (!HashesMatch(fullPath, backup))
            throw new IOException("The pre-add profile backup could not be verified.");
        try
        {
            new Xbox360StfsGpdAdder(packages).Write(fullPath, staged, titleId, gpd);
            Xbox360ConSigner.SignFile(staged);
            if (!Xbox360ConSigner.VerifyFile(staged) ||
                !packages.ReadEntryBytes(staged, titleId.ToUpperInvariant() + ".gpd").SequenceEqual(gpd))
                throw new IOException("The signed profile or inserted GPD did not verify.");
            File.Move(fullPath, rollback);
            try { File.Move(staged, fullPath); }
            catch { File.Move(rollback, fullPath); throw; }
            if (!Xbox360ConSigner.VerifyFile(fullPath))
                throw new IOException("The updated profile did not verify. The earlier copy remains beside it for recovery.");
            var marker = fullPath + ".awaiting-console-sync.txt";
            var marked = File.Exists(marker) ? File.ReadAllLines(marker).ToList() : [];
            if (!marked.Contains(titleId, StringComparer.OrdinalIgnoreCase)) marked.Add(titleId.ToUpperInvariant());
            var markerStaged = marker + "." + nonce + ".partial";
            try
            {
                File.WriteAllLines(markerStaged, marked);
                File.Move(markerStaged, marker, true);
            }
            finally { if (File.Exists(markerStaged)) File.Delete(markerStaged); }
            File.Delete(rollback);
            return backup;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static bool HashesMatch(string first, string second)
    {
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        return SHA256.HashData(a).SequenceEqual(SHA256.HashData(b));
    }
}
