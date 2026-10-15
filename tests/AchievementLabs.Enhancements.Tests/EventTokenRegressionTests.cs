using AchievementLabs.Core;
using AchievementLabs.Desktop;
using System.Reflection;
using System.Text;

public static class EventTokenRegressionTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        Check(EventTokenValidator.GetUserHash("  \"x:xbl3.0   x=abc-123;synthetic\"  ") == "abc-123", "Quoted, case-insensitive, nonnumeric hash.");
        Check(EventTokenValidator.GetUserHash("XBL3.0 x=-;synthetic") == null, "Redacted hash remains unknown.");
        Check(!EventTokenValidator.IsDifferentUser("x:XBL3.0 x=abc;synthetic", "unreadable-session"), "Unknown session hash is not proof of mismatch.");
        Check(!EventTokenValidator.IsDifferentUser("x:XBL3.0 x=-;synthetic", "XBL3.0 x=999;synthetic"), "Unknown token hash is not proof of mismatch.");
        Check(!EventTokenValidator.IsDifferentUser("x:XBL3.0 x=999;synthetic", "XBL3.0 x=999;synthetic"), "Matching hashes.");
        Check(EventTokenValidator.IsDifferentUser("x:XBL3.0 x=111;synthetic", "XBL3.0 x=999;synthetic"), "Readable difference still produces a warning signal.");
        Check(EventTokenValidator.TryNormalize("tickets: \"1\"=\"x:XBL3.0 x=999;synthetic\"", out var normalized) && normalized == "x:XBL3.0 x=999;synthetic", "Copied header normalization.");
        using var model = new DesktopModel();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = typeof(DesktopModel).GetField("session", flags)!;
        session.SetValue(model, new ConnectedXboxSession("unreadable-session", "123", ""));
        var saved = typeof(DesktopModel).GetField("savedEventsToken", flags)!;
        var rejected = typeof(DesktopModel).GetField("eventTokenRejected", flags)!;
        var usable = typeof(DesktopModel).GetMethod("IsEventTokenUsable", flags)!;
        bool IsUsable() => (bool)usable.Invoke(model, null)!;
        saved.SetValue(model, normalized);
        Check(IsUsable(), "Unreadable attached hash no longer blocks a valid saved token.");
        session.SetValue(model, new ConnectedXboxSession("XBL3.0 x=111;synthetic", "123", ""));
        Check(IsUsable(), "Use-time heuristic does not reintroduce the save-time block.");
        rejected.SetValue(model, true); Check(!IsUsable(), "Xbox rejection still blocks reuse."); rejected.SetValue(model, false);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds() + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        saved.SetValue(model, "x:XBL3.0 x=999;e30." + payload + ".synthetic"); Check(!IsUsable(), "Expired tokens still fail.");
        saved.SetValue(model, "invalid"); Check(!IsUsable(), "Malformed tokens still fail.");
        Console.WriteLine("PASS: event-token parsing, unknown/matching/different hashes, usable-token regression and retained rejection/expiry/format checks. Synthetic data only; no stored credentials changed.");
    }
}
