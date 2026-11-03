using System.Security.Cryptography;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Signs with the bundled 556-byte identity only, matching the mobile apps' signing.dat. There is no user setup.</summary>
public static class Xbox360SigningSession
{
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
    private static void LoadIdentity(byte[] plain)
    {
        if (plain.Length != 556 || !plain.AsSpan(0,4).SequenceEqual("ALC1"u8)) throw new InvalidDataException("Invalid signing identity.");
        var certificate = plain.AsSpan(4,424).ToArray(); var p = plain.AsSpan(428,64).ToArray(); var q = plain.AsSpan(492,64).ToArray();
        try
        {
            Xbox360ConSigner.ValidateSigningMaterial(certificate, p, q);
            Xbox360ConSigningMaterial.Set(certificate, p, q);
        }
        finally { CryptographicOperations.ZeroMemory(p); CryptographicOperations.ZeroMemory(q); }
    }
    public static void Clear() => Xbox360ConSigningMaterial.Clear();
}
