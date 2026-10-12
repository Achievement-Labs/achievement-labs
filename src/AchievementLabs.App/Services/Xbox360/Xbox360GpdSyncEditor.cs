using System.Buffers.Binary;
using System.Text;

namespace AchievementLabs.Services.Xbox360;

internal static class Xbox360GpdSyncEditor
{
    public static Span<byte> Payload(byte[] gpd, ushort entryNamespace, ulong entryId)
    {
        if (gpd.Length < 24 || Encoding.ASCII.GetString(gpd, 0, 4) != "XDBF")
            throw new InvalidDataException("The selected entry is not a GPD database.");
        var capacity = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(8, 4));
        var count = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(12, 4));
        var freeCapacity = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(16, 4));
        var dataStart = 24L + capacity * 18L + freeCapacity * 8L;
        if (count > capacity || count > 100000 || dataStart > gpd.Length)
            throw new InvalidDataException("The GPD entry table is invalid.");
        var payload = FindPayload(gpd, count, dataStart, entryNamespace, entryId);
        if (payload.IsEmpty) throw new InvalidDataException($"GPD entry {entryId:X} was not found.");
        return payload;
    }

    public static void Touch(byte[] gpd, ushort entryNamespace, ulong entryId) =>
        MoveItem(gpd, entryNamespace, entryId, assignSyncId: true);

    public static void MoveToEnd(byte[] gpd, ushort entryNamespace, ulong entryId) =>
        MoveItem(gpd, entryNamespace, entryId, assignSyncId: false);

    private static void MoveItem(byte[] gpd, ushort entryNamespace, ulong entryId, bool assignSyncId)
    {
        if (gpd.Length < 24 || Encoding.ASCII.GetString(gpd, 0, 4) != "XDBF")
            throw new InvalidDataException("The sync target is not a GPD database.");
        var capacity = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(8, 4));
        var count = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(12, 4));
        var freeCapacity = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(16, 4));
        var dataStart = 24L + capacity * 18L + freeCapacity * 8L;
        if (count > capacity || count > 100000 || dataStart > gpd.Length)
            throw new InvalidDataException("The GPD entry table is invalid.");

        var list = FindPayload(gpd, count, dataStart, entryNamespace, 0x100000000);
        var data = FindPayload(gpd, count, dataStart, entryNamespace, 0x200000000);
        if (list.Length == 0 || list.Length % 16 != 0 || (assignSyncId && data.Length < 8))
            throw new InvalidDataException("The GPD sync records are invalid.");
        var position = -1;
        for (var offset = 0; offset < list.Length; offset += 16)
            if (BinaryPrimitives.ReadUInt64BigEndian(list.Slice(offset, 8)) == entryId)
            {
                position = offset;
                break;
            }
        if (position < 0) throw new InvalidDataException("The edited GPD entry is missing from its sync list.");

        var nextId = assignSyncId ? BinaryPrimitives.ReadUInt64BigEndian(data[..8]) : 0;
        if (assignSyncId && nextId == ulong.MaxValue) throw new InvalidDataException("The GPD sync counter is exhausted.");
        Span<byte> moved = stackalloc byte[16];
        list.Slice(position, 16).CopyTo(moved);
        if (position + 16 < list.Length)
            list.Slice(position + 16).CopyTo(list.Slice(position));
        var last = list[^16..];
        moved.CopyTo(last);
        if (assignSyncId)
        {
            BinaryPrimitives.WriteUInt64BigEndian(last.Slice(8, 8), nextId);
            BinaryPrimitives.WriteUInt64BigEndian(data[..8], nextId + 1);
        }
    }

    private static Span<byte> FindPayload(byte[] gpd, uint count, long dataStart, ushort entryNamespace, ulong entryId)
    {
        for (var index = 0; index < count; index++)
        {
            var entry = 24 + checked((int)index * 18);
            if (entry + 18 > gpd.Length) throw new InvalidDataException("The GPD entry table is truncated.");
            if (BinaryPrimitives.ReadUInt16BigEndian(gpd.AsSpan(entry, 2)) != entryNamespace ||
                BinaryPrimitives.ReadUInt64BigEndian(gpd.AsSpan(entry + 2, 8)) != entryId) continue;
            var start = dataStart + BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(entry + 10, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(gpd.AsSpan(entry + 14, 4));
            if (length > int.MaxValue || start < dataStart || start + length > gpd.Length)
                throw new InvalidDataException("A GPD sync record is out of range.");
            return gpd.AsSpan(checked((int)start), checked((int)length));
        }
        return Span<byte>.Empty;
    }
}
