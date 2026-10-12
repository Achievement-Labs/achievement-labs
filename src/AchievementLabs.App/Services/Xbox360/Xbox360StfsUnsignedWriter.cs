using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

/// <summary>
/// Builds a locally modified STFS package with consistent block and header hashes.
/// Its original CON signature is intentionally invalid until a trusted signer replaces it.
/// </summary>
public sealed class Xbox360StfsUnsignedWriter(Xbox360PackageService packages)
{
    private const int BlockSize = 0x1000;

    public void Write(string source, string destination, IReadOnlyDictionary<string, byte[]> replacements)
    {
        if (replacements.Count == 0) throw new ArgumentException("No GPD replacements were supplied.", nameof(replacements));
        if (File.Exists(destination)) throw new IOException("The unsigned destination already exists.");
        var info = packages.InspectPackage(source);
        if (info.Magic != "CON ") throw new InvalidDataException("Only a CON profile can be edited.");
        var plans = new List<(string Path, byte[] Data, int[] Blocks)>();
        using (var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Span<byte> header = stackalloc byte[0x380];
            input.ReadExactly(header);
            var headerSize = (header[0x340] << 24) | (header[0x341] << 16) | (header[0x342] << 8) | header[0x343];
            if (headerSize != 0x971A) throw new NotSupportedException("This STFS layout is not yet supported for writing.");
            foreach (var replacement in replacements)
            {
                var entry = info.Entries.SingleOrDefault(candidate => !candidate.IsDirectory &&
                    candidate.Path.Equals(replacement.Key, StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new FileNotFoundException("The GPD entry is missing from the profile.", replacement.Key);
                if (entry.Size != replacement.Value.Length)
                    throw new InvalidDataException("GPD replacement must preserve its original byte length.");
                var blocks = new int[(replacement.Value.Length + BlockSize - 1) / BlockSize];
                var block = entry.StartBlock;
                for (var index = 0; index < blocks.Length; index++)
                {
                    if (block < 0 || block >= 0x70E4 || blocks.AsSpan(0, index).Contains(block))
                        throw new InvalidDataException("The GPD block chain is invalid or unsupported.");
                    blocks[index] = block;
                    if (index + 1 < blocks.Length)
                        block = Xbox360PackageService.ReadNextBlock(input, block, headerSize, header[0x37B], "CON ");
                }
                plans.Add((replacement.Key, replacement.Value, blocks));
            }
        }

        File.Copy(source, destination);
        try
        {
            using (var output = File.Open(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Span<byte> topHash = stackalloc byte[20];
                output.Position = 0x381;
                output.ReadExactly(topHash);
                var levelOne = SelectTable(output, 0xB6000, topHash);
                var changedGroups = new Dictionary<int, long>();
                foreach (var plan in plans)
                {
                    for (var index = 0; index < plan.Blocks.Length; index++)
                    {
                        var blockNumber = plan.Blocks[index];
                        var group = blockNumber / 0xAA;
                        if (!changedGroups.TryGetValue(group, out var levelZero))
                        {
                            Span<byte> expected = stackalloc byte[20];
                            output.Position = levelOne + group * 0x18L;
                            output.ReadExactly(expected);
                            levelZero = SelectTable(output, LevelZeroFirstOffset(blockNumber), expected);
                            changedGroups.Add(group, levelZero);
                        }
                        var offset = Xbox360PackageService.BlockToOffset(blockNumber, 0x971A, 0, "CON ");
                        var block = new byte[BlockSize];
                        output.Position = offset;
                        output.ReadExactly(block);
                        var sourceOffset = index * BlockSize;
                        plan.Data.AsSpan(sourceOffset, Math.Min(BlockSize, plan.Data.Length - sourceOffset)).CopyTo(block);
                        output.Position = offset;
                        output.Write(block);
                        var hash = SHA1.HashData(block);
                        output.Position = levelZero + (blockNumber % 0xAA) * 0x18L;
                        output.Write(hash);
                    }
                }
                foreach (var (group, levelZero) in changedGroups)
                {
                    var hash = HashTable(output, levelZero);
                    output.Position = levelOne + group * 0x18L;
                    output.Write(hash);
                }
                var newTopHash = HashTable(output, levelOne);
                output.Position = 0x381;
                output.Write(newTopHash);
                var headerTail = new byte[0xA000 - 0x344];
                output.Position = 0x344;
                output.ReadExactly(headerTail);
                output.Position = 0x32C;
                output.Write(SHA1.HashData(headerTail));
            }
            foreach (var plan in plans)
                if (!packages.ReadEntryBytes(destination, plan.Path).SequenceEqual(plan.Data))
                    throw new IOException("The unsigned STFS GPD did not verify after writing.");
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    private static long LevelZeroFirstOffset(int blockNumber)
    {
        var tableBlock = (blockNumber / 0xAA) * 0xAC - 2;
        if (blockNumber >= 0xAA) tableBlock += (blockNumber / 0x70E4 + 1) * 2;
        return 0xC000L + tableBlock * BlockSize;
    }

    private static long SelectTable(Stream stream, long firstOffset, ReadOnlySpan<byte> expectedHash)
    {
        var table = new byte[BlockSize];
        for (var copy = 0; copy < 2; copy++)
        {
            var offset = firstOffset + copy * BlockSize;
            if (offset < 0 || offset + BlockSize > stream.Length) continue;
            stream.Position = offset;
            stream.ReadExactly(table);
            if (SHA1.HashData(table).AsSpan().SequenceEqual(expectedHash)) return offset;
        }
        throw new InvalidDataException("The active STFS hash table could not be verified.");
    }

    private static byte[] HashTable(Stream stream, long offset)
    {
        var table = new byte[BlockSize];
        stream.Position = offset;
        stream.ReadExactly(table);
        return SHA1.HashData(table);
    }
}
