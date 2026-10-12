using System.Diagnostics;
using System.IO;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Gfwl;

public interface IGfwlAchievementManagerService
{
    GfwlBinaryStatus GetBinaryStatus();
    IReadOnlyList<GfwlProcessItem> GetCandidateProcesses(bool showAll = false);
    Task<GfwlInjectionResult> InjectAdapterAsync(GfwlProcessItem process, CancellationToken cancellationToken);
}

public sealed class GfwlAchievementManagerService : IGfwlAchievementManagerService
{
    public GfwlBinaryStatus GetBinaryStatus()
    {
        var root = ResolveBinaryRoot();
        var enginePath = Path.Combine(root, "engine.dll");
        var managerPath = Path.Combine(root, "gfwlam.exe");
        var readmePath = Path.Combine(root, "readme.txt");

        return new GfwlBinaryStatus(
            root,
            File.Exists(enginePath),
            File.Exists(managerPath),
            File.Exists(readmePath),
            enginePath,
            managerPath);
    }

    public IReadOnlyList<GfwlProcessItem> GetCandidateProcesses(bool showAll = false)
    {
        var results = new List<GfwlProcessItem>();

        foreach (var process in Process.GetProcesses().OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            bool hasXlive;
            string status;
            try
            {
                hasXlive = ProcessHasModule(process, "xlive.dll");
                status = hasXlive ? "GFWL runtime loaded" : "No xlive.dll";
            }
            catch (Exception ex)
            {
                hasXlive = false;
                status = ex.Message;
            }

            if (showAll || hasXlive || LooksLikeGameProcess(process))
                results.Add(GfwlProcessItem.FromProcess(process, hasXlive, status));
        }

        return results;
    }

    public async Task<GfwlInjectionResult> InjectAdapterAsync(GfwlProcessItem process, CancellationToken cancellationToken)
    {
        var binaryStatus = GetBinaryStatus();
        if (!binaryStatus.EngineFound)
            return GfwlInjectionResult.Fail("engine.dll was not found.");

        if (process.ProcessId <= 0)
            return GfwlInjectionResult.Fail("No GFWL process was selected.");

        var helperPath = ResolveInjectorPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
            return GfwlInjectionResult.Fail("The x86 GFWL injector helper was not found. Rebuild the app and try again.");

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("inject");
        startInfo.ArgumentList.Add(process.ProcessId.ToString());
        startInfo.ArgumentList.Add(binaryStatus.EnginePath);

        using var helper = Process.Start(startInfo);
        if (helper is null)
            return GfwlInjectionResult.Fail("Failed to start the x86 GFWL injector helper.");

        var stdoutTask = helper.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = helper.StandardError.ReadToEndAsync(cancellationToken);
        await helper.WaitForExitAsync(cancellationToken);

        var output = (await stdoutTask).Trim();
        var error = (await stderrTask).Trim();
        var message = string.Join(Environment.NewLine, new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return helper.ExitCode == 0
            ? GfwlInjectionResult.Ok(string.IsNullOrWhiteSpace(message) ? "Injected GFWL adapter." : message)
            : GfwlInjectionResult.Fail(string.IsNullOrWhiteSpace(message) ? $"Injector exited with code {helper.ExitCode}." : message);
    }

    private static bool ProcessHasModule(Process process, string moduleName)
    {
        foreach (ProcessModule module in process.Modules)
        {
            if (module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LooksLikeGameProcess(Process process)
    {
        var name = process.ProcessName;
        return name.Contains("game", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("xlive", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("gfwl", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBinaryRoot()
    {
        foreach (var root in EnumerateBinaryRoots())
        {
            if (File.Exists(Path.Combine(root, "engine.dll")) || File.Exists(Path.Combine(root, "gfwlam.exe")))
                return root;
        }

        return Path.Combine(AppContext.BaseDirectory, "gfwlam");
    }

    private static string ResolveInjectorPath()
    {
        foreach (var root in EnumerateBinaryRoots())
        {
            var path = Path.Combine(root, "AchievementLabs.GfwlInjector.exe");
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(AppContext.BaseDirectory, "gfwl", "AchievementLabs.GfwlInjector.exe");
    }

    private static IEnumerable<string> EnumerateBinaryRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "gfwlam");

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "gfwlam");
            yield return Path.Combine(current.FullName, "AchievementLabs", "gfwlam");
            current = current.Parent;
        }
    }
}

public sealed record GfwlBinaryStatus(
    string Root,
    bool EngineFound,
    bool ManagerFound,
    bool ReadmeFound,
    string EnginePath,
    string ManagerPath)
{
    public bool IsComplete => EngineFound;
    public string Summary => IsComplete
        ? "GFWLAM engine ready for native Achievement Labs controls"
        : "GFWLAM engine.dll is missing";
}

public sealed record GfwlInjectionResult(bool Success, string Message)
{
    public static GfwlInjectionResult Ok(string message) => new(true, message);
    public static GfwlInjectionResult Fail(string message) => new(false, message);
}
