using System.Globalization;

namespace AchievementLabs.Core;

public static class XboxResponseLanguage
{
    public static string Resolve(bool forceEnglish)
        => forceEnglish || string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
            ? "en-GB" : CultureInfo.CurrentUICulture.Name;
}
