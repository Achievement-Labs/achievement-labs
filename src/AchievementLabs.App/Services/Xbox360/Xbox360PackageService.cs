using System.IO;
using System.Security.Cryptography;
using System.Text;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

public sealed class Xbox360PackageService : IXbox360PackageService
{
    private const int MaxHeaderBytes = 0xA000;
    private const int StfsBlockSize = 0x1000;
    private const int FileEntrySize = 0x40;

    public string HorizonInstallPath { get; } =
        @"C:\Program Files (x86)\Daring Development\Horizon\Horizon.exe";

    public bool IsHorizonInstalled => File.Exists(HorizonInstallPath);

    public Xbox360PackageInfo InspectPackage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("No file path was provided.", nameof(filePath));

        var file = new FileInfo(filePath);
        if (!file.Exists)
            throw new FileNotFoundException("The selected Xbox 360 package was not found.", filePath);

        var header = ReadHeader(file);
        var magic = Encoding.ASCII.GetString(header, 0x0, 4);
        var looksLikeStfs = magic is "CON " or "LIVE" or "PIRS";
        var headerSize = (int)ReadUInt32BE(header, 0x340);
        var contentType = ReadUInt32BE(header, 0x344);
        var probes = BuildProbes(header, magic).ToList();
        var entries = looksLikeStfs ? ReadFileTable(file, header, magic, headerSize).ToList() : [];

