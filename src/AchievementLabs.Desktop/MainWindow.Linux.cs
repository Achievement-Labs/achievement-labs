using System.Security.Cryptography;
using AchievementLabs.Core;
using AchievementLabs.Services.Xbox360;
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
            await Task.Delay(150); // Let the desktop lifetime finish opening before a test can close it.
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
            if (!Xbox360SigningSession.LoadBundledIdentity()) throw new Exception("Bundled signing resource did not load.");
            var workspace = Path.Combine(Path.GetTempPath(), "labs-linux-synthetic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try
            {
                var store = Path.Combine(workspace, "identity.protected");
                Xbox360SigningSession.Remember(store);
                Xbox360SigningSession.Clear(); Xbox360SigningSession.Restore(store);
                var fixture = Path.GetFullPath("tools/xbox360/shared/synthetic.dat");
                var originalHash = SHA256.HashData(File.ReadAllBytes(fixture));
                var packages = new Xbox360PackageService();
                var signed = Path.Combine(workspace, "signed-profile");
                new Xbox360StfsSignedWriter(new Xbox360StfsUnsignedWriter(packages)).Write(fixture, signed,
                    new Dictionary<string, byte[]> { ["FFFE07D1.gpd"] = packages.ReadEntryBytes(fixture, "FFFE07D1.gpd") });
                if (!Xbox360ConSigner.VerifyFile(signed) || !originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture))))
                    throw new Exception("Synthetic profile export or original preservation failed.");
            }
            finally { Directory.Delete(workspace, true); }
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
            File.WriteAllText(Path.Combine(output, "linux-smoke.txt"), "PASS: native Linux startup and page rendering; Windows controls hidden; sign-in entry; real Secret Service AES-GCM round trip/tamper rejection; saved-session opt-in/out; synthetic event exchange and PC transfer checks; protected signing identity round trip and signed synthetic profile export; Windows update selection blocked. No account attachment, presence heartbeat or unlock requests.");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "linux-smoke-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
