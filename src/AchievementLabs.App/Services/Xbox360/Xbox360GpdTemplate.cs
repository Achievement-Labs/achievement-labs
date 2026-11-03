using System.Buffers.Binary;
using System.Text;

namespace AchievementLabs.Services.Xbox360;

/// <summary>
/// Builds the locked catalog form of a title GPD: achievements, images and strings only, every
/// achievement locked, fresh sync records. Byte-for-byte equal to Labs360.Gpd.template in profile-core.js.
/// </summary>
public static class Xbox360GpdTemplate
{
    public sealed record Info(string Name, int Achievements, int Gamerscore, int Unlocked);
    private sealed record Entry(ushort Namespace, ulong Id, byte[] Data);
    private const ulong SyncList = 0x100000000, SyncData = 0x200000000, TitleString = 0x8000;
    public const int MaxBytes = 8 * 1024 * 1024;

    public static Info Inspect(byte[] gpd)
    {
        var entries = Read(gpd);
        var achievements = entries.Where(IsAchievement).ToArray();
        if (achievements.Length is 0 or > 5000) throw new InvalidDataException("The GPD has no achievements.");
        var unlocked = 0; var score = 0;
        foreach (var entry in achievements)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(entry.Data.AsSpan(4));
            if (id == 0 || entry.Id != id) throw new InvalidDataException("Achievement ID mismatch.");
            var flags = BinaryPrimitives.ReadUInt32BigEndian(entry.Data.AsSpan(16));
            score = checked(score + (int)BinaryPrimitives.ReadUInt32BigEndian(entry.Data.AsSpan(12)));
            if ((flags & 0x120000) != 0 || BinaryPrimitives.ReadUInt64BigEndian(entry.Data.AsSpan(20)) != 0) unlocked++;
        }
        var title = entries.FirstOrDefault(e => e.Namespace == 5 && e.Id == TitleString);
        return new(title is { Data.Length: >= 2 } ? ReadString(title.Data) : "", achievements.Length, score, unlocked);
    }

    public static byte[] Reset(byte[] gpd)
    {
        if (gpd.Length < 24 || gpd.Length > MaxBytes) throw new InvalidDataException("GPD must be smaller than 8 MB.");
        Inspect(gpd);
        var kept = new List<Entry>(); var ids = new List<ulong>();
        foreach (var entry in Read(gpd))
        {
            if (IsAchievement(entry))
            {
                var data = (byte[])entry.Data.Clone();
                var flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)) & ~0x130000u;
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), flags);
                data.AsSpan(20, 8).Clear();
                kept.Add(entry with { Data = data }); ids.Add(entry.Id);
            }
            else if (entry.Namespace is 2 or 5 && entry.Id <= uint.MaxValue) kept.Add(entry with { Data = (byte[])entry.Data.Clone() });
        }
        ids.Sort();
        var list = new byte[ids.Count * 16];
        for (var i = 0; i < ids.Count; i++) BinaryPrimitives.WriteUInt64BigEndian(list.AsSpan(i * 16), ids[i]);
        var sync = new byte[24]; BinaryPrimitives.WriteUInt64BigEndian(sync, 1);
        kept.Add(new(1, SyncList, list)); kept.Add(new(1, SyncData, sync));
        var result = Write(kept);
        var info = Inspect(result);
        if (info.Unlocked != 0 || info.Achievements != ids.Count) throw new InvalidDataException("GPD reset did not verify.");
        return result;
    }

    private static bool IsAchievement(Entry e) => e.Namespace == 1 && e.Id <= uint.MaxValue && e.Data.Length >= 28 &&
        BinaryPrimitives.ReadUInt32BigEndian(e.Data) == 28;

    private static List<Entry> Read(byte[] b)
    {
        if (b.Length < 24 || !b.AsSpan(0, 4).SequenceEqual("XDBF"u8)) throw new InvalidDataException("Invalid game GPD database.");
        var cap = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)); var count = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
        var free = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)); var start = 24L + cap * 18L + free * 8L;
        if (count > cap || count > 100000 || start > b.Length) throw new InvalidDataException("Invalid GPD entry table.");
        var result = new List<Entry>(); var seen = new HashSet<(ushort, ulong)>();
        for (var i = 0; i < count; i++)
        {
            var o = 24 + i * 18;
            var ns = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)); var id = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o + 2));
            var offset = start + BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 10)); var length = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 14));
            if (offset + length > b.Length) throw new InvalidDataException("Truncated or out-of-range profile record.");
            if (!seen.Add((ns, id))) throw new InvalidDataException("Duplicate GPD entry.");
            result.Add(new(ns, id, b.AsSpan((int)offset, (int)length).ToArray()));
        }
        return result;
    }

    private static byte[] Write(List<Entry> entries)
    {
        entries.Sort((a, b) => a.Namespace != b.Namespace ? a.Namespace.CompareTo(b.Namespace) : a.Id.CompareTo(b.Id));
        var cap = Math.Max(512, (entries.Count + 511) / 512 * 512); const int freeCap = 512;
        var start = 24 + cap * 18 + freeCap * 8; var total = entries.Sum(e => e.Data.Length);
        var output = new byte[start + total];
        "XDBF"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(8), (uint)cap);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), freeCap);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20), 1);
        var offset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var o = 24 + i * 18; var e = entries[i];
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o), e.Namespace);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(o + 2), e.Id);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 10), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(o + 14), (uint)e.Data.Length);
            e.Data.CopyTo(output, start + offset); offset += e.Data.Length;
        }
        var freeEntry = 24 + cap * 18;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(freeEntry), (uint)offset);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(freeEntry + 4), uint.MaxValue - (uint)offset);
        return output;
    }

    private static string ReadString(byte[] data)
    {
        var end = 0; while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        return Encoding.BigEndianUnicode.GetString(data, 0, end);
    }
}
