using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Reads data from a user-selected file. Never loads or executes Horizon code.</summary>
public static class Xbox360SigningSession
{
    public static void LoadHorizonFile(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("The selected executable exceeds the supported size.");
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false, InMemory = true });
        var config = assembly.MainModule.Types.SingleOrDefault(t => t.FullName == "Horizon.Server.Config");
        var load = config?.Methods.SingleOrDefault(m => m.Name == "loadConKeys");
        if (load?.HasBody != true) throw new InvalidDataException("This Horizon build has no supported local CON signing data.");
        var fields = load.Body.Instructions.Where(i => i.OpCode.Code == Code.Ldtoken && i.Operand is FieldReference)
            .Select(i => assembly.MainModule.LookupToken(((FieldReference)i.Operand).MetadataToken) as FieldDefinition).ToArray();
        int[] lengths = [128, 64, 64, 64, 64, 64, 424];
        if (fields.Length != lengths.Length || fields.Where((f, i) => f?.InitialValue.Length != lengths[i]).Any())
            throw new InvalidDataException("This Horizon signing layout is unsupported. Existing session data was retained.");
        try { Load(fields[6]!.InitialValue, fields[1]!.InitialValue, fields[2]!.InitialValue); }
        finally { foreach (var field in fields) CryptographicOperations.ZeroMemory(field!.InitialValue); }
    }
    public static void LoadKeyvault(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is not (0x4000 or 0x3FF0)) throw new InvalidDataException("Select a decrypted 16 KB console keyvault supported by Horizon.");
        var bytes = File.ReadAllBytes(path);
        try
        {
            var offset = length == 0x4000 ? 0x18 : 0x08;
            var certificate = bytes.AsSpan(offset + 0x9B0, 0x1A8).ToArray();
            var p = ReverseWords(bytes.AsSpan(offset + 0x280 + 0x90, 64));
            var q = ReverseWords(bytes.AsSpan(offset + 0x280 + 0xD0, 64));
            try { Load(certificate, p, q); }
            finally { CryptographicOperations.ZeroMemory(p); CryptographicOperations.ZeroMemory(q); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] ReverseWords(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i += 8) input.Slice(input.Length - i - 8, 8).CopyTo(output.AsSpan(i, 8));
        return output;
    }
    private static void Load(byte[] certificate, byte[] p, byte[] q)
    {
        Xbox360ConSigner.ValidateSigningMaterial(certificate, p, q);
        Xbox360ConSigningMaterial.Set(certificate, p, q);
    }
    private const string BundledIdentityName = "AchievementLabs.Xbox360.LocalConIdentity";
    public static bool HasBundledIdentity => typeof(Xbox360SigningSession).Assembly.GetManifestResourceInfo(BundledIdentityName) != null;
    public static bool LoadBundledIdentity()
    {
        using var stream = typeof(Xbox360SigningSession).Assembly.GetManifestResourceStream(BundledIdentityName);
        if (stream == null) return false;
        if (stream.Length != 556) throw new InvalidDataException("Invalid built-in signing identity.");
        var plain = new byte[556]; stream.ReadExactly(plain);
        try { LoadIdentity(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return true;
    }
    /// <summary>Local build preparation only. Keep this private build input outside source control.</summary>
    public static void WriteLocalBuildIdentity(string path)
    {
        var material = Xbox360ConSigningMaterial.Snapshot();
        var plain = new byte[556];
        try
        {
            "ALC1"u8.CopyTo(plain); material.Certificate.CopyTo(plain,4); material.P.CopyTo(plain,428); material.Q.CopyTo(plain,492);
            using var file = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); file.Write(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(material.P); CryptographicOperations.ZeroMemory(material.Q); }
    }
    private static void LoadIdentity(byte[] plain)
    {
        if (plain.Length != 556 || !plain.AsSpan(0,4).SequenceEqual("ALC1"u8)) throw new InvalidDataException("Invalid signing identity.");
        var p = plain.AsSpan(428,64).ToArray(); var q = plain.AsSpan(492,64).ToArray();
        try { Load(plain.AsSpan(4,424).ToArray(),p,q); }
        finally { CryptographicOperations.ZeroMemory(p); CryptographicOperations.ZeroMemory(q); }
    }
    // DPAPI binds the saved identity to the current Windows account, outside the app ZIP.
    private static readonly byte[] StoreEntropy = "AchievementLabs.Xbox360.Signing.v1"u8.ToArray();
    public static void Remember(string path)
    {
        var material = Xbox360ConSigningMaterial.Snapshot();
        var plain = new byte[4 + 424 + 64 + 64];
        byte[] encrypted;
        try
        {
            "ALC1"u8.CopyTo(plain);
            material.Certificate.CopyTo(plain, 4); material.P.CopyTo(plain, 428); material.Q.CopyTo(plain, 492);
            encrypted = ProtectedData.Protect(plain, StoreEntropy, DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(material.P); CryptographicOperations.ZeroMemory(material.Q); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Restore(string path)
    {
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Invalid saved signing identity.");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), StoreEntropy, DataProtectionScope.CurrentUser);
        try
        {
            LoadIdentity(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Clear() => Xbox360ConSigningMaterial.Clear();
}
