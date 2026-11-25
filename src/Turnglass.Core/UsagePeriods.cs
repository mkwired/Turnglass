using System.Globalization;

namespace Turnglass.Core;

public sealed record TimeWindow(string Label, DateTimeOffset Start, DateTimeOffset End);

public static class UsagePeriods
{
    public static IReadOnlyList<TimeWindow> GetPeriods(DateTimeOffset now, string mode, string timezone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timezone);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        if (mode == "rolling")
        {
            return [new("Last 24 hours", now.AddHours(-24), now), new("Last 7 days", now.AddDays(-7), now), new("Last 30 days", now.AddDays(-30), now)];
        }

        if (mode != "calendar")
        {
            throw new ArgumentException("Mode must be calendar or rolling.", nameof(mode));
        }

        var local = TimeZoneInfo.ConvertTime(now, zone).Date;
        var monday = local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
        var month = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return [new("Today", LocalToUtc(local, zone), now), new("This week", LocalToUtc(monday, zone), now), new("This month", LocalToUtc(month, zone), now)];
    }

    public static string GetDay(DateTimeOffset timestamp, string timezone) => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, timezone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
