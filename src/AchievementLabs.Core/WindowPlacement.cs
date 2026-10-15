namespace AchievementLabs.Core;

public sealed record WindowPlacement(double Width, double Height, int? X = null, int? Y = null)
{
    /// <summary>Client sizes use DIPs; position and monitor working area use physical pixels.</summary>
    public static WindowPlacement Fit(WindowPlacement saved, int left, int top, int areaWidth, int areaHeight,
        double scale, double frameWidth, double frameHeight, double minimumWidth = 1120, double minimumHeight = 720)
    {
        if (areaWidth <= 0 || areaHeight <= 0 || !double.IsFinite(scale) || scale <= 0) throw new ArgumentException("Invalid monitor working area.");
        frameWidth = double.IsFinite(frameWidth) ? Math.Max(0, frameWidth) : 16;
        frameHeight = double.IsFinite(frameHeight) ? Math.Max(0, frameHeight) : 48;
        var maxWidth = Math.Max(1, areaWidth / scale - frameWidth);
        var maxHeight = Math.Max(1, areaHeight / scale - frameHeight);
        var width = Math.Clamp(double.IsFinite(saved.Width) ? saved.Width : 1440, Math.Min(minimumWidth, maxWidth), maxWidth);
        var height = Math.Clamp(double.IsFinite(saved.Height) ? saved.Height : 920, Math.Min(minimumHeight, maxHeight), maxHeight);
        var outerWidth = Math.Min(areaWidth, (int)Math.Ceiling((width + frameWidth) * scale));
        var outerHeight = Math.Min(areaHeight, (int)Math.Ceiling((height + frameHeight) * scale));
        var x = Math.Clamp(saved.X ?? left + (areaWidth - outerWidth) / 2, left, left + areaWidth - outerWidth);
        var y = Math.Clamp(saved.Y ?? top + (areaHeight - outerHeight) / 2, top, top + areaHeight - outerHeight);
        return new(width, height, x, y);
    }
}
