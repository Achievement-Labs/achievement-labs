using System.Diagnostics;
using System.Text.Json;

namespace AchievementLabs.Desktop;

internal static class MacMicrosoftSignIn
{
    public static async Task<string> CaptureAsync(Uri startUri, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing app executable.");
        var helper = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable)!, "..", "Helpers", "MicrosoftSignIn"));
        if (!File.Exists(helper)) throw new FileNotFoundException("The Mac sign-in helper is missing. Install the complete app bundle.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var process = new Process { StartInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        }};
        process.Start();
        try
        {
            // Drain native WebKit diagnostics without retaining or displaying them.
            var discardErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { startUri = startUri.AbsoluteUri }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var buffer = new char[16385]; var used = 0;
            while (used < buffer.Length)
            {
                var read = await process.StandardOutput.ReadAsync(buffer.AsMemory(used), timeout.Token);
                if (read == 0) break;
                used += read;
            }
            if (used == buffer.Length) throw new InvalidDataException("Invalid Mac sign-in response.");
            await process.WaitForExitAsync(timeout.Token);
            await discardErrors;
            if (process.ExitCode != 0) throw new InvalidDataException("Mac sign-in window did not finish.");
            using var result = JsonDocument.Parse(new string(buffer, 0, used));
            var status = result.RootElement.GetProperty("status").GetString();
            if (status is "cancelled" or "timeout") throw new OperationCanceledException("Mac sign-in cancelled.");
            if (status != "completed") throw new InvalidDataException("Microsoft sign-in did not return a valid callback. Start a new sign-in.");
            return result.RootElement.GetProperty("redirect").GetString() ?? throw new InvalidDataException("Missing sign-in callback.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
}
