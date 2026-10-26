using System.Reflection;

namespace AchievementLabs.Desktop;

/// <summary>
/// Version text shown in the UI. Read from the built assembly so it always matches the
/// version passed to tools/Build-Release.ps1 instead of a hard-coded string.
/// </summary>
public static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    public static string DisplayName { get; } = "Achievement Labs " + Version;

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK may append "+<commit>" source-revision metadata; it isn't useful to show.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version == null ? "unknown" : version.ToString(3);
    }
}
