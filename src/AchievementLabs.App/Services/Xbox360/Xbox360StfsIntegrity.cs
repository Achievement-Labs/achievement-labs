using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Checks active data blocks against the STFS hash tree for the supported retail profile layout.</summary>
public static class Xbox360StfsIntegrity
{
    public static void VerifyFile(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[0x39D]; stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x395, 4));
        if (!header.AsSpan(0, 4).SequenceEqual("CON "u8) || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x340,4)) != 0x971A || count <= 0xAA || count > 0x70E4)
            throw new NotSupportedException("This STFS profile layout is not supported for verified writes.");
        var top = ReadTable(stream, 0xB6000, header.AsSpan(0x381, 20));
        var block = new byte[0x1000];
        for (var group = 0; group * 0xAA < count; group++)
        {
            var first = 0xA000L + group * 0xAC000L + (group > 0 ? 0x2000 : 0);
            var table = ReadTable(stream, first, top.AsSpan(group * 0x18, 20));
            for (var item = 0; item < 0xAA && group * 0xAA + item < count; item++)
            {
                // Free blocks have no active data to verify. Allocated and newly allocated blocks do.
                if ((table[item * 0x18 + 0x14] & 0x80) == 0) continue;
                stream.Position = Xbox360PackageService.BlockToOffset(group * 0xAA + item, 0x971A, header[0x37B], "CON ");
                stream.ReadExactly(block);
                if (!SHA1.HashData(block).AsSpan().SequenceEqual(table.AsSpan(item * 0x18,20)))
                    throw new InvalidDataException("An active STFS data block failed hash verification. Reload an intact profile backup.");
            }
        }
    }
    private static byte[] ReadTable(Stream stream, long first, ReadOnlySpan<byte> expected)
    {
        var table = new byte[0x1000];
        for (var copy = 0; copy < 2; copy++)
        {
            var offset = first + copy * 0x1000;
            if (offset < 0 || offset + table.Length > stream.Length) continue;
            stream.Position = offset; stream.ReadExactly(table);
            if (SHA1.HashData(table).AsSpan().SequenceEqual(expected)) return table;
        }
        throw new InvalidDataException("An active STFS hash table failed verification.");
    }
}
