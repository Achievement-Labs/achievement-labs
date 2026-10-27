using System.Security.Cryptography;
using AchievementLabs.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async Task RunLinuxSmokeAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var output = Path.GetFullPath(args.SkipWhile(a => a != "--output").Skip(1).First());
        Directory.CreateDirectory(output);
        try
        {
            if (!OperatingSystem.IsLinux() || model.HasWindowsLocalTools || !model.IsPortablePlatform || model.IsWindowsPlatform)
                throw new Exception("Linux platform capabilities are incorrect.");
            await model.AttachXboxPcAppAsync();
            if (!model.OAuthFallbackAvailable || model.ConnectActionLabel != "Sign in with Microsoft") throw new Exception("Linux sign-in entry unavailable.");
            var plain = "synthetic-linux-storage-test"u8.ToArray();
            var encrypted = SecureUserData.Protect(plain);
            if (encrypted.AsSpan().IndexOf(plain) >= 0 || !SecureUserData.Unprotect(encrypted).SequenceEqual(plain)) throw new Exception("Secret Service round trip failed.");
            encrypted[^1] ^= 1;
            try { SecureUserData.Unprotect(encrypted); throw new Exception("Tampered ciphertext accepted."); }
            catch (AuthenticationTagMismatchException) { }
            await MacRememberSignInChecks.RunAsync(model);
            await MacEventTokenChecks.RunAsync(); // Fake HTTP transport; no Microsoft/Xbox requests.
            PcPresenceChecks.Run();
            using var updaterCheck = new AppUpdater();
            if (await updaterCheck.CheckAsync(new Version(0, 0, 0), CancellationToken.None) != null) throw new Exception("Windows updater exposed on Linux.");
            foreach (var page in new[] { "Home", "Library", "Queues", "Xbox360Profiles", "Settings" })
            {
                model.Navigate(page); await Task.Delay(300);
                var root = this.FindControl<Grid>("PreviewRoot")!;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Vector(96, 96));
                bitmap.Render(root);
                bitmap.Save(Path.Combine(output, "linux-" + page.ToLowerInvariant() + ".png"), new PngBitmapEncoderOptions());
            }
            File.WriteAllText(Path.Combine(output, "linux-smoke.txt"), "PASS: native Linux startup and page rendering; Windows controls hidden; sign-in entry; real Secret Service AES-GCM round trip/tamper rejection; synthetic event exchange and PC transfer checks; Windows update selection blocked. No account attachment, presence heartbeat or unlock requests.");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "linux-smoke-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
