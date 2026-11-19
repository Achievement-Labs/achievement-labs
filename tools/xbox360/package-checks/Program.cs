using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AchievementLabs.Services.Xbox360;

var root = Path.Combine(Path.GetTempPath(), "labs-stfs-root-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var count in new[] { 3, 0xAA, 0xAB })
    foreach (var copy in new[] { 0, 1 })
    {
        var bytes = new byte[count > 0xAA ? 0xB8000 : 0xF000];
        Encoding.ASCII.GetBytes("CON ").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x340), 0x971A);
        bytes[0x379] = 0x24;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x37C), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x395), (uint)count);
        Encoding.ASCII.GetBytes("sample.gpd").CopyTo(bytes, 0xC000);
        bytes[0xC028] = 10; // File name length.
        bytes[0xC029] = 1; // One data block.
        bytes[0xC02F] = 2; // Starts at block 2.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0xC034), 1);
        bytes[0xE000] = 42;
        var table = new byte[0x1000];
        for (int block = 0; block < 3; block++)
        {
            SHA1.HashData(bytes.AsSpan(0xC000 + block * 0x1000, 0x1000)).CopyTo(table.AsSpan(block * 0x18));
            table[block * 0x18 + 0x14] = 0x80;
            table[block * 0x18 + 0x15] = block == 0 ? (byte)0 : (byte)0xFF;
            table[block * 0x18 + 0x16] = block == 0 ? (byte)0 : (byte)0xFF;
            table[block * 0x18 + 0x17] = block == 0 ? (byte)1 : (byte)0xFF;
        }
        var offset = 0xA000 + copy * 0x1000;
        table.CopyTo(bytes, offset);
        var hash = SHA1.HashData(table);
        if (count > 0xAA)
        {
            var levelOne = new byte[0x1000]; hash.CopyTo(levelOne, 0);
            levelOne.CopyTo(bytes, 0xB6000 + copy * 0x1000);
            hash = SHA1.HashData(levelOne);
        }
        hash.CopyTo(bytes, 0x381);
        var path = Path.Combine(root, $"profile-{count}-{copy}");
        File.WriteAllBytes(path, bytes);
        var service = new Xbox360PackageService();
        var info = service.InspectPackage(path);
        if (info.Entries.Count != 1 || info.Entries[0].Name != "sample.gpd" || !service.ReadEntryBytes(path, "sample.gpd").SequenceEqual(new byte[] { 42 }))
            throw new Exception("Profile file-table chain was read incorrectly.");
        bytes[offset] ^= 1;
        File.WriteAllBytes(path, bytes);
        try { service.InspectPackage(path); throw new Exception("Corrupt root was accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine($"PASS {count} allocated blocks, active copy {copy}: chained file table and GPD read; corrupt hash rejected.");
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine("PASS: synthetic profiles only; no signing, profile writes, telemetry or unlocks.");
