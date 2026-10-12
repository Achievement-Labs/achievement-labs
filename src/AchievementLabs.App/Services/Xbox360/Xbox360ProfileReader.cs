using System.Buffers.Binary;
using System.Text;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

public sealed class Xbox360ProfileReader(Xbox360PackageService packages)
{
    public IReadOnlyList<Xbox360ProfileGame> ReadGames(string packagePath)
    {
        var bytes = packages.ReadEntryBytes(packagePath, "FFFE07D1.gpd");
        var games = new List<Xbox360ProfileGame>();
        foreach (var entry in ReadEntries(bytes, 4))
        {
            var data = entry.Payload;
            if (data.Length < 40) continue;
            var titleId = ReadU32(data, 0);
            if (titleId == 0) continue;
            var name = ReadString(data, 40);
            games.Add(new Xbox360ProfileGame(
                titleId.ToString("X8"), string.IsNullOrWhiteSpace(name) ? $"Title {titleId:X8}" : name,
                ReadI32(data, 4), ReadI32(data, 8),
                ReadI32(data, 12), ReadI32(data, 16)));
        }
        return games.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<Xbox360ProfileAchievement> ReadAchievements(string packagePath, string titleId)
    {
        if (titleId.Length != 8 || !titleId.All(Uri.IsHexDigit))
            throw new ArgumentException("A hexadecimal Xbox 360 title ID is required.", nameof(titleId));
        var bytes = packages.ReadEntryBytes(packagePath, titleId + ".gpd");
        var achievements = new List<Xbox360ProfileAchievement>();
        foreach (var entry in ReadEntries(bytes, 1))
        {
            var data = entry.Payload;
            if (data.Length < 28 || ReadU32(data, 0) != 28) continue;
            var fileTime = ReadI64(data, 20);
            DateTimeOffset? unlockedAt = null;
            if (fileTime > 0)
            {
                try { unlockedAt = DateTimeOffset.FromFileTime(fileTime); }
                catch (ArgumentOutOfRangeException) { }
            }
            var strings = ReadStrings(data, 28, 3);
            achievements.Add(new Xbox360ProfileAchievement(
                ReadU32(data, 4), strings[0], strings[2], strings[1],
                ReadU32(data, 12), ReadU32(data, 16), unlockedAt));
        }
        return achievements.OrderBy(item => item.Id).ToArray();
    }

    private static IEnumerable<(ulong Id, byte[] Payload)> ReadEntries(byte[] data, ushort wantedNamespace)
    {
        if (data.Length < 24 || Encoding.ASCII.GetString(data, 0, 4) != "XDBF")
            throw new InvalidDataException("The package entry is not an Xbox 360 GPD database.");
        var capacity = ReadU32(data, 8);
        var count = ReadU32(data, 12);
        var freeCapacity = ReadU32(data, 16);
        var payloadBase = 24L + capacity * 18L + freeCapacity * 8L;
        if (count > capacity || count > 100000 || payloadBase > data.Length)
            throw new InvalidDataException("The GPD entry table is invalid.");
        for (var index = 0; index < count; index++)
        {
            var offset = 24 + checked((int)index * 18);
            if (offset + 18 > data.Length) throw new InvalidDataException("The GPD entry table is truncated.");
            if (ReadU16(data, offset) != wantedNamespace) continue;
            var id = ReadU64(data, offset + 2);
            var start = payloadBase + ReadU32(data, offset + 10);
            var length = ReadU32(data, offset + 14);
            if (length == 0 || start < payloadBase || start + length > data.Length) continue;
            var payload = new byte[checked((int)length)];
            Buffer.BlockCopy(data, checked((int)start), payload, 0, payload.Length);
            yield return (id, payload);
        }
    }

    private static string ReadString(byte[] bytes, int offset)
    {
        var end = offset;
        while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0)) end += 2;
        return Encoding.BigEndianUnicode.GetString(bytes, offset, Math.Max(0, end - offset));
    }

    private static string[] ReadStrings(byte[] bytes, int offset, int count)
    {
        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            var end = offset;
            while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0)) end += 2;
            values[index] = Encoding.BigEndianUnicode.GetString(bytes, offset, Math.Max(0, end - offset));
            offset = Math.Min(bytes.Length, end + 2);
        }
        return values;
    }

    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static int ReadI32(byte[] data, int offset) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
    private static ulong ReadU64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
    private static long ReadI64(byte[] data, int offset) => BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset, 8));
}
