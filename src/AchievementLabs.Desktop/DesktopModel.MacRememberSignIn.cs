using AchievementLabs.Core;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private async Task ConnectMacAsync()
    {
        PrepareMacSignIn();
        if (CanConnect && RememberBrowserSession && File.Exists(NativeSessionStore.DefaultPath))
        {
            SessionPath = NativeSessionStore.DefaultPath;
            await ConnectSavedOAuthFallbackAsync();
            if (session != null) return;
        }
        await StartBrowserLoginAsync();
    }

    public async Task ApplyMacRememberSignInAsync()
    {
        if (!AchievementLabs.Core.DesktopPlatform.IsUnixDesktop || !CanInteract) return;
        Busy(true);
        var locked = false;
        try
        {
            await authorizationRefresh.WaitAsync(lifetime.Token); locked = true;
            if (RememberBrowserSession && session?.OAuthResponse != null)
            {
                await NativeSessionStore.SaveAsync(NativeSessionStore.DefaultPath,
                    new(session.OAuthProfileName ?? activeOAuthProfile.Name, session.OAuthResponse), lifetime.Token);
                SessionPath = NativeSessionStore.DefaultPath;
                activeOAuthSessionPath = NativeSessionStore.DefaultPath;
            }
            else if (!RememberBrowserSession)
            {
                activeOAuthSessionPath = null;
                if (File.Exists(NativeSessionStore.DefaultPath)) File.Delete(NativeSessionStore.DefaultPath);
            }
            await SavePreferencesAsync();
            if (Notice.StartsWith("Could not save", StringComparison.Ordinal)) return;
            Notice = RememberBrowserSession
                ? session?.OAuthResponse != null ? "Microsoft sign-in saved securely on this desktop. Reconnection will use it automatically." : "Remember sign-in enabled for your next Microsoft sign-in."
                : "Remember sign-in disabled and this app's saved Microsoft sign-in removed. The current session stays connected.";
        }
        catch (OperationCanceledException) { }
        catch { Notice = "Could not change the saved sign-in. Check macOS Keychain access and try again."; }
        finally { if (locked) authorizationRefresh.Release(); Busy(false); }
    }
}
