using System.Runtime.InteropServices;

namespace AchievementLabs.Desktop;

// Windows-only global hotkey (Alt+U). Runs a dedicated message loop thread so the
// registration does not depend on the Avalonia window having focus. While the overlay
// is open, Esc is also registered so it can close the overlay without the overlay ever
// taking keyboard focus from the game.
internal sealed class OverlayHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, WM_APP_ESCAPE = 0x8000 + 0x4C;
    private const int MOD_ALT = 0x0001, MOD_NOREPEAT = 0x4000, VK_U = 0x55, VK_ESCAPE = 0x1B;
    private const int ToggleId = 0x4C41, EscapeId = 0x4C42;
    private readonly Action toggle, escape;
    private Thread? thread;
    private uint threadId;

    public bool Registered { get; private set; }

    private OverlayHotkey(Action toggle, Action escape) { this.toggle = toggle; this.escape = escape; }

    public static OverlayHotkey? TryStart(Action toggle, Action escape)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var hotkey = new OverlayHotkey(toggle, escape);
        using var ready = new ManualResetEventSlim();
        hotkey.thread = new Thread(() => hotkey.Run(ready)) { IsBackground = true, Name = "Overlay hotkey" };
        hotkey.thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        return hotkey;
    }

    // Esc is only claimed while the overlay is visible, so games get it back afterwards.
    public void SetEscapeEnabled(bool enabled)
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_APP_ESCAPE, enabled ? 1 : 0, IntPtr.Zero);
    }

    private void Run(ManualResetEventSlim ready)
    {
        threadId = GetCurrentThreadId();
        Registered = RegisterHotKey(IntPtr.Zero, ToggleId, MOD_ALT | MOD_NOREPEAT, VK_U);
        OverlayLog.Write(Registered ? "Alt+U registered" : $"Alt+U registration failed (error {Marshal.GetLastWin32Error()})");
        ready.Set();
        if (!Registered) return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_ESCAPE)
                {
                    if (msg.wParam != 0) RegisterHotKey(IntPtr.Zero, EscapeId, MOD_NOREPEAT, VK_ESCAPE);
                    else UnregisterHotKey(IntPtr.Zero, EscapeId);
                }
                else if (msg.message == WM_HOTKEY && (int)msg.wParam == ToggleId) { OverlayLog.Write("Alt+U pressed"); toggle(); }
                else if (msg.message == WM_HOTKEY && (int)msg.wParam == EscapeId) escape();
            }
        }
        finally { UnregisterHotKey(IntPtr.Zero, ToggleId); UnregisterHotKey(IntPtr.Zero, EscapeId); }
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
