using System.Diagnostics;
using System.Text.Json;

namespace AchievementLabs.Desktop;

internal static class LinuxMicrosoftSignIn
{
    public static async Task<string> CaptureAsync(Uri uri, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var helper = Path.Combine(AppContext.BaseDirectory, "Helpers", "microsoft-signin.py");
        if (!File.Exists(helper)) throw new FileNotFoundException("Reinstall the complete Linux package.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var process = new Process { StartInfo = new("/usr/bin/python3") {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(helper);
        process.Start();
        try
        {
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { startUri = uri.AbsoluteUri }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var chars = new char[16385]; var count = 0;
            while (count < chars.Length)
            {
                var n = await process.StandardOutput.ReadAsync(chars.AsMemory(count), timeout.Token);
                if (n == 0) break;
                count += n;
            }
            if (count == chars.Length) throw new InvalidDataException("Invalid sign-in helper response.");
            await process.WaitForExitAsync(timeout.Token); await errors;
            if (process.ExitCode != 0) throw new InvalidDataException("Linux sign-in needs Python GObject and WebKitGTK 4.1. Check the package instructions.");
            using var data = JsonDocument.Parse(new string(chars, 0, count));
            if (data.RootElement.GetProperty("status").GetString() != "completed") throw new OperationCanceledException("Sign-in cancelled.");
            return data.RootElement.GetProperty("redirect").GetString() ?? throw new InvalidDataException();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
