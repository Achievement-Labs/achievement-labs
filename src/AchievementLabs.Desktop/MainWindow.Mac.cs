using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AchievementLabs.Core;
using AchievementLabs.Services.Xbox360;
namespace AchievementLabs.Desktop;
public partial class MainWindow
{
    private void ApplyMacPlatformControls()
    {
        if(!AchievementLabs.Core.DesktopPlatform.IsUnixDesktop)return;
        foreach(var text in this.GetLogicalDescendants().OfType<TextBlock>().Where(t=>t.Text is "STEAM" or "EPIC GAMES" or "UBISOFT CONNECT"))
        { var card=text.GetLogicalAncestors().OfType<Border>().FirstOrDefault(b=>b.Classes.Contains("panel"));if(card!=null)card.IsVisible=false; }
        foreach(var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if(button.Content is string content)
            {
                if(content is "Retry attachment")button.Content="Sign in with Microsoft";
                if(content.Contains("Windows account",StringComparison.OrdinalIgnoreCase))button.IsVisible=false;
            }
        }
        foreach(var toggle in this.GetLogicalDescendants().OfType<CheckBox>())
            if(toggle.Content is string content && (content.Contains("Xbox app",StringComparison.OrdinalIgnoreCase)||content.Contains("launch hidden",StringComparison.OrdinalIgnoreCase)))toggle.IsVisible=false;
    }
    private async Task RunMacSmokeAsync()
    {
        var args=Environment.GetCommandLineArgs();var output=Path.GetFullPath(args.SkipWhile(a=>a!="--output").Skip(1).First());Directory.CreateDirectory(output);
        try
        {
            await Task.Delay(100); // Let the native desktop lifetime finish opening before testing/closing.
            var expected=args.Contains("--mac-x64-package-check") ? System.Runtime.InteropServices.Architecture.X64 : System.Runtime.InteropServices.Architecture.Arm64;
            if(!OperatingSystem.IsMacOS()||System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture!=expected)throw new Exception("Expected native Mac runtime architecture: "+expected);
            if(model.HasWindowsLocalTools)throw new Exception("Windows capabilities exposed on Mac.");
            await model.AttachXboxPcAppAsync();if(!model.OAuthFallbackAvailable||!model.ConnectActionLabel.Contains("Microsoft"))throw new Exception("Mac sign-in preparation failed.");
            if (this.FindControl<Button>("ConnectAccountAction")?.Content?.ToString() != model.ConnectActionLabel
                || this.FindControl<Button>("InstallUpdateAction")?.Content?.ToString() != model.UpdateActionLabel)
                throw new Exception("Native account/update actions show Windows-specific labels.");
            if (BrowserXboxLogin.ValidateRedirect("https://login.live.com/oauth20_desktop.srf?code=synthetic&state=expected", "expected") != "synthetic") throw new Exception("Callback parsing failed.");
            foreach (var invalid in new[] {
                "https://login.live.com/oauth20_desktop.srf?removed=true",
                "https://login.live.com/oauth20_desktop.srf?code=synthetic&state=wrong",
                "https://login.live.com/oauth20_desktop.srf?code=synthetic&state=expected&state=expected",
                "https://login.live.com/oauth20_desktop.srf?code=synthetic&code=other&state=expected",
                "https://login.live.com/oauth20_desktop.srf?code=synthetic&state=expected&error=denied",
                "https://login.live.com/oauth20_desktop.srf?code=synthetic&state=expected#fragment",
                "https://example.com/oauth20_desktop.srf?code=synthetic&state=expected" })
            {
                try { BrowserXboxLogin.ValidateRedirect(invalid, "expected"); throw new Exception("Invalid callback accepted."); }
                catch (InvalidDataException) { }
            }
            var notificationStatus = await MacNotifications.StatusAsync();
            if (notificationStatus < 100 || notificationStatus > 104) throw new Exception("Native Mac notification status query failed.");
            await MacEventTokenChecks.RunAsync();
            PcPresenceChecks.Run();
            if (this.FindControl<CheckBox>("MacRememberSignInToggle")?.IsVisible != true) throw new Exception("Mac remember sign-in option is hidden.");
            await MacRememberSignInChecks.RunAsync(model);
            var syntheticExpiry = DateTimeOffset.UtcNow.AddMinutes(30);
            await EventTokenIssuerExpiry.SaveAsync("synthetic-event-token", syntheticExpiry, CancellationToken.None);
            if (await EventTokenIssuerExpiry.ReadAsync("synthetic-event-token", CancellationToken.None) != syntheticExpiry ||
                await EventTokenIssuerExpiry.ReadAsync("different-token", CancellationToken.None) != null)
                throw new Exception("Event issuer expiry was not bound to its exact token.");
            var sample=Encoding.UTF8.GetBytes("synthetic-macos-storage-test");var encrypted=SecureUserData.Protect(sample);
            if(encrypted.AsSpan().IndexOf(sample)>=0||!SecureUserData.Unprotect(encrypted).SequenceEqual(sample))throw new Exception("Keychain storage round trip failed.");
            encrypted[^1]^=1;try{SecureUserData.Unprotect(encrypted);throw new Exception("Tampered storage accepted.");}catch(AuthenticationTagMismatchException){}
            if(!Xbox360SigningSession.LoadBundledIdentity()||!Xbox360ConSigner.SigningAvailable)throw new Exception("Bundled signing identity did not load.");
            var fixture=Path.GetFullPath(args.SkipWhile(a=>a!="--fixture").Skip(1).First());var originalHash=SHA256.HashData(File.ReadAllBytes(fixture));
            var packages=new Xbox360PackageService();var signed=Path.Combine(output,Convert.ToHexString(File.ReadAllBytes(fixture).AsSpan(0x371,8)));
            new Xbox360StfsSignedWriter(new Xbox360StfsUnsignedWriter(packages)).Write(fixture,signed,new Dictionary<string,byte[]>{{"FFFE07D1.gpd",packages.ReadEntryBytes(fixture,"FFFE07D1.gpd")}});
            if(!Xbox360ConSigner.VerifyFile(signed))throw new Exception("Synthetic signed profile verification failed.");
            var profiles=new Xbox360ProfileService(Path.Combine(output,"working-profiles"));var working=await profiles.ExtractProfileAsync(signed);
            var reader=new Xbox360ProfileReader(packages);var game=reader.ReadGames(working).First();var achievement=reader.ReadAchievements(working,game.TitleId).First(a=>(a.Flags&0x20000)==0);
            new Xbox360ProfileEditService(packages,profiles).Save(working,game.TitleId,new[]{achievement with{Flags=achievement.Flags|0x20000,UnlockedAt=null}});
            if(!Xbox360ConSigner.VerifyFile(working)||!reader.ReadAchievements(working,game.TitleId).Any(a=>a.Id==achievement.Id&&(a.Flags&0x20000)!=0)||!SHA256.HashData(File.ReadAllBytes(fixture)).SequenceEqual(originalHash))throw new Exception("Synthetic edit/sign or original preservation failed.");
            var root=this.FindControl<Grid>("PreviewRoot")!;
            foreach(var page in new[]{"Home","Library","Queues","Xbox360Profiles","Settings"})
            {
                model.Navigate(page);await Task.Delay(350);using var bitmap=new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width,(int)root.Bounds.Height),new Vector(96,96));bitmap.Render(root); // Validate rendering without exporting any image.
            }
            File.WriteAllText(Path.Combine(output,"mac-smoke.txt"),"PASS: native "+System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture+" UI startup; Microsoft sign-in entry and callback validation; visible remember choice, persisted opt-in/opt-out, Keychain-protected synthetic sign-in and removal; native notification bridge status query (no permission prompt or delivery); mobile-style event exchange with fake transport and account/expiry rejection; Keychain-backed AES-GCM round trip/tamper rejection; synthetic Xbox 360 working-copy edit and signed export verified; Home, Library, Game Session, Xbox 360 and Settings render; no account or event requests.");
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"mac-smoke-error.txt"),ex.ToString());Environment.ExitCode=1;}
        finally{Close();}
    }
}
