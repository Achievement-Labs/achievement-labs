using AchievementLabs.Core;
using XboxAuthNet.OAuth;
using System.Text;

namespace AchievementLabs.Desktop;

internal static class MacRememberSignInChecks
{
    public static async Task RunAsync(DesktopModel model)
    {
        // This check writes a synthetic session only in an isolated hosted CI account.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || File.Exists(NativeSessionStore.DefaultPath))
            throw new InvalidOperationException("Remember-sign-in checks require a clean hosted CI account.");
        var preferences = new DesktopPreferencesStore(AchievementLabsPaths.LocalFile("native-settings.json"));
        try
        {
            model.RememberBrowserSession = true;
            await model.ApplyMacRememberSignInAsync();
            var savedChoice = await preferences.LoadAsync();
            if ((OperatingSystem.IsLinux() ? savedChoice.RememberLinuxSignIn : savedChoice.RememberMacSignIn) != true) throw new Exception("Remember choice was not saved.");
            model.RememberBrowserSession = false;
            await model.LoadPreferencesAsync();
            if (!model.RememberBrowserSession) throw new Exception("Remember choice was not restored.");
            var oauth = new MicrosoftOAuthResponse { AccessToken = "synthetic-access", RefreshToken = "synthetic-renewal", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) };
            await NativeSessionStore.SaveAsync(NativeSessionStore.DefaultPath, new("Xbox App PC", oauth));
            var read = await NativeSessionStore.ReadAsync(NativeSessionStore.DefaultPath);
            if (read.OAuth.RefreshToken != oauth.RefreshToken || read.ProfileName != "Xbox App PC" ||
                (await File.ReadAllBytesAsync(NativeSessionStore.DefaultPath)).AsSpan().IndexOf(Encoding.UTF8.GetBytes("synthetic-renewal")) >= 0)
                throw new Exception("Protected remembered session round trip failed.");
            model.RememberBrowserSession = false;
            await model.ApplyMacRememberSignInAsync();
            savedChoice = await preferences.LoadAsync();
            if (File.Exists(NativeSessionStore.DefaultPath) || (OperatingSystem.IsLinux() ? savedChoice.RememberLinuxSignIn : savedChoice.RememberMacSignIn) != false)
                throw new Exception("Remember opt-out did not remove the saved sign-in.");
            model.RememberBrowserSession = true;
            await model.LoadPreferencesAsync();
            if (model.RememberBrowserSession) throw new Exception("Remember opt-out was not restored.");
        }
        finally { if (File.Exists(NativeSessionStore.DefaultPath)) File.Delete(NativeSessionStore.DefaultPath); }
    }
}
