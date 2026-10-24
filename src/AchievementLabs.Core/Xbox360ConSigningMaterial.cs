namespace AchievementLabs.Services.Xbox360;

// Session material comes from a user import or an explicitly supplied local-build resource.
// Raw signing build inputs are never checked into source control.
internal static class Xbox360ConSigningMaterial
{
    private static readonly object Gate = new();
    private static (byte[] Certificate, byte[] P, byte[] Q)? material;
    internal static bool Available { get { lock (Gate) return material.HasValue; } }
    internal static void Set(byte[] certificate, byte[] p, byte[] q)
    {
        lock (Gate) { Clear(); material = ((byte[])certificate.Clone(), (byte[])p.Clone(), (byte[])q.Clone()); }
    }
    internal static (byte[] Certificate, byte[] P, byte[] Q) Snapshot()
    {
        lock (Gate)
        {
            var value = material ?? throw new NotSupportedException("Load valid local signing data before saving. No profile was modified.");
            return ((byte[])value.Certificate.Clone(), (byte[])value.P.Clone(), (byte[])value.Q.Clone());
        }
    }
    internal static void Clear()
    {
        lock (Gate)
        {
            if (material is { } value) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(value.P); System.Security.Cryptography.CryptographicOperations.ZeroMemory(value.Q); }
            material = null;
        }
    }
}
