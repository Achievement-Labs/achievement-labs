using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

public sealed class Xbox360ProfileService(string? profilesDirectory = null)
{
    public string ProfilesDirectory { get; } = Path.GetFullPath(profilesDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AchievementLabs", "Xbox360", "Profiles"));

    public IReadOnlyList<string> FindUsbProfiles()
    {
        var results = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                var content = Path.Combine(drive.RootDirectory.FullName, "Content");
                if (!Directory.Exists(content)) continue;
                foreach (var owner in Directory.EnumerateDirectories(content))
                {
                    var id = Path.GetFileName(owner);
                    if (!IsProfileId(id)) continue;
                    var folder = Path.Combine(owner, "FFFE07D1", "00010000");
                    if (!Directory.Exists(folder)) continue;
                    foreach (var path in Directory.EnumerateFiles(folder))
                        if (Path.GetFileName(path).Equals(id, StringComparison.OrdinalIgnoreCase) && IsProfilePackage(path, id))
                            results.Add(path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return results;
    }

    public string? FindLatestExtractedProfile()
    {
        return FindLocalProfiles().FirstOrDefault();
    }

    public IReadOnlyList<string> FindLocalProfiles()
    {
        if (!Directory.Exists(ProfilesDirectory)) return [];
        return Directory.EnumerateFiles(ProfilesDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                var parent = Path.GetFileName(Path.GetDirectoryName(path)!);
                var id = IsProfileId(name) ? name : parent[..Math.Min(16, parent.Length)];
                return IsProfileId(id) && (IsProfileId(name) || name.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
                    && IsProfilePackage(path, id);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
    }

    public async Task<string> ExtractProfileAsync(string source, CancellationToken cancellationToken = default)
    {
        var id = Path.GetFileName(source);
        if (!IsProfileId(id) || !IsProfilePackage(source, id))
            throw new InvalidDataException("The selected file is not a matching Xbox 360 profile package.");

        var folder = Path.Combine(ProfilesDirectory, id + "-working");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"{id}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.con");
        var temporary = destination + ".partial";
        var backup = destination + ".bak";
        var backupTemporary = backup + ".partial";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);

            var sourceHash = await HashAsync(source, cancellationToken);
            var copyHash = await HashAsync(temporary, cancellationToken);
            if (!sourceHash.SequenceEqual(copyHash)) throw new IOException("Profile copy verification failed.");
            File.Copy(temporary, backupTemporary);
            var backupHash = await HashAsync(backupTemporary, cancellationToken);
            if (!sourceHash.SequenceEqual(backupHash)) throw new IOException("Profile backup verification failed.");
            File.Move(backupTemporary, backup);
            File.Move(temporary, destination);
            return destination;
        }
        catch
        {
            if (File.Exists(backup)) File.Delete(backup);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backupTemporary)) File.Delete(backupTemporary);
        }
    }

    public async Task<string> InjectProfileAsync(string workingCopy, string usbDestination, CancellationToken cancellationToken = default)
    {
        var id = Path.GetFileName(usbDestination);
        if (!IsProfileId(id) || !IsProfilePackage(workingCopy, id) || !IsProfilePackage(usbDestination, id))
            throw new InvalidDataException("The working and USB profiles must have the same valid profile ID.");
        var profilesFolder = Path.GetFullPath(ProfilesDirectory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(workingCopy).StartsWith(profilesFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only a profile extracted into Achievement Labs can be injected.");
        var knownDestination = FindUsbProfiles().Any(path =>
            Path.GetFullPath(path).Equals(Path.GetFullPath(usbDestination), StringComparison.OrdinalIgnoreCase));
        if (!knownDestination) throw new InvalidDataException("The target is not a detected Xbox 360 USB profile.");
        if (!Xbox360ConSigner.VerifyFile(workingCopy))
            throw new InvalidDataException("The working profile has an invalid CON signature or metadata hash.");
        if (!Xbox360ConSigner.VerifyFile(usbDestination))
            throw new InvalidDataException("The USB profile has an invalid CON signature or metadata hash.");

        var backupFolder = Path.Combine(ProfilesDirectory, id + "-working");
        Directory.CreateDirectory(backupFolder);
        var backup = Path.Combine(backupFolder, $"{id}-usb-before-inject-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bak");
        var staged = usbDestination + ".achievementlabs.partial";
        var rollback = usbDestination + ".achievementlabs.previous";
        if (File.Exists(staged) || File.Exists(rollback))
            throw new IOException("An earlier USB injection requires inspection before retrying.");
        File.Copy(usbDestination, backup);
        if (!(await HashAsync(usbDestination, cancellationToken)).SequenceEqual(await HashAsync(backup, cancellationToken)))
            throw new IOException("The original USB backup could not be verified.");
        try
        {
            await using (var input = new FileStream(workingCopy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);
            var sourceHash = await HashAsync(workingCopy, cancellationToken);
            if (!sourceHash.SequenceEqual(await HashAsync(staged, cancellationToken)))
                throw new IOException("The staged USB profile could not be verified.");
            File.Move(usbDestination, rollback);
            try { File.Move(staged, usbDestination); }
            catch { File.Move(rollback, usbDestination); throw; }
            if (!sourceHash.SequenceEqual(await HashAsync(usbDestination, cancellationToken)))
                throw new IOException("The injected USB profile could not be verified. The previous copy remains beside it for recovery.");
            File.Delete(rollback);
            return backup;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }

    private static bool IsProfileId(string id) => id.Length == 16 && id.All(Uri.IsHexDigit);

    private static bool IsProfilePackage(string path, string id)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 0x379) return false;
            var header = new byte[0x379];
            stream.ReadExactly(header);
            return header[0] == 'C' && header[1] == 'O' && header[2] == 'N' && header[3] == ' '
                && Convert.ToHexString(header.AsSpan(0x371, 8)).Equals(id, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
