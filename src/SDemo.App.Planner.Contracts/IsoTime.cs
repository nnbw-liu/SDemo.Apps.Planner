using System.Globalization;

namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 时间口径（docs/requirements.md §3）：一律存 UTC、按本地时区显示。
/// 入库的字符串必须带偏移（ISO-8601），禁止「明天」这类自然语言直接落库。
/// </summary>
public static class IsoTime
{
    /// <summary>带偏移的时间串。Z / +08:00 / -05:00 都算；没有偏移的裸时间一律拒绝。</summary>
    public static bool TryParseUtc(string? value, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // DateTimeOffset.TryParse 在没有偏移时会假定本地时区，这里先显式拒绝：
        // 时区一旦搞错，dueAt 与日历视图就会整体偏移。
        if (!HasExplicitOffset(text))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        utc = parsed.ToUniversalTime();
        return true;
    }

    public static bool HasExplicitOffset(string text)
    {
        if (text.Length < 6)
        {
            return false;
        }

        if (text.EndsWith('Z') || text.EndsWith('z'))
        {
            return true;
        }

        // 末尾 ±hh:mm 或 ±hhmm
        var tail = text[^6..];
        if ((tail[0] is '+' or '-') && tail[3] == ':')
        {
            return true;
        }

        var tail5 = text[^5..];
        return (tail5[0] is '+' or '-') && char.IsDigit(tail5[1]) && char.IsDigit(tail5[2]);
    }

    public static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    public static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();

    /// <summary>UTC ISO-8601（用于 ICS 的 DTSTAMP / DUE / DTSTART）。</summary>
    public static string ToIcs(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string ToDisplay(DateTimeOffset utc, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>本地日期（用于日历视图分组）。</summary>
    public static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local).DateTime);

    /// <summary>把本地 yyyy-MM-dd 的某个时刻转成 UTC。</summary>
    public static DateTimeOffset FromLocalDate(DateOnly date, TimeOnly time, TimeZoneInfo? zone = null)
    {
        var localZone = zone ?? TimeZoneInfo.Local;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = localZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
