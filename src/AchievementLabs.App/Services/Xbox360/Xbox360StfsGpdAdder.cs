using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Adds one title GPD to a CON profile without changing existing file data.</summary>
public sealed class Xbox360StfsGpdAdder(Xbox360PackageService packages)
{
    private const int BlockSize = 0x1000;

    public void Write(string source, string destination, string titleId, byte[] gpd)
    {
        if (File.Exists(destination)) throw new IOException("The destination already exists.");
        if (titleId.Length != 8 || !titleId.All(Uri.IsHexDigit))
            throw new InvalidDataException("A hexadecimal Xbox 360 title ID is required.");
        if (gpd.Length < 24 || gpd.Length > 64 * 1024 * 1024 ||
            !gpd.AsSpan(0, 4).SequenceEqual("XDBF"u8))
            throw new InvalidDataException("The selected file is not an Xbox 360 GPD database.");
        var entryName = titleId.ToUpperInvariant() + ".gpd";
        var info = packages.InspectPackage(source);
        if (info.Magic != "CON ") throw new InvalidDataException("Only CON profiles can receive a GPD.");
        if (info.Entries.Any(item => item.Path.Equals(entryName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{entryName} is already in the profile.");

        File.Copy(source, destination);
        try
        {
            using var output = File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var header = new byte[0xA000];
            output.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x340, 4)) != 0x971A)
                throw new NotSupportedException("This profile's STFS layout is not supported for adding games.");
            var tableCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x37C, 2));
            var tableStart = Read24(header, 0x37E);
            var allocated = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0x395, 4));
            var unallocated = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0x399, 4));
            var blockCount = checked((gpd.Length + BlockSize - 1) / BlockSize);
            if (tableCount == 0 || tableStart < 0 || info.Entries.Count >= tableCount * 64)
                throw new InvalidDataException("The profile has no free file table entry.");
            if (allocated < 0 || unallocated < blockCount || allocated + blockCount > 0x70E4)
                throw new InvalidDataException("The profile has insufficient supported STFS blocks for this GPD.");

            Span<byte> topHash = stackalloc byte[20];
            header.AsSpan(0x381, 20).CopyTo(topHash);
            var levelOne = SelectTable(output, 0xB6000, topHash);
            var changedGroups = new Dictionary<int, long>();
            var existingGroups = (allocated + 0xA9) / 0xAA;

            for (var index = 0; index < blockCount; index++)
            {
                var blockNumber = allocated + index;
                var group = blockNumber / 0xAA;
                var levelZero = GetGroupTable(output, levelOne, group, existingGroups, changedGroups);
                var block = new byte[BlockSize];
                gpd.AsSpan(index * BlockSize, Math.Min(BlockSize, gpd.Length - index * BlockSize)).CopyTo(block);
                var offset = Xbox360PackageService.BlockToOffset(blockNumber, 0x971A, header[0x37B], "CON ");
                output.Position = offset;
                output.Write(block);
                output.Position = levelZero + (blockNumber % 0xAA) * 0x18L;
                output.Write(SHA1.HashData(block));
                output.WriteByte(0x80);
                Span<byte> next = stackalloc byte[3];
                var nextBlock = index + 1 < blockCount ? blockNumber + 1 : 0xFFFFFF;
                next[0] = (byte)(nextBlock >> 16);
                next[1] = (byte)(nextBlock >> 8);
                next[2] = (byte)nextBlock;
                output.Write(next);
            }

            var slot = info.Entries.Count;
            var tableBlock = tableStart;
            for (var index = 0; index < slot / 64; index++)
            {
                tableBlock = Xbox360PackageService.ReadNextBlock(output, tableBlock, 0x971A, header[0x37B], "CON ");
                if (tableBlock < 0) throw new InvalidDataException("The profile file table chain is truncated.");
            }
            var tableOffset = Xbox360PackageService.BlockToOffset(tableBlock, 0x971A, header[0x37B], "CON ");
            var tableData = new byte[BlockSize];
            output.Position = tableOffset;
            output.ReadExactly(tableData);
            var entry = tableData.AsSpan((slot % 64) * 0x40, 0x40);
            if (entry.IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("The next profile file table slot is occupied.");
            var template = info.Entries.FirstOrDefault(item => !item.IsDirectory && item.Name.EndsWith(".gpd", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The profile has no GPD entry to use as a file table template.");
            var templateBlock = tableStart;
            for (var index = 0; index < template.Index / 64; index++)
                templateBlock = Xbox360PackageService.ReadNextBlock(output, templateBlock, 0x971A, header[0x37B], "CON ");
            var templateOffset = Xbox360PackageService.BlockToOffset(templateBlock, 0x971A, header[0x37B], "CON ")
                + (template.Index % 64) * 0x40L;
            output.Position = templateOffset;
            output.ReadExactly(entry);
            entry[..0x28].Clear();
            Encoding.ASCII.GetBytes(entryName, entry);
            entry[0x28] = checked((byte)entryName.Length);
            Write24(entry.Slice(0x29, 3), blockCount);
            Write24(entry.Slice(0x2C, 3), blockCount);
            Write24(entry.Slice(0x2F, 3), allocated);
            entry[0x32] = 0xFF;
            entry[0x33] = 0xFF;
            BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(0x34, 4), checked((uint)gpd.Length));
            output.Position = tableOffset;
            output.Write(tableData);
            var tableGroup = tableBlock / 0xAA;
            var tableHashOffset = GetGroupTable(output, levelOne, tableGroup, existingGroups, changedGroups)
                + (tableBlock % 0xAA) * 0x18L;
            output.Position = tableHashOffset;
            output.Write(SHA1.HashData(tableData));

            foreach (var (group, levelZero) in changedGroups)
            {
                var hash = HashBlock(output, levelZero);
                output.Position = levelOne + group * 0x18L;
                output.Write(hash);
                if (group >= existingGroups)
                {
                    output.WriteByte(0x80);
                    output.Write([0xFF, 0xFF, 0xFF]);
                }
            }
            var newTopHash = HashBlock(output, levelOne);
            output.Position = 0x381;
            output.Write(newTopHash);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x395, 4), allocated + blockCount);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x399, 4), unallocated - blockCount);
            output.Position = 0x395;
            output.Write(header, 0x395, 8);
            var headerTail = new byte[0xA000 - 0x344];
            output.Position = 0x344;
            output.ReadExactly(headerTail);
            output.Position = 0x32C;
            output.Write(SHA1.HashData(headerTail));
            output.Flush();
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
        if (!packages.ReadEntryBytes(destination, entryName).SequenceEqual(gpd))
        {
            File.Delete(destination);
            throw new IOException("The new GPD did not verify after insertion.");
        }
    }

    private static long GetGroupTable(Stream stream, long levelOne, int group, int existingGroups,
        Dictionary<int, long> changed)
    {
        if (changed.TryGetValue(group, out var table)) return table;
        var first = LevelZeroFirstOffset(group);
        if (group >= existingGroups)
        {
            var empty = new byte[BlockSize];
            stream.Position = first;
            stream.Write(empty);
            stream.Write(empty);
            table = first;
        }
        else
        {
            Span<byte> expected = stackalloc byte[20];
            stream.Position = levelOne + group * 0x18L;
            stream.ReadExactly(expected);
            table = SelectTable(stream, first, expected);
        }
        changed.Add(group, table);
        return table;
    }

    private static long LevelZeroFirstOffset(int group) =>
        0xC000L + (group * 0xAC - 2 + (group > 0 ? 2 : 0)) * BlockSize;

    private static long SelectTable(Stream stream, long first, ReadOnlySpan<byte> expected)
    {
        for (var copy = 0; copy < 2; copy++)
        {
            var offset = first + copy * BlockSize;
            if (offset + BlockSize <= stream.Length && HashBlock(stream, offset).AsSpan().SequenceEqual(expected))
                return offset;
        }
        throw new InvalidDataException("The active STFS hash table could not be verified.");
    }

    private static byte[] HashBlock(Stream stream, long offset)
    {
        var block = new byte[BlockSize];
        stream.Position = offset;
        stream.ReadExactly(block);
        return SHA1.HashData(block);
    }

    private static int Read24(byte[] source, int offset) =>
        source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16;

    private static void Write24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }
}
