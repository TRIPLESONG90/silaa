using System.Text.Json;
using Microsoft.Extensions.AI;

namespace silaa.Services;

internal sealed class DateTimeTools
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            (Func<string>)GetCurrentDateTime,
            "get_current_datetime",
            "Get the authoritative current local date and time, UTC time, UTC offset, day of week, and operating-system time zone. Call this before resolving relative expressions such as today, tomorrow, or in two hours.")
    ];

    internal static string GetCurrentDateTime()
    {
        var local = DateTimeOffset.Now;
        var utc = local.ToUniversalTime();
        return JsonSerializer.Serialize(new
        {
            local_datetime = local.ToString("O"),
            local_date = local.ToString("yyyy-MM-dd"),
            local_time = local.ToString("HH:mm:ss"),
            day_of_week = local.DayOfWeek.ToString(),
            day_of_week_ko = KoreanDayOfWeek(local.DayOfWeek),
            utc_datetime = utc.UtcDateTime.ToString("O"),
            utc_offset = local.ToString("zzz"),
            time_zone_id = TimeZoneInfo.Local.Id,
            time_zone_name = TimeZoneInfo.Local.DisplayName,
            unix_time_seconds = local.ToUnixTimeSeconds()
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string KoreanDayOfWeek(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "일요일",
        DayOfWeek.Monday => "월요일",
        DayOfWeek.Tuesday => "화요일",
        DayOfWeek.Wednesday => "수요일",
        DayOfWeek.Thursday => "목요일",
        DayOfWeek.Friday => "금요일",
        DayOfWeek.Saturday => "토요일",
        _ => ""
    };
}
