using System.Buffers.Binary;
using System.Text;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

public static class Xbox360GpdAchievementEditor
{
    public static byte[] Apply(byte[] source, IReadOnlyCollection<Xbox360ProfileAchievement> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edits);
        if (source.Length < 24 || Encoding.ASCII.GetString(source, 0, 4) != "XDBF")
            throw new InvalidDataException("The selected entry is not a GPD database.");

        var result = (byte[])source.Clone();
        var capacity = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(8, 4));
        var count = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(12, 4));
        var freeCapacity = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(16, 4));
        var dataStart = 24L + capacity * 18L + freeCapacity * 8L;
        if (count > capacity || count > 100000 || dataStart > result.Length)
            throw new InvalidDataException("The GPD entry table is invalid.");

        var byId = edits.ToDictionary(edit => edit.Id);
        var found = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var entry = 24 + checked((int)index * 18);
            if (entry + 18 > result.Length)
                throw new InvalidDataException("The GPD entry table is truncated.");
            if (BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(entry, 2)) != 1) continue;
            var id = BinaryPrimitives.ReadUInt64BigEndian(result.AsSpan(entry + 2, 8));
            if (id > uint.MaxValue || !byId.TryGetValue((uint)id, out var edit)) continue;
            var start = dataStart + BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(entry + 10, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(entry + 14, 4));
            if (length < 28 || start < dataStart || start + length > result.Length)
                throw new InvalidDataException($"Achievement {id} has an invalid GPD record.");
            var payload = result.AsSpan(checked((int)start), checked((int)length));
            if (BinaryPrimitives.ReadUInt32BigEndian(payload[..4]) != 28 ||
                BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)) != edit.Id)
                throw new InvalidDataException($"Achievement {id} has an unexpected GPD record.");

            BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(16, 4), edit.Flags);
            BinaryPrimitives.WriteInt64BigEndian(payload.Slice(20, 8), edit.UnlockedAt?.ToFileTime() ?? 0L);
            Xbox360GpdSyncEditor.Touch(result, 1, edit.Id);
            found.Add(edit.Id);
        }
        if (found.Count != byId.Count)
            throw new InvalidDataException("One or more edited achievements were not found in the game GPD.");
        return result;
    }
}