        return new Xbox360PackageInfo
        {
            FilePath = file.FullName,
            FileName = file.Name,
            FileSize = file.Length,
            Magic = string.IsNullOrWhiteSpace(magic) ? "Unknown" : magic,
            PackageType = magic switch
            {
                "CON " => "CON profile-owned package",
                "LIVE" => "LIVE marketplace package",
                "PIRS" => "PIRS signed package",
                _ => "Unknown"
            },
            ContentType = $"{FormatHex(contentType)} ({DescribeContentType(contentType)})",
            MediaId = FormatHex(ReadUInt32BE(header, 0x354)),
            TitleId = FormatHex(ReadUInt32BE(header, 0x360)),
            ProfileId = ReadHex(header, 0x371, 8),
            DeviceId = ReadHex(header, 0x3FD, 20),
            DisplayName = FirstReadable(
                ReadUtf8(header, 0x411, 0x80),
                ReadAscii(header, 0x411, 0x80)),
            TitleName = FirstReadable(
                ReadUtf8(header, 0x1691, 0x80),
                ReadAscii(header, 0x1691, 0x80)),
            LooksLikeStfsPackage = looksLikeStfs,
            Probes = probes,
            Entries = entries
        };
    }

    public byte[] ReadEntryBytes(string filePath, string entryPath)
    {
        var info = InspectPackage(filePath);
        var entry = info.Entries.FirstOrDefault(item => !item.IsDirectory &&
            item.Path.Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new FileNotFoundException("The package entry was not found.", entryPath);
        if (entry.Size < 0 || entry.Size > 64L * 1024 * 1024)
            throw new InvalidDataException("The package entry exceeds the supported size.");

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[Math.Min(MaxHeaderBytes, (int)Math.Min(stream.Length, MaxHeaderBytes))];
        stream.ReadExactly(header);
        var headerSize = (int)ReadUInt32BE(header, 0x340);
        var descriptor = ReadVolumeDescriptor(header);
        var magic = Encoding.ASCII.GetString(header, 0, 4);
        var result = new byte[checked((int)entry.Size)];
        var visited = new HashSet<int>();
        var block = entry.StartBlock;
        for (var offset = 0; offset < result.Length; offset += StfsBlockSize)
        {
            if (block < 0 || !visited.Add(block) || block >= descriptor.AllocatedBlockCount)
                throw new InvalidDataException("The package entry has an invalid block chain.");
            var physicalOffset = BlockToOffset(block, headerSize, descriptor.BlockSeparation, magic);
            var length = Math.Min(StfsBlockSize, result.Length - offset);
            if (physicalOffset < 0 || physicalOffset + length > stream.Length)
                throw new InvalidDataException("The package entry extends past the package.");
            stream.Position = physicalOffset;
            stream.ReadExactly(result, offset, length);
            if (offset + length < result.Length)
                block = ReadNextBlock(stream, block, headerSize, descriptor.BlockSeparation, magic);
        }
        return result;
    }

    private static byte[] ReadHeader(FileInfo file)
    {
        var headerLength = (int)Math.Min(MaxHeaderBytes, file.Length);
        var header = new byte[headerLength];
        using var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = stream.Read(header, 0, header.Length);
        if (read < header.Length)
            Array.Resize(ref header, read);

        return header;
    }

    private static IEnumerable<Xbox360PackageProbe> BuildProbes(byte[] data, string magic)
    {
        var headerSize = (int)ReadUInt32BE(data, 0x340);
        var descriptor = ReadVolumeDescriptor(data);

        yield return Probe("Magic", 0x0, ReadAscii(data, 0x0, 4));
        yield return Probe("Header Size", 0x340, headerSize == 0 ? "Unknown" : $"0x{headerSize:X}");
        yield return Probe("Content Type", 0x344, $"{FormatHex(ReadUInt32BE(data, 0x344))} ({DescribeContentType(ReadUInt32BE(data, 0x344))})");
        yield return Probe("Metadata Version", 0x348, ReadUInt32BE(data, 0x348).ToString());
        yield return Probe("Media ID", 0x354, FormatHex(ReadUInt32BE(data, 0x354)));
        yield return Probe("Title ID", 0x360, FormatHex(ReadUInt32BE(data, 0x360)));
        yield return Probe("Save Game ID", 0x368, FormatHex(ReadUInt32BE(data, 0x368)));
        yield return Probe("Console ID", 0x36C, ReadHex(data, 0x36C, 5));
        yield return Probe("Profile ID", 0x371, ReadHex(data, 0x371, 8));
        yield return Probe("Descriptor Size", 0x379, descriptor.Size.ToString());
        yield return Probe("File Table Blocks", 0x37C, descriptor.FileTableBlockCount.ToString());
        yield return Probe("File Table Start Block", 0x37E, descriptor.FileTableBlockNumber.ToString());
        yield return Probe("Allocated Blocks", 0x395, descriptor.AllocatedBlockCount.ToString());
        yield return Probe("Data File Count", 0x39D, ReadUInt32BE(data, 0x39D).ToString());
        yield return Probe("Device ID", 0x3FD, ReadHex(data, 0x3FD, 20));
        yield return Probe("Display Name", 0x411, FirstReadable(ReadUtf8(data, 0x411, 0x80), ReadAscii(data, 0x411, 0x80)));
        yield return Probe("Title Name", 0x1691, FirstReadable(ReadUtf8(data, 0x1691, 0x80), ReadAscii(data, 0x1691, 0x80)));
        yield return Probe("Base Data Offset", headerSize, $"0x{ComputeHeaderBase(headerSize):X}");
        yield return Probe("STFS Block Math", 0x379, magic is "CON " ? "CON hash table spacing" : "LIVE/PIRS hash table spacing");
    }

    private static IEnumerable<Xbox360PackageEntry> ReadFileTable(FileInfo file, byte[] header, string magic, int headerSize)
    {
        if (headerSize <= 0)
            yield break;

        var descriptor = ReadVolumeDescriptor(header);
        if (descriptor.FileTableBlockCount <= 0 || descriptor.FileTableBlockNumber < 0)
            yield break;

        var tableLength = Math.Min(descriptor.FileTableBlockCount * StfsBlockSize, 0x100000);
        if (tableLength < FileEntrySize)
            yield break;

        var table = new byte[tableLength];
        using (var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var block = descriptor.FileTableBlockNumber;
            var visited = new HashSet<int>();
            for (var offset = 0; offset < table.Length; offset += StfsBlockSize)
            {
                if (block < 0 || !visited.Add(block)) break;
                var tableOffset = BlockToOffset(block, headerSize, descriptor.BlockSeparation, magic);
                if (tableOffset < 0 || tableOffset + StfsBlockSize > stream.Length) break;
                stream.Position = tableOffset;
                stream.ReadExactly(table, offset, StfsBlockSize);
                block = ReadNextBlock(stream, block, headerSize, descriptor.BlockSeparation, magic);
            }
        }

        var parsed = new List<MutableFileEntry>();
        for (var offset = 0; offset + FileEntrySize <= table.Length; offset += FileEntrySize)
        {
            if (IsZeroBlock(table, offset, FileEntrySize))
                break;

            var flags = table[offset + 0x28];
            var nameLength = flags & 0x3F;
            if (nameLength <= 0 || nameLength > 0x28)
                continue;

            var name = ReadAscii(table, offset, nameLength);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var entry = new MutableFileEntry
            {
                Index = parsed.Count,
                Name = name,
                IsDirectory = (flags & 0x80) != 0,
                IsConsecutive = (flags & 0x40) != 0,
                Blocks = ReadInt24LE(table, offset + 0x29),
                StartBlock = ReadInt24LE(table, offset + 0x2F),
                PathIndicator = NormalizePathIndicator(ReadInt16BE(table, offset + 0x32)),
                Size = ReadUInt32BE(table, offset + 0x34)
            };

            parsed.Add(entry);
        }

        foreach (var entry in parsed)
            entry.Path = BuildEntryPath(parsed, entry);

        foreach (var entry in parsed)
        {
            yield return new Xbox360PackageEntry
            {
                Index = entry.Index,
                Name = entry.Name,
                Path = entry.Path,
                Kind = DescribeEntry(entry),
                IsDirectory = entry.IsDirectory,
                IsConsecutive = entry.IsConsecutive,
                Blocks = entry.Blocks,
                StartBlock = entry.StartBlock,
                PathIndicator = entry.PathIndicator,
                Size = entry.Size
            };
        }
    }

    private static VolumeDescriptor ReadVolumeDescriptor(byte[] data)
    {
        const int offset = 0x379;
        return new VolumeDescriptor
        {
            Size = ReadByte(data, offset),
            BlockSeparation = ReadByte(data, offset + 0x02),
            FileTableBlockCount = ReadUInt16LE(data, offset + 0x03),
            FileTableBlockNumber = ReadInt24LE(data, offset + 0x05),
            AllocatedBlockCount = ReadInt32BE(data, offset + 0x1C)
        };
    }

    internal static long BlockToOffset(int blockNumber, int headerSize, byte blockSeparation, string magic)
    {
        if (magic == "CON " && headerSize == 0x971A)
        {
            var adjustment = blockNumber >= 0xAA ? ((blockNumber / 0xAA) + 1) * 2 : 0;
            if (blockNumber >= 0x70E4)
                adjustment += ((blockNumber / 0x70E4) + 1) * 2;
            return 0xC000L + ((long)blockNumber + adjustment) * StfsBlockSize;
        }
        var baseOffset = ComputeHeaderBase(headerSize);
        var physicalBlock = ComputePhysicalBlockNumber(blockNumber, headerSize, blockSeparation, magic);
        return baseOffset + ((long)physicalBlock * StfsBlockSize);
    }

    internal static int ReadNextBlock(Stream stream, int blockNumber, int headerSize, byte blockSeparation, string magic)
    {
        if (magic != "CON " || headerSize != 0x971A) return blockNumber + 1;
        var tableBlock = (blockNumber / 0xAA) * 0xAC - 2;
        if (blockNumber >= 0xAA)
            tableBlock += ((blockNumber / 0x70E4) + 1) * 2;
        if (blockNumber >= 0x70E4)
            tableBlock += 2;
        var tableOffset = 0xC000L + (long)tableBlock * StfsBlockSize;
        var group = blockNumber / 0xAA;
        if (group < 0xAA)
        {
            var topHash = new byte[20];
            stream.Position = 0x381;
            stream.ReadExactly(topHash);
            var levelOneOffset = SelectHashTable(stream, 0xB6000, topHash);
            var levelZeroHash = new byte[20];
            stream.Position = levelOneOffset + group * 0x18L;
            stream.ReadExactly(levelZeroHash);
            tableOffset = SelectHashTable(stream, tableOffset, levelZeroHash);
        }
        var recordOffset = tableOffset + (blockNumber % 0xAA) * 0x18 + 0x15;
        if (recordOffset < 0 || recordOffset + 3 > stream.Length) return -1;
        stream.Position = recordOffset;
        Span<byte> next = stackalloc byte[3];
        stream.ReadExactly(next);
        var value = (next[0] << 16) | (next[1] << 8) | next[2];
        return value == 0xFFFFFF ? -1 : value;
    }

    private static long SelectHashTable(Stream stream, long firstOffset, byte[] expectedHash)
    {
        var table = new byte[StfsBlockSize];
        for (var copy = 0; copy < 2; copy++)
        {
            var offset = firstOffset + copy * StfsBlockSize;
            if (offset < 0 || offset + StfsBlockSize > stream.Length) continue;
            stream.Position = offset;
            stream.ReadExactly(table);
            if (SHA1.HashData(table).SequenceEqual(expectedHash)) return offset;
        }
        throw new InvalidDataException("The profile's active STFS hash table could not be verified.");
    }

    private static int ComputePhysicalBlockNumber(int blockNumber, int headerSize, byte blockSeparation, string magic)
    {
        var blockShift = ComputeHeaderBase(headerSize) == 0xB000 || (blockSeparation & 1) == 0 ? 1 : 0;
        var tableCount = (blockNumber + 0xAA) / 0xAA;
        if (magic == "CON ")
            tableCount <<= blockShift;

        var physical = tableCount + blockNumber;
        if (blockNumber >= 0x70E4)
            physical += ((blockNumber + 0x70E4) / 0x70E4) << blockShift;
        if (blockNumber >= 0x4AF768)
            physical += ((blockNumber + 0x4AF768) / 0x4AF768) << blockShift;

        return physical;
    }

    private static int ComputeHeaderBase(int headerSize)
    {
        if (headerSize <= 0)
            return 0;

        return (headerSize + 0xFFF) & 0xF000;
    }

    private static string BuildEntryPath(IReadOnlyList<MutableFileEntry> entries, MutableFileEntry entry)
    {
        if (entry.PathIndicator < 0 || entry.PathIndicator >= entries.Count || entry.PathIndicator == entry.Index)
            return entry.Name;

        var parent = entries[entry.PathIndicator];
        var parentPath = string.IsNullOrWhiteSpace(parent.Path)
            ? BuildEntryPath(entries, parent)
            : parent.Path;
        return $"{parentPath}/{entry.Name}";
    }

    private static string DescribeEntry(MutableFileEntry entry)
    {
        if (entry.IsDirectory)
            return "Directory";

        if (entry.Name.Equals("Account", StringComparison.OrdinalIgnoreCase))
            return "Account";

        if (entry.Name.EndsWith(".gpd", StringComparison.OrdinalIgnoreCase))
            return "GPD";

        return "File";
    }

    private static Xbox360PackageProbe Probe(string name, int offset, string value) => new()
    {
        Name = name,
        Offset = offset == 0 ? "0x0" : $"0x{offset:X}",
        Value = string.IsNullOrWhiteSpace(value) ? "Unknown" : value
    };

    private static byte ReadByte(byte[] data, int offset)
    {
        return offset >= 0 && offset < data.Length ? data[offset] : (byte)0;
    }

    private static int ReadInt16BE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
            return 0;

        return (short)((data[offset] << 8) | data[offset + 1]);
    }

    private static int ReadUInt16LE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
            return 0;

        return data[offset] | (data[offset + 1] << 8);
    }

    private static int ReadInt24BE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 3 > data.Length)
            return 0;

        var value = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static int ReadInt24LE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 3 > data.Length)
            return 0;

        var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static int ReadInt32BE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            return 0;

        return (int)ReadUInt32BE(data, offset);
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            return 0;

        return ((uint)data[offset] << 24)
            | ((uint)data[offset + 1] << 16)
            | ((uint)data[offset + 2] << 8)
            | data[offset + 3];
    }

    private static int NormalizePathIndicator(int value) => value == -1 ? -1 : value;

    private static bool IsZeroBlock(byte[] data, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (data[offset + i] != 0)
                return false;
        }

        return true;
    }

    private static string ReadHex(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length)
            return string.Empty;

        var count = Math.Min(length, data.Length - offset);
        return BitConverter.ToString(data, offset, count).Replace("-", string.Empty);
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length)
            return string.Empty;

        var count = Math.Min(length, data.Length - offset);
        return CleanString(Encoding.ASCII.GetString(data, offset, count));
    }

    private static string ReadUtf8(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset >= data.Length)
            return string.Empty;

        var count = Math.Min(length, data.Length - offset);
        return CleanString(Encoding.UTF8.GetString(data, offset, count));
    }

    private static string CleanString(string value)
    {
        var cleaned = new string(value
            .TakeWhile(c => c != '\0')
            .Where(c => !char.IsControl(c))
            .ToArray())
            .Trim();

        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static string FirstReadable(params string[] values)
    {
        return values.FirstOrDefault(value => value.Count(char.IsLetterOrDigit) >= 2) ?? "Unknown";
    }

    private static string FormatHex(uint value) => value == 0 ? "Unknown" : $"0x{value:X8}";

    private static string DescribeContentType(uint value) => value switch
    {
        0x00000001 => "Saved Game",
        0x00010000 => "Marketplace Content",
        0x00020000 => "Publisher",
        0x00030000 => "Xbox 360 Title",
        0x00090000 => "Avatar Item",
        0x000A0000 => "Profile",
        0x000B0000 => "Gamer Picture",
        0x000C0000 => "Theme",
        0x000D0000 => "Cache File",
        0x000F0000 => "Game Demo",
        0x00100000 => "Video",
        0x00200000 => "Game Title",
        0x00400000 => "Installer",
        0x02000000 => "Game on Demand",
        0x04000000 => "Avatar Asset Pack",
        0x40000000 => "Title Update",
        _ => "Unknown"
    };

    private sealed class VolumeDescriptor
    {
        public int Size { get; init; }
        public byte BlockSeparation { get; init; }
        public int FileTableBlockCount { get; init; }
        public int FileTableBlockNumber { get; init; }
        public int AllocatedBlockCount { get; init; }
    }

    private sealed class MutableFileEntry
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; init; }
        public bool IsConsecutive { get; init; }
        public int Blocks { get; init; }
        public int StartBlock { get; init; }
        public int PathIndicator { get; init; }
        public long Size { get; init; }
    }
}
