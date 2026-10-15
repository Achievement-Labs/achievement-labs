using System.Globalization;
namespace AchievementLabs.MultiSelect;

public static class AchievementRarity
{
    public static double? Parse(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) && value is >= 0 and <= 100 ? value : null;
}
