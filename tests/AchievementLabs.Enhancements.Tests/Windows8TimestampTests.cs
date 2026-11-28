using System.Text.Json;
using AchievementLabs.Desktop;

static class Windows8TimestampTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject(string input, DateTimeOffset now)
    {
        try { Windows8UnlockRequest.ResolveTimestamp(input, now); }
        catch (InvalidDataException) { return; }
        throw new Exception($"Accepted invalid Windows 8 timestamp: {input}");
    }

    private static void RejectLocal(string? date, string? time, DateTimeOffset now)
    {
        try { Windows8UnlockRequest.ResolveLocalTimestamp(date, time, now, TimeZoneInfo.Utc, new Random(17)); }
        catch (InvalidDataException) { return; }
        throw new Exception($"Accepted invalid local Windows 8 timestamp: {date} {time}");
    }

    public static void Run()
    {
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        Check(Windows8UnlockRequest.ResolveTimestamp("", now) == now, "Empty input should use request time.");
        var custom = Windows8UnlockRequest.ResolveTimestamp("2024-01-02T15:04:05.1234567-05:00", now);
        Check(custom == DateTimeOffset.Parse("2024-01-02T20:04:05.1234567Z"), "Offset conversion failed.");
        var milliseconds = Windows8UnlockRequest.ResolveTimestamp("2024-01-02T15:04:05.123-05:00", now);
        Check(Windows8UnlockRequest.Build("123", "1297292157", "7", milliseconds).Body.Contains("2024-01-02T20:04:05.1230000Z"), "Editable milliseconds were not preserved in the request.");
        var (url, body) = Windows8UnlockRequest.Build("123", "1297292157", "7", custom);
        Check(url == "https://progress.xboxlive.com/users/xuid(123)/progress/achievements/7?titleId=1297292157", "Legacy route changed.");
        using var json = JsonDocument.Parse(body);
        var record = json.RootElement.GetProperty("achievement");
        Check(record.GetProperty("id").GetUInt32() == 7 && record.GetProperty("titleId").GetUInt32() == 1297292157 &&
              record.GetProperty("platform").GetInt32() == 17 && record.GetProperty("unlocked").GetBoolean() &&
              record.GetProperty("unlockedOnline").GetBoolean() &&
              record.GetProperty("timeUnlocked").GetString() == "2024-01-02T20:04:05.1234567Z", "Legacy request payload changed.");
        Reject("2024-01-02T15:04:05", now);
        Reject("2024-02-30T15:04:05Z", now);
        Reject("2024-01-02T15:04:05+15:00", now);
        Reject("2005-11-21T23:59:59Z", now);
        Reject("2026-11-22T12:00:01Z", now);
        Check(Windows8UnlockRequest.ResolveLocalTimestamp("", "", now, TimeZoneInfo.Utc) == now, "Blank date and time should use request time.");
        var dateOnly = Windows8UnlockRequest.ResolveLocalTimestamp("2024-01-02", "", now, TimeZoneInfo.Utc, new Random(17));
        Check(DateOnly.FromDateTime(dateOnly.UtcDateTime) == new DateOnly(2024, 1, 2), "Date-only input changed the selected date.");
        var minuteOnly = Windows8UnlockRequest.ResolveLocalTimestamp("2024-01-02", "15:04", now, TimeZoneInfo.Utc, new Random(17));
        Check(minuteOnly.Hour == 15 && minuteOnly.Minute == 4, "Minute input did not preserve its hour and minute.");
        var secondOnly = Windows8UnlockRequest.ResolveLocalTimestamp("2024-01-02", "15:04:05", now, TimeZoneInfo.Utc, new Random(17));
        Check(secondOnly.Hour == 15 && secondOnly.Minute == 4 && secondOnly.Second == 5 && secondOnly.Millisecond >= 0 && secondOnly.Millisecond <= 999,
            "Second input did not randomize only the milliseconds.");
        var precise = Windows8UnlockRequest.ResolveLocalTimestamp("2024-01-02", "15:04:05.123", now, TimeZoneInfo.Utc, new Random(17));
        Check(precise == DateTimeOffset.Parse("2024-01-02T15:04:05.123Z"), "Explicit milliseconds were not preserved.");
        var eastern = TimeZoneInfo.CreateCustomTimeZone("test-eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern");
        Check(Windows8UnlockRequest.ResolveLocalTimestamp("2024-01-02", "15:04:05.123", now, eastern) ==
              DateTimeOffset.Parse("2024-01-02T20:04:05.123Z"), "Local input was not converted to request UTC.");
        Check(Windows8UnlockRequest.Build("123", "1297292157", "7", precise).Body.Contains("2024-01-02T15:04:05.1230000Z"),
            "Friendly timestamp did not reach the Windows 8 request payload.");
        var planned = Windows8UnlockRequest.Schedule(precise, [0, 1.5, 0]);
        Check(planned[0] == precise && planned[1] == precise.AddMilliseconds(1500) && planned[2] == planned[1],
            "Queue timestamps did not follow configured delays.");
        var todayMinute = Windows8UnlockRequest.ResolveLocalTimestamp("2026-11-22", "12:00", now, TimeZoneInfo.Utc, new Random(17));
        Check(todayMinute <= now, "Randomized current-minute time went into the future.");
        RejectLocal(null, "15:04", now);
        RejectLocal("2026-11-23", null, now);
        RejectLocal("2024-02-30", null, now);
        RejectLocal("2024-01-02", "24:00", now);
        RejectLocal("2026-11-22", "12:01", now);
        Console.WriteLine("PASS: Windows 8 timestamp input and request payload. No network.");
    }
}
