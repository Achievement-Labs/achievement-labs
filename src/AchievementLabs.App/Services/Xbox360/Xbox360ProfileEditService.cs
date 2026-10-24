using System.Globalization;
using System.Security.Cryptography;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Saves staged achievement changes to an extracted, backed-up local profile.</summary>
public sealed class Xbox360ProfileEditService(Xbox360PackageService packages, Xbox360ProfileService profiles)
{
    private readonly Xbox360ProfileReader reader = new(packages);
    private readonly Xbox360StfsSignedWriter writer = new(new Xbox360StfsUnsignedWriter(packages));

    public string Save(string workingCopy, string titleId, IReadOnlyCollection<Xbox360ProfileAchievement> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) throw new InvalidOperationException("Set at least one achievement before saving.");
        if (titleId.Length != 8 || !titleId.All(Uri.IsHexDigit))
            throw new InvalidDataException("The Xbox 360 title ID is invalid.");
        var profileFolder = Path.GetFullPath(profiles.ProfilesDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(workingCopy);
        if (!fullPath.StartsWith(profileFolder, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) || !fullPath.EndsWith(".con", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save requires a profile extracted into Achievement Labs.");
        if (!Xbox360ConSigner.VerifyFile(fullPath))
            throw new InvalidDataException("The source profile's CON signature or metadata hash is invalid.");
        Xbox360StfsIntegrity.VerifyFile(fullPath);
        if (Xbox360ProfileGameAddService.IsAwaitingConsoleSync(fullPath, titleId))
            throw new InvalidOperationException("This added game is awaiting Xbox 360 profile sync. Inject the profile, sync it on the console, then extract it again before editing achievements.");

        var info = packages.InspectPackage(fullPath);
        if (!info.ContentType.StartsWith("0x00010000", StringComparison.OrdinalIgnoreCase) || info.ProfileId.All(c => c == '0'))
            throw new InvalidDataException("Only a non-anonymous Xbox 360 profile can be edited.");
        var sourceHash = HashFile(fullPath);
        if (edits.Select(e => e.Id).Distinct().Count() != edits.Count) throw new InvalidDataException("Duplicate achievement edits are not allowed.");
        var original = reader.ReadAchievements(fullPath, titleId).ToDictionary(item => item.Id);
        var changed = edits.Where(edit => original.TryGetValue(edit.Id, out var before) &&
            (edit.Flags != before.Flags || edit.UnlockedAt?.ToFileTime() != before.UnlockedAt?.ToFileTime())).ToArray();
        if (changed.Length != edits.Count)
            throw new InvalidDataException("An edited achievement is missing or unchanged in this profile.");

        var unlockedDelta = 0;
        var gamerscoreDelta = 0;
        foreach (var edit in changed)
        {
            var before = original[edit.Id];
            if (edit.Id == 0 || edit.Name != before.Name || edit.Gamerscore != before.Gamerscore ||
                edit.LockedDescription != before.LockedDescription || edit.UnlockedDescription != before.UnlockedDescription ||
                (edit.Flags & ~0x130000u) != (before.Flags & ~0x130000u) ||
                (edit.Flags & 0x30000u) == 0x10000u ||
                ((edit.Flags & 0x30000u) == 0x30000u) != edit.UnlockedAt.HasValue ||
                edit.UnlockedAt < new DateTimeOffset(2005,11,22,0,0,0,TimeSpan.Zero) || edit.UnlockedAt > DateTimeOffset.UtcNow)
                throw new InvalidDataException("An edited achievement has invalid metadata, state or timestamp.");
            var difference = (edit.Flags & 0x20000) != 0 ? 1 : 0;
            difference -= (before.Flags & 0x20000) != 0 ? 1 : 0;
            unlockedDelta = checked(unlockedDelta + difference);
            gamerscoreDelta = checked(gamerscoreDelta + difference * checked((int)edit.Gamerscore));
        }

        var numericTitleId = uint.Parse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var gamePath = titleId.ToUpperInvariant() + ".gpd";
        var gameGpd = Xbox360GpdAchievementEditor.Apply(packages.ReadEntryBytes(fullPath, gamePath), changed);
        var sourceGames = reader.ReadGames(fullPath).ToDictionary(g => g.TitleId,StringComparer.OrdinalIgnoreCase);
        var sourceGame = sourceGames[titleId];
        var lastPlayed = changed.Select(edit => edit.UnlockedAt).Append(sourceGame.LastPlayed).Max();
        var expectedGame = sourceGame with { UnlockedCount=checked(sourceGame.UnlockedCount+unlockedDelta), GamerscoreUnlocked=checked(sourceGame.GamerscoreUnlocked+gamerscoreDelta), LastPlayed=lastPlayed };
        if (expectedGame.UnlockedCount<0 || expectedGame.UnlockedCount>expectedGame.AchievementCount || expectedGame.GamerscoreUnlocked<0 || expectedGame.GamerscoreUnlocked>expectedGame.GamerscoreTotal)
            throw new InvalidDataException("The proposed edit conflicts with the dashboard totals. Sync and re-extract this profile before retrying.");
        var dashboardGpd = Xbox360GpdDashboardEditor.Apply(
            packages.ReadEntryBytes(fullPath, "FFFE07D1.gpd"), numericTitleId,
            unlockedDelta, gamerscoreDelta, lastPlayed);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var backup = fullPath + $".before-save-{stamp}-{nonce}.bak";
        var staged = fullPath + $".{nonce}.partial";
        var rollback = fullPath + $".{nonce}.previous";
        File.Copy(fullPath, backup);
        if (!FileHashesMatch(fullPath, backup))
            throw new IOException("The pre-save profile backup could not be verified.");
        try
        {
            writer.Write(fullPath, staged, new Dictionary<string, byte[]>
            {
                [gamePath] = gameGpd,
                ["FFFE07D1.gpd"] = dashboardGpd
            });
            Xbox360StfsIntegrity.VerifyFile(staged);
            var written = reader.ReadAchievements(staged, titleId).ToDictionary(item => item.Id);
            var expected = changed.ToDictionary(edit => edit.Id);
            if (written.Count!=original.Count || original.Any(pair => !written.TryGetValue(pair.Key,out var actual) || actual!=expected.GetValueOrDefault(pair.Key,pair.Value)))
                throw new IOException("The saved achievement records did not match the requested changes.");
            var actualGames = reader.ReadGames(staged).ToDictionary(g => g.TitleId,StringComparer.OrdinalIgnoreCase);
            if (actualGames.Count!=sourceGames.Count || sourceGames.Any(pair => !actualGames.TryGetValue(pair.Key,out var actual) || actual!=(pair.Key.Equals(titleId,StringComparison.OrdinalIgnoreCase)?expectedGame:pair.Value)))
                throw new IOException("The saved dashboard records did not match the expected totals.");
            if (packages.InspectPackage(staged).ProfileId!=info.ProfileId)
                throw new IOException("The saved package profile ID changed unexpectedly.");

            if (!HashFile(fullPath).SequenceEqual(sourceHash)) throw new IOException("The working profile changed during save. Reload before retrying.");
            File.Replace(staged, fullPath, rollback);
            if (!Xbox360ConSigner.VerifyFile(fullPath))
                throw new IOException("The saved profile did not pass CON signature verification. The earlier copy remains beside it for recovery.");
            File.Delete(rollback);
            return backup;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static byte[] HashFile(string path) { using var input = File.OpenRead(path); return SHA256.HashData(input); }

    private static bool FileHashesMatch(string first, string second)
    {
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).SequenceEqual(SHA256.HashData(secondStream));
    }
}
