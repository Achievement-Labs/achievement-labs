using System.Buffers.Binary;

namespace AchievementLabs.Services.Xbox360;

public static class Xbox360GpdDashboardEditor
{
    public static byte[] Apply(byte[] source, uint titleId, int unlockedDelta, int gamerscoreDelta,
        DateTimeOffset? lastPlayed)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = (byte[])source.Clone();
        var title = Xbox360GpdSyncEditor.Payload(result, 4, titleId);
        if (title.Length < 40 || BinaryPrimitives.ReadUInt32BigEndian(title[..4]) != titleId)
            throw new InvalidDataException("The dashboard title entry is invalid.");

        UpdateNonnegative(title.Slice(8, 4), unlockedDelta);
        UpdateNonnegative(title.Slice(16, 4), gamerscoreDelta);
        if (lastPlayed is { } time)
            BinaryPrimitives.WriteInt64BigEndian(title.Slice(32, 8), time.ToFileTime());

        UpdateSetting(result, 0x10040006, gamerscoreDelta);
        UpdateSetting(result, 0x10040013, unlockedDelta);
        Xbox360GpdSyncEditor.MoveToEnd(result, 4, titleId);
        return result;
    }

    private static void UpdateSetting(byte[] gpd, ulong id, int delta)
    {
        var setting = Xbox360GpdSyncEditor.Payload(gpd, 3, id);
        if (setting.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(setting[..4]) != id || setting[8] != 1)
            throw new InvalidDataException($"Dashboard setting {id:X} is invalid.");
        UpdateNonnegative(setting.Slice(16, 4), delta);
    }

    private static void UpdateNonnegative(Span<byte> value, int delta)
    {
        var current = BinaryPrimitives.ReadInt32BigEndian(value);
        var updated = checked(current + delta);
        if (updated < 0) throw new InvalidDataException("An Xbox 360 profile total cannot be negative.");
        BinaryPrimitives.WriteInt32BigEndian(value, updated);
    }
}
