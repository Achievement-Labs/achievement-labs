namespace AchievementLabs.Services.Xbox360;

// Public checkouts omit the private signing dependency. Never invent or package a key.
internal static class Xbox360ConSigningMaterial
{
    private static byte[] Missing() => throw new NotSupportedException("Xbox 360 profile signing is unavailable in this public source build. No profile was modified.");
    public static byte[] Certificate => Missing();
    public static byte[] PrimeP => Missing();
    public static byte[] PrimeQ => Missing();
}
