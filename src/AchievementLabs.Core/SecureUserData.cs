using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace AchievementLabs.Core;

/// <summary>User-bound protection: Windows DPAPI or macOS Keychain-backed AES-GCM.</summary>
public static class SecureUserData
{
    static readonly byte[] Magic = "ALMK1"u8.ToArray();
    public static byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Secure storage is unavailable.");
        var key=MacKeychain.ReadKey(true);
        try { var bytes=new byte[Magic.Length+28+plain.Length];Magic.CopyTo(bytes,0);RandomNumberGenerator.Fill(bytes.AsSpan(Magic.Length,12));using var aes=new AesGcm(key,16);aes.Encrypt(bytes.AsSpan(Magic.Length,12),plain,bytes.AsSpan(Magic.Length+28),bytes.AsSpan(Magic.Length+12,16),Magic);return bytes; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] Unprotect(byte[] bytes)
    {
        if (OperatingSystem.IsWindows()) return ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Secure storage is unavailable.");
        if(bytes.Length<Magic.Length+28||!bytes.AsSpan(0,Magic.Length).SequenceEqual(Magic))throw new CryptographicException("Invalid protected Mac data.");
        var key=MacKeychain.ReadKey(false);var plain=new byte[bytes.Length-Magic.Length-28];
        try { using var aes=new AesGcm(key,16);aes.Decrypt(bytes.AsSpan(Magic.Length,12),bytes.AsSpan(Magic.Length+28),bytes.AsSpan(Magic.Length+12,16),plain,Magic);return plain; }
        catch { CryptographicOperations.ZeroMemory(plain);throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    static class MacKeychain
    {
        const string Library="/System/Library/Frameworks/Security.framework/Security";
        // The standalone synthetic test executable must not create the real app's
        // Keychain item with an ACL belonging to the dotnet test host.
#if LABS_LAN_PAIRING_CHECKS
        const string ServiceName="org.achievementlabs.desktop.lan-pairing.synthetic-checks";
#else
        const string ServiceName="org.achievementlabs.desktop.encryption";
#endif
        static readonly byte[] Service=Encoding.UTF8.GetBytes(ServiceName),Account=Encoding.UTF8.GetBytes("storage-key-v1");
        static readonly object Gate=new();
        [DllImport(Library)] static extern int SecKeychainFindGenericPassword(IntPtr chain,uint sl,byte[] service,uint al,byte[] account,out uint length,out IntPtr data,out IntPtr item);
        [DllImport(Library)] static extern int SecKeychainAddGenericPassword(IntPtr chain,uint sl,byte[] service,uint al,byte[] account,uint length,byte[] data,out IntPtr item);
        [DllImport(Library)] static extern int SecKeychainItemFreeContent(IntPtr attributes,IntPtr data);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern void CFRelease(IntPtr item);
        internal static byte[] ReadKey(bool create)
        {
            lock(Gate)
            {
                var status=SecKeychainFindGenericPassword(IntPtr.Zero,(uint)Service.Length,Service,(uint)Account.Length,Account,out var length,out var data,out var item);
                try { if(status==0){if(length!=32||data==IntPtr.Zero)throw new CryptographicException("Invalid Keychain storage key.");var key=new byte[32];Marshal.Copy(data,key,0,32);return key;}if(status!=-25300||!create)throw new CryptographicException("macOS Keychain access failed (OSStatus "+status+")."); }
                finally { if(data!=IntPtr.Zero)SecKeychainItemFreeContent(IntPtr.Zero,data);if(item!=IntPtr.Zero)CFRelease(item); }
                item=IntPtr.Zero;var fresh=RandomNumberGenerator.GetBytes(32);
                try { status=SecKeychainAddGenericPassword(IntPtr.Zero,(uint)Service.Length,Service,(uint)Account.Length,Account,32,fresh,out item);if(status==-25299)return ReadKey(false);if(status!=0)throw new CryptographicException("macOS Keychain save failed (OSStatus "+status+").");return fresh.ToArray(); }
                finally { if(item!=IntPtr.Zero)CFRelease(item);CryptographicOperations.ZeroMemory(fresh); }
            }
        }
    }
}
