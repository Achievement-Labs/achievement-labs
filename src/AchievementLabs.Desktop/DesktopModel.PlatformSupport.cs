namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    public bool HasWindowsLocalTools => OperatingSystem.IsWindows();
    public string ConnectActionLabel => OperatingSystem.IsMacOS() ? "Sign in with Microsoft" : "Attach Xbox PC app";
    public string UpdateActionLabel => OperatingSystem.IsMacOS() ? "Open downloaded Mac update" : "Install update and restart";
    public bool OAuthManualRedirectAvailable => !OperatingSystem.IsMacOS();
    public string OAuthSectionTitle => OperatingSystem.IsMacOS() ? "Microsoft sign-in" : "Microsoft OAuth fallback";
    public string OAuthSignInDescription => OperatingSystem.IsMacOS()
        ? "Sign in in the Microsoft window. Achievement Labs connects automatically when you finish. You can also use a protected saved sign-in."
        : "Use Microsoft browser sign-in when Xbox PC app attachment is unavailable. Try a protected saved sign-in first. For a new sign-in, copy the final login.live.com address and use the single paste-and-finish button.";
    public string OAuthStartLabel => OperatingSystem.IsMacOS() ? "Sign in with Microsoft" : "Open Microsoft sign-in";
    public string SavedSignInLabel => OperatingSystem.IsMacOS() ? "Use saved Microsoft sign-in" : "Use saved OAuth fallback";
    public string RememberOAuthLabel => OperatingSystem.IsMacOS() ? "Remember this sign-in securely on this Mac" : "Remember this fallback sign-in for this Windows user";
    public string PlatformUserLabel => OperatingSystem.IsMacOS() ? "Mac user" : "Windows user";
    public string PlatformAccountLabel => OperatingSystem.IsMacOS() ? "Mac account" : "Windows account";
    public string NotificationsLabel => OperatingSystem.IsMacOS() ? "Mac notifications" : "Windows notifications";
    public string NotificationTestLabel => OperatingSystem.IsMacOS() ? "Test Mac notification" : "Test Windows notification";
    public string EventTokenRetrievalLabel => OperatingSystem.IsMacOS() ? "Get event token on this Mac" : "Retrieve event token for connected Windows account";
    public string EventTokenRetrievalHelp => OperatingSystem.IsMacOS()
        ? "Uses your Microsoft sign-in on this Mac and verifies the Xbox account before saving. Existing tokens are kept if retrieval fails. Event tokens do not grant title-spoofing permission."
        : "Retrieves an event token for the connected Xbox account using the Windows account broker. You can also paste a current token manually.";
    public string SigningSetupDescription => $"The bundled signing identity is ready. Optional local signing setup is remembered encrypted for your {PlatformAccountLabel}; selected executables are never launched.";
    private void PrepareMacSignIn()
    {
        XboxPcAppDetected=false;XboxPcAttached=false;OAuthFallbackAvailable=session==null;
        XboxPcAttachmentLabel=session==null?"Microsoft sign-in for Mac":"Connected on Mac";
        XboxPcAttachmentDetail="Sign in with Microsoft in the Mac sign-in window, or use a protected saved session.";
        if(session==null)Notice="Choose Sign in with Microsoft. The app will complete sign-in automatically.";
    }
}
