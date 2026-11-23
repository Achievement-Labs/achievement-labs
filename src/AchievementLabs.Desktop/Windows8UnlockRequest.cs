using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchievementLabs.Desktop;

public static class Windows8UnlockRequest
{
    private static readonly Regex ExplicitOffset = new(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant);
    private static readonly DateTimeOffset EarliestUnlock = new(2005, 11, 22, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset ResolveTimestamp(string? input, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(input)) return now.ToUniversalTime();
        var value = input.Trim();
        if (!ExplicitOffset.IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw new InvalidDataException("Enter an ISO timestamp with seconds and Z or an explicit UTC offset, such as 2024-01-02T15:04:05-05:00.");
        if (timestamp < EarliestUnlock || timestamp > now)
            throw new InvalidDataException("The unlock timestamp must be between November 22, 2005 and now.");
        return timestamp.ToUniversalTime();
    }

    public static DateTimeOffset ResolveLocalTimestamp(string? dateInput, string? timeInput, DateTimeOffset now,
        TimeZoneInfo? zone = null, Random? random = null)
    {
        if (string.IsNullOrWhiteSpace(dateInput))
        {
            if (!string.IsNullOrWhiteSpace(timeInput)) throw new InvalidDataException("Enter a date before entering a time.");
            return now.ToUniversalTime();
        }
        if (!DateOnly.TryParseExact(dateInput.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("Enter a date as YYYY-MM-DD.");
        zone ??= TimeZoneInfo.Local;
        random ??= Random.Shared;
        var clock = (timeInput ?? "").Trim();
        if (clock.Length > 0 && !Regex.IsMatch(clock, @"\A\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Enter an optional local time as HH:mm, HH:mm:ss, or HH:mm:ss.SSS.");
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var today = DateOnly.FromDateTime(localNow);
        if (date > today) throw new InvalidDataException("The unlock date cannot be in the future.");
        var parts = clock.Length == 0 ? [] : clock.Split(':');
        var hour = parts.Length > 0 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minute = parts.Length > 0 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var seconds = parts.Length == 3 ? parts[2].Split('.') : [];
        var explicitSecond = seconds.Length > 0 ? int.Parse(seconds[0], CultureInfo.InvariantCulture) : (int?)null;
        var explicitMillisecond = seconds.Length == 2 ? int.Parse(seconds[1].PadRight(3, '0'), CultureInfo.InvariantCulture) : (int?)null;
        if (hour > 23 || minute > 59 || explicitSecond > 59)
            throw new InvalidDataException("The local time must be between 00:00 and 23:59:59.");
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            TimeSpan chosen;
            if (clock.Length == 0)
            {
                var max = date == today ? (long)localNow.TimeOfDay.TotalMilliseconds : 86_399_999L;
                chosen = TimeSpan.FromMilliseconds(random.NextInt64(max + 1));
            }
            else
            {
                var prefix = hour * 3_600_000L + minute * 60_000L + (explicitSecond ?? 0) * 1000L + (explicitMillisecond ?? 0);
                var spread = explicitMillisecond.HasValue ? 1L : explicitSecond.HasValue ? 1000L : 60_000L;
                if (date == today) spread = Math.Min(spread, (long)localNow.TimeOfDay.TotalMilliseconds - prefix + 1);
                if (spread <= 0) throw new InvalidDataException("The unlock time cannot be in the future.");
                chosen = TimeSpan.FromMilliseconds(prefix + random.NextInt64(spread));
            }
            var local = date.ToDateTime(TimeOnly.FromTimeSpan(chosen), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                if (clock.Length > 0) throw new InvalidDataException("This local time does not exist because of a daylight-saving change. Choose another time.");
                continue;
            }
            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            ValidateTimestamp(utc, now);
            return utc;
        }
        throw new InvalidDataException("Could not find a valid time on this date. Choose a time manually.");
    }

    public static void ValidateTimestamp(DateTimeOffset timestamp, DateTimeOffset now)
    {
        if (timestamp < EarliestUnlock || timestamp > now)
            throw new InvalidDataException("The unlock timestamp must be between November 22, 2005 and now.");
    }

    public static DateTimeOffset[] Schedule(DateTimeOffset start, IReadOnlyList<double> delays)
    {
        var result = new DateTimeOffset[delays.Count];
        for (var index = 0; index < delays.Count; index++)
        {
            if (!double.IsFinite(delays[index]) || delays[index] < 0) throw new InvalidDataException("Queue delays must be nonnegative and finite.");
            result[index] = index == 0 ? start : result[index - 1].AddMilliseconds(Math.Round(delays[index] * 1000, MidpointRounding.AwayFromZero));
        }
        return result;
    }

    public static (string Url, string Body) Build(string xuid, string titleId, string achievementId, DateTimeOffset timestamp) =>
        ($"https://progress.xboxlive.com/users/xuid({xuid})/progress/achievements/{achievementId}?titleId={titleId}",
         JsonSerializer.Serialize(new { achievement = new
         {
             id = uint.Parse(achievementId, CultureInfo.InvariantCulture),
             platform = 17,
             titleId = uint.Parse(titleId, CultureInfo.InvariantCulture),
             unlocked = true,
             unlockedOnline = true,
             timeUnlocked = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)
         } }));
}
