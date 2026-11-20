using System.Globalization;
using System.Text.RegularExpressions;

namespace TNM.Management.Services;

/// <summary>
/// Chuẩn hóa hiển thị & phân tích thời gian định dạng chuẩn Việt Nam (dd/MM/yyyy HH:mm:ss)
/// phục vụ bộ lọc khung thời gian chuẩn ERP lớn.
/// </summary>
public static partial class VnDateTimeHelper
{
    public const string DisplayFormat = "dd/MM/yyyy HH:mm:ss";
    public const string SqlFormat = "yyyy-MM-dd HH:mm:ss";

    public static DateTime NowVn => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime;
    public static DateTime TodayVn => NowVn.Date;
    public static string NowSql() => NowVn.ToString(SqlFormat, CultureInfo.InvariantCulture);

    private static readonly string[] ExactFormatsWithSeconds =
    [
        "dd/MM/yyyy HH:mm:ss", "d/M/yyyy HH:mm:ss", "d/M/yyyy H:m:s", "dd/MM/yyyy H:m:s",
        "dd-MM-yyyy HH:mm:ss", "d-M-yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        "yyyy/MM/dd HH:mm:ss"
    ];

    private static readonly string[] ExactFormatsWithMinutes =
    [
        "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm", "d/M/yyyy H:m", "dd/MM/yyyy H:m",
        "dd-MM-yyyy HH:mm", "d-M-yyyy HH:mm",
        "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm",
        "yyyy/MM/dd HH:mm"
    ];

    private static readonly string[] ExactDateOnlyFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd"
    ];

    public static DateTime? ParseDbTimestamp(string? raw, bool defaultEndOfDayIfDateOnly = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // ISO UTC timestamp (e.g. 2026-11-14T08:18:38.123Z) -> convert to Vietnam time (+07:00)
        if (s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToOffset(TimeSpan.FromHours(7)).DateTime;
        }

        if (s.Length > 19 && (s[10] == ' ' || s[10] == 'T'))
            s = s[..19];

        if (DateTime.TryParseExact(s, ExactFormatsWithSeconds, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtSec))
            return dtSec;

        if (DateTime.TryParseExact(s, ExactFormatsWithMinutes, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtMin))
            return defaultEndOfDayIfDateOnly ? dtMin.AddSeconds(59) : dtMin;

        if (DateTime.TryParseExact(s, ExactDateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtDay))
            return defaultEndOfDayIfDateOnly ? dtDay.Date.AddHours(23).AddMinutes(59).AddSeconds(59) : dtDay.Date;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallback))
            return fallback;

        return null;
    }

    /// <summary>
    /// Phân tích chuỗi người dùng nhập vào ô Từ ngày / Đến ngày.
    /// Tự động điền 00:00:00 (cho Từ ngày) hoặc 23:59:59 (cho Đến ngày) nếu chỉ nhập ngày.
    /// </summary>
    public static bool TryParseUserFilterInput(string? input, bool isEndBoundary, out DateTime parsed, out string normalizedDisplay)
    {
        parsed = default;
        normalizedDisplay = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var clean = Regex.Replace(input.Trim(), @"\s+", " ");

        if (DateTime.TryParseExact(clean, ExactFormatsWithSeconds, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtSec))
        {
            parsed = dtSec;
            normalizedDisplay = parsed.ToString(DisplayFormat, CultureInfo.InvariantCulture);
            return true;
        }

        if (DateTime.TryParseExact(clean, ExactFormatsWithMinutes, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtMin))
        {
            parsed = isEndBoundary ? dtMin.AddSeconds(59) : dtMin;
            normalizedDisplay = parsed.ToString(DisplayFormat, CultureInfo.InvariantCulture);
            return true;
        }

        if (DateTime.TryParseExact(clean, ExactDateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtDay))
        {
            parsed = isEndBoundary
                ? dtDay.Date.AddHours(23).AddMinutes(59).AddSeconds(59)
                : dtDay.Date;
            normalizedDisplay = parsed.ToString(DisplayFormat, CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    public static string FormatDisplay(string? dbTimestamp, bool defaultEndOfDayIfDateOnly = false)
    {
        var dt = ParseDbTimestamp(dbTimestamp, defaultEndOfDayIfDateOnly);
        return dt.HasValue ? dt.Value.ToString(DisplayFormat, CultureInfo.InvariantCulture) : (dbTimestamp ?? "—");
    }

    public static string FormatDateOnly(string? dbDate)
    {
        var dt = ParseDbTimestamp(dbDate, false);
        return dt.HasValue ? dt.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : (dbDate ?? "—");
    }

    public static string FormatDisplay(DateTime dt) => dt.ToString(DisplayFormat, CultureInfo.InvariantCulture);

    public static string ToHtmlDateTimeLocal(DateTime? dt) =>
        dt.HasValue ? dt.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "";

    public static bool InRange(DateTime? target, DateTime? from, DateTime? to)
    {
        if (!from.HasValue && !to.HasValue) return true;
        if (!target.HasValue) return false;
        if (from.HasValue && target.Value < from.Value) return false;
        if (to.HasValue && target.Value > to.Value) return false;
        return true;
    }

    [GeneratedRegex(@"\[(?:#\d+\s*·\s*)?(\d{2}/\d{2}/\d{4}(?:\s+\d{2}:\d{2}(?::\d{2})?)?|\d{4}-\d{2}-\d{2}(?:\s+\d{2}:\d{2}(?::\d{2})?)?)")]
    private static partial Regex BracketDateRegex();

    public static DateTime? TryExtractLineTimestamp(string? description, DateTime? fallbackInvoiceTime = null)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            var m = BracketDateRegex().Match(description);
            if (m.Success)
            {
                var dt = ParseDbTimestamp(m.Groups[1].Value, false);
                if (dt.HasValue) return dt;
            }
        }
        return fallbackInvoiceTime;
    }
}
