using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AchievementLabs.Desktop;

internal static class MacNotifications
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Reply(long identifier, int result);
    [DllImport("libLabsMacNotifications.dylib", EntryPoint = "LabsMacNotification", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Native([MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body, int explicitTest, int queryOnly, long identifier, Reply reply);
    private static long next;
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<int>> pending = new();
    private static readonly Reply reply = (id, result) => { if (pending.TryRemove(id, out var completion)) completion.TrySetResult(result); };
    public static Task<int> ShowAsync(string title, string body, bool explicitTest = false) => InvokeAsync(title, body, explicitTest, false);
    public static Task<int> StatusAsync() => InvokeAsync("", "", false, true);
    private static async Task<int> InvokeAsync(string title, string body, bool explicitTest, bool queryOnly)
    {
        if (!OperatingSystem.IsMacOS()) return 3;
        var id = Interlocked.Increment(ref next);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try { Native(title, body, explicitTest ? 1 : 0, queryOnly ? 1 : 0, id, reply); return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2)); }
        catch { return 3; }
        finally { pending.TryRemove(id, out _); }
    }
    public static string ResultMessage(int result) => result switch
    {
        0 => "Mac notification submitted. Banner visibility depends on macOS notification and Focus settings.",
        1 => "Mac notifications are denied. Enable Achievement Labs in System Settings → Notifications.",
        2 => "Use Test Mac notification in Settings to allow Mac notifications.",
        _ => "Mac notification could not be submitted. Check System Settings → Notifications for Achievement Labs."
    };
}
