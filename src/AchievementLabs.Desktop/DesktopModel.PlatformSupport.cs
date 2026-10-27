using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    public bool HasWindowsLocalTools => OperatingSystem.IsWindows();
    public string ConnectActionLabel => DesktopPlatform.IsUnixDesktop ? "Sign in with Microsoft" : "Attach Xbox PC app";
    public string UpdateActionLabel => DesktopPlatform.IsUnixDesktop ? "Open platform releases" : "Install update and restart";
    public bool OAuthManualRedirectAvailable => !DesktopPlatform.IsUnixDesktop;
    public string OAuthSectionTitle => DesktopPlatform.IsUnixDesktop ? "Microsoft sign-in" : "Microsoft OAuth fallback";
    public string OAuthSignInDescription => DesktopPlatform.IsUnixDesktop
        ? "Sign in in the Microsoft window. Achievement Labs connects automatically when you finish. You can also use a protected saved sign-in."
        : "Use Microsoft browser sign-in when Xbox PC app attachment is unavailable. Try a protected saved sign-in first. For a new sign-in, copy the final login.live.com address and use the single paste-and-finish button.";
    public string OAuthStartLabel => DesktopPlatform.IsUnixDesktop ? "Sign in with Microsoft" : "Open Microsoft sign-in";
    public string SavedSignInLabel => DesktopPlatform.IsUnixDesktop ? "Use saved Microsoft sign-in" : "Use saved OAuth fallback";
    public string RememberOAuthLabel => DesktopPlatform.IsUnixDesktop ? "Remember this sign-in securely on this device" : "Remember this fallback sign-in for this Windows user";
    public string PlatformUserLabel => DesktopPlatform.Name + " user";
    public string PlatformAccountLabel => DesktopPlatform.Name + " account";
    public string NotificationsLabel => DesktopPlatform.Name + " notifications";
    public string NotificationTestLabel => "Test " + DesktopPlatform.Name + " notification";
    public string EventTokenRetrievalLabel => DesktopPlatform.IsUnixDesktop ? "Get event token on this device" : "Retrieve event token for connected Windows account";
    public string EventTokenRetrievalHelp => DesktopPlatform.IsUnixDesktop
        ? "Uses your Microsoft sign-in on this device and verifies the Xbox account before saving. Existing tokens are kept if retrieval fails. Event tokens do not grant title-spoofing permission."
        : "Retrieves an event token for the connected Xbox account using the Windows account broker. You can also paste a current token manually.";
    public string SigningSetupDescription => $"The bundled signing identity is ready. Optional local signing setup is remembered encrypted for your {PlatformAccountLabel}; selected executables are never launched.";
    private void PrepareMacSignIn()
    {
        XboxPcAppDetected=false;XboxPcAttached=false;OAuthFallbackAvailable=session==null;
        XboxPcAttachmentLabel=session==null?"Microsoft sign-in for " + DesktopPlatform.Name:"Connected on " + DesktopPlatform.Name;
        XboxPcAttachmentDetail="Sign in with Microsoft in the sign-in window, or use a protected saved session.";
        if(session==null)Notice="Choose Sign in with Microsoft. The app will complete sign-in automatically.";
    }
}
