using System.Globalization;
using System.Text;

namespace AchievementLabs.Mirror;

/// <summary>
/// Reads achievements straight from the Steam client's local cache on this PC:
/// <c>appcache\stats\UserGameStats_&lt;accountId&gt;_&lt;appId&gt;.bin</c> (which achievements are unlocked, and when)
/// plus <c>UserGameStatsSchema_&lt;appId&gt;.bin</c> (which bit is which achievement). Steam rewrites the stats file
/// the moment an achievement unlocks on this PC, so this is immediate, unlike the Steam Web API, which can lag.
/// </summary>
public static class SteamLocalStats
{
    public static string StatsPath(string steamPath, uint accountId, long appId) =>
        Path.Combine(steamPath, "appcache", "stats", $"UserGameStats_{accountId}_{appId.ToString(CultureInfo.InvariantCulture)}.bin");

    public static string SchemaPath(string steamPath, long appId) =>
        Path.Combine(steamPath, "appcache", "stats", $"UserGameStatsSchema_{appId.ToString(CultureInfo.InvariantCulture)}.bin");

    /// <summary>Returns null when either file is missing (e.g. the game's stats were never loaded on this PC).</summary>
    public static IReadOnlyList<MirrorAchievement>? TryRead(string steamPath, uint accountId, long appId)
    {
        var stats = StatsPath(steamPath, accountId, appId);
        var schema = SchemaPath(steamPath, appId);
        if (!File.Exists(stats) || !File.Exists(schema)) return null;
        return Parse(ReadShared(stats), ReadShared(schema), appId);
    }

    /// <summary>Reads a file Steam may be writing at the same moment, retrying briefly.</summary>
    private static byte[] ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(100); }
        }
    }

    public static IReadOnlyList<MirrorAchievement> Parse(byte[] statsFile, byte[] schemaFile, long appId)
    {
        var schemaRoot = KeyValues.Parse(schemaFile);
        var game = schemaRoot.Child(appId.ToString(CultureInfo.InvariantCulture)) ?? schemaRoot.Children.FirstOrDefault()
            ?? throw new InvalidDataException("Steam's achievement schema for this game is empty.");
        var statsRoot = KeyValues.Parse(statsFile);
        // The stat blocks sit at the top level or under "cache" depending on the Steam client version.
        var userStats = statsRoot.Child("cache") ?? statsRoot;

        var result = new List<MirrorAchievement>();
        foreach (var stat in game.Child("stats")?.Children ?? [])
        {
            if (!IsAchievementStat(stat.Child("type")) || stat.Child("bits") is not { } bits) continue;
            var user = userStats.Child(stat.Name);
            var mask = unchecked((uint)(user?.Child("data")?.AsInt() ?? 0));
            var times = user?.Child("AchievementTimes");
            foreach (var bit in bits.Children)
            {
                var index = bit.Child("bit")?.AsInt() ?? (int.TryParse(bit.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1);
                var id = bit.Child("name")?.AsString() ?? "";
                if (index is < 0 or > 31 || id.Length == 0) continue;
                var display = bit.Child("display");
                var name = display?.Child("name")?.Child("english")?.AsString() ?? display?.Child("name")?.AsString() ?? id;
                var unlocked = (mask & (1u << index)) != 0;
                var seconds = times?.Child(index.ToString(CultureInfo.InvariantCulture))?.AsInt() ?? 0;
                DateTimeOffset? at = unlocked && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds) : null;
                var description = display?.Child("desc")?.Child("english")?.AsString() ?? "";
                result.Add(new(id, name.Trim(), description, unlocked, at));
            }
        }
        return result;
    }

    private static bool IsAchievementStat(KeyValues? type)
    {
        if (type == null) return false;
        if (type.Value is int code) return code is 4 or 5; // Achievements, GroupAchievements
        return type.AsString()?.Contains("ACHIEVEMENT", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Minimal reader for Valve's binary KeyValues format.</summary>
    public sealed class KeyValues
    {
        public string Name { get; private init; } = "";
        public object? Value { get; private init; }
        public List<KeyValues> Children { get; } = [];

        public KeyValues? Child(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        public int? AsInt() => Value switch { int i => i, long l => (int)l, ulong u => (int)u, uint u => (int)u, string s when int.TryParse(s, out var p) => p, _ => null };
        public string? AsString() => Value switch { string s => s, null => null, IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var other => other.ToString() };

        public static KeyValues Parse(byte[] data)
        {
            var root = new KeyValues { Name = "" };
            var position = 0;
            ReadChildren(data, ref position, root, 0);
            return root;
        }

        private static void ReadChildren(byte[] data, ref int position, KeyValues parent, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Steam stats file is nested too deeply.");
            while (position < data.Length)
            {
                var type = data[position++];
                if (type is 8 or 11) return;
                var name = ReadString(data, ref position);
                object? value;
                switch (type)
                {
                    case 0:
                        var node = new KeyValues { Name = name };
                        ReadChildren(data, ref position, node, depth + 1);
                        parent.Children.Add(node);
                        continue;
                    case 1: value = ReadString(data, ref position); break;
                    case 2: value = BitConverter.ToInt32(Take(data, ref position, 4)); break;
                    case 3: value = BitConverter.ToSingle(Take(data, ref position, 4)); break;
                    case 4: value = BitConverter.ToUInt32(Take(data, ref position, 4)); break;
                    case 5: value = ReadWideString(data, ref position); break;
                    case 6: value = BitConverter.ToUInt32(Take(data, ref position, 4)); break;
                    case 7: value = BitConverter.ToUInt64(Take(data, ref position, 8)); break;
                    case 10: value = BitConverter.ToInt64(Take(data, ref position, 8)); break;
                    default: throw new InvalidDataException($"Unknown Steam stats value type {type}.");
                }
                parent.Children.Add(new KeyValues { Name = name, Value = value });
            }
        }

        private static ReadOnlySpan<byte> Take(byte[] data, ref int position, int count)
        {
            if (position + count > data.Length) throw new InvalidDataException("Steam stats file ended early.");
            var span = new ReadOnlySpan<byte>(data, position, count);
            position += count;
            return span;
        }

        private static string ReadString(byte[] data, ref int position)
        {
            var end = Array.IndexOf(data, (byte)0, position);
            if (end < 0) throw new InvalidDataException("Steam stats file ended early.");
            var text = Encoding.UTF8.GetString(data, position, end - position);
            position = end + 1;
            return text;
        }

        private static string ReadWideString(byte[] data, ref int position)
        {
            var start = position;
            while (position + 1 < data.Length && (data[position] != 0 || data[position + 1] != 0)) position += 2;
            var text = Encoding.Unicode.GetString(data, start, position - start);
            position += 2;
            return text;
        }
    }
}
