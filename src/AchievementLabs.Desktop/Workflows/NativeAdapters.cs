global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
namespace AchievementLabs.Desktop.Workflows;
public interface INativePageLifecycle { void OnNavigatedTo(); void OnNavigatedFrom(); }
public enum NoticeAppearance { Success, Danger, Caution, Secondary, Primary, Info }
public enum NoticeSymbol { ErrorCircle24, Checkmark24, CheckmarkCircle24, Warning24, Info24, Save24, Clipboard24 }
public sealed record NoticeIcon(NoticeSymbol Symbol);
public sealed class NativeNotices(Action<string> update)
{
    public void Show(string title, string message, NoticeAppearance appearance, NoticeIcon icon, TimeSpan duration) => Avalonia.Threading.Dispatcher.UIThread.Post(() => update(title + ": " + message));
}
public sealed class NativeAccountContext
{
    public string XAUTH { get; set; } = "";
    public string XUIDOnly { get; set; } = "";
    public string EventsToken { get; set; } = "";
    public XboxAuthNet.OAuth.MicrosoftOAuthResponse? LastOAuthResponse { get; set; }
    public bool InitComplete => XAUTH.Length > 0;
    public string SpoofedTitleID { get; set; } = "0";
    public int SpoofingStatus { get; set; }
    public NativeAccountSettings Settings { get; } = new();
    public string EventsDirectory { get; set; } = "";
    public Func<CancellationToken, Task>? EnsureAuthorizationAsync { get; set; }
    public Func<CancellationToken, Task>? EnsureEventTokenAsync { get; set; }
    public Action? EventTokenRejected { get; set; }
}
public sealed class NativeAccountSettings { public bool FakeSignatureEnabled { get; set; } }

public static class NativeClipboard
{
    public static Func<string, Task>? WriteAsync { get; set; }
    public static void SetText(string text) { if (WriteAsync != null) _ = WriteAsync(text); }
}
