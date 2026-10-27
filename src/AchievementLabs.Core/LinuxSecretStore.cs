using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AchievementLabs.Core;

// A random encryption key lives in Secret Service, never beside ciphertext.
internal static class LinuxSecretStore
{
    const string Lib = "libsecret-1.so.0";
#if LABS_LAN_PAIRING_CHECKS
    const string Service = "org.achievementlabs.desktop.synthetic-checks";
#else
    const string Service = "org.achievementlabs.desktop.encryption";
#endif
    [StructLayout(LayoutKind.Sequential)] struct Attribute { public IntPtr Name; public int Type; }
    [StructLayout(LayoutKind.Sequential)] struct Schema
    {
        public IntPtr Name;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public Attribute[] Attributes;
        public int Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public IntPtr[] ReservedPointers;
    }
    // Fixed string attributes followed by a null sentinel map the C varargs API.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr secret_password_lookup_sync(IntPtr schema, IntPtr cancel, out IntPtr error, string a1, string v1, string a2, string v2, IntPtr end);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int secret_password_store_sync(IntPtr schema, string collection, string label, string password, IntPtr cancel, out IntPtr error, string a1, string v1, string a2, string v2, IntPtr end);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern void secret_password_free(IntPtr password);
    [DllImport("libglib-2.0.so.0")] static extern void g_error_free(IntPtr error);

    internal static byte[] ReadKey(bool create)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var gate = new Mutex(false, "AchievementLabs.SecretService.StorageKey.v1");
        bool entered;
        try { entered = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new CryptographicException("Secure storage is busy.");
        var allocated = new List<IntPtr>();
        IntPtr Utf8(string value) { var p = Marshal.StringToCoTaskMemUTF8(value); allocated.Add(p); return p; }
        var schema = new Schema { Name = Utf8("org.achievementlabs.StorageKey"), Attributes = new Attribute[32], ReservedPointers = new IntPtr[7] };
        schema.Attributes[0] = new() { Name = Utf8("service") };
        schema.Attributes[1] = new() { Name = Utf8("account") };
        var native = Marshal.AllocHGlobal(Marshal.SizeOf<Schema>());
        try
        {
            Marshal.StructureToPtr(schema, native, false);
            var secret = secret_password_lookup_sync(native, IntPtr.Zero, out var error, "service", Service, "account", "storage-key-v1", IntPtr.Zero);
            try
            {
                Check(error);
                if (secret != IntPtr.Zero)
                {
                    var key = Convert.FromBase64String(Marshal.PtrToStringUTF8(secret) ?? "");
                    if (key.Length == 32) return key;
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException("Invalid Secret Service encryption key.");
                }
            }
            finally { if (secret != IntPtr.Zero) secret_password_free(secret); }
            if (!create) throw new CryptographicException("Secret Service encryption key is missing.");
            var fresh = RandomNumberGenerator.GetBytes(32);
            try
            {
                var saved = secret_password_store_sync(native, "default", "Achievement Labs encryption", Convert.ToBase64String(fresh), IntPtr.Zero, out error, "service", Service, "account", "storage-key-v1", IntPtr.Zero);
                Check(error);
                if (saved == 0) throw new CryptographicException("Secret Service could not save the encryption key.");
                return fresh.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(fresh); }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
            foreach (var p in allocated) Marshal.FreeCoTaskMem(p);
            gate.ReleaseMutex();
        }
    }
    static void Check(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        g_error_free(error);
        throw new CryptographicException("Linux Secret Service is unavailable or locked. Unlock your desktop keyring and retry.");
    }
}
