using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static Lokad.Jq.JqRuntime;

namespace Lokad.Jq;

// Explicit-host date core: proleptic Gregorian civil arithmetic, epoch
// conversion with fractional seconds, broken-down assembly, and directive
// parsing and formatting. No ambient clock, timezone, locale, or calendar.
internal static class JqTime
{
    internal static double Now(JqContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Clock is null)
            throw new JqException("now requires an explicit host clock");
        return (context.Clock.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
    }

    internal static bool IsLeapYear(long year) =>
        (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    internal static int DaysInMonth(long year, int month) => month switch
    {
        2 => IsLeapYear(year) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    // Days since 1970-01-01 from civil fields; month is 1-12.
    internal static long DaysFromCivil(long year, int month, int day)
    {
        long y = month <= 2 ? year - 1 : year;
        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - era * 400;
        int mp = month > 2 ? month - 3 : month + 9;
        long doy = ((153L * mp) + 2) / 5 + day - 1;
        long doe = (yoe * 365) + (yoe / 4) - (yoe / 100) + doy;
        return (era * 146097) + doe - 719468;
    }

    internal static void CivilFromDays(long days, out long year, out int month, out int day)
    {
        long z = days + 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;
        long yoe = (doe - (doe / 1460) + (doe / 36524) - (doe / 146096)) / 365;
        long y = yoe + era * 400;
        long doy = doe - ((365 * yoe) + (yoe / 4) - (yoe / 100));
        long mp = ((5 * doy) + 2) / 153;
        long d = doy - (((153 * mp) + 2) / 5) + 1;
        long m = mp < 10 ? mp + 3 : mp - 9;
        year = m <= 2 ? y + 1 : y;
        month = (int)m;
        day = (int)d;
    }

    internal static int WeekdaySunday0(long days) => (int)((((days % 7) + 7) % 7 + 4) % 7);

    private static long DivFloor(long value, long positive)
    {
        long quotient = value / positive;
        return value % positive < 0 ? quotient - 1 : quotient;
    }

    private static long ModPositive(long value, long positive)
    {
        long rest = value % positive;
        return rest < 0 ? rest + positive : rest;
    }

    // Broken-down UTC civil fields with fractional seconds, mirroring tm2jv order.
    internal sealed record BrokenDown(long Year, int Month, int Day, int Hour, int Minute, double Second, int Weekday, int YearDay);

    // Normalizes field overflow like timegm; years beyond a hundred billion
    // report the upstream out-of-range diagnostic instead of wrapping.
    internal static long ToEpochSeconds(long year, long month0, long day, long hour, long minute, long second)
    {
        if (year > 100000000000L || year < -100000000000L)
            throw new JqException("invalid gmtime representation");
        long adjustedYear = year + DivFloor(month0, 12);
        int month = (int)ModPositive(month0, 12) + 1;
        long days = DaysFromCivil(adjustedYear, month, 1) + (day - 1);
        if (days > long.MaxValue / 86400L || days < long.MinValue / 86400L)
            throw new JqException("invalid gmtime representation");
        return (days * 86400L) + (hour * 3600L) + (minute * 60L) + second;
    }

    internal static BrokenDown FromEpochUtc(long whole, double frac)
    {
        long days = DivFloor(whole, 86400L);
        long rest = ModPositive(whole, 86400L);
        CivilFromDays(days, out long year, out int month, out int day);
        int hour = (int)(rest / 3600L);
        int minute = (int)((rest % 3600L) / 60L);
        double second = (rest % 60L) + frac;
        int weekday = WeekdaySunday0(days);
        int yearday = (int)(days - DaysFromCivil(year, 1, 1));
        return new BrokenDown(year, month, day, hour, minute, second, weekday, yearday);
    }

    private static bool IsRepresentableEpoch(double epoch) =>
        !double.IsNaN(epoch) && !double.IsInfinity(epoch) && epoch < 9.223372036854776e18 && epoch >= -9.223372036854776e18;

    // Truncating conversion mirroring the reference time_t cast, so negative
    // fractional epochs keep the positive fraction the reference adds.
    internal static BrokenDown DecomposeUtc(double epoch)
    {
        if (!IsRepresentableEpoch(epoch))
            throw new JqException("error converting number of seconds since epoch to datetime");
        long whole = (long)epoch;
        double frac = epoch - Math.Floor(epoch);
        return FromEpochUtc(whole, frac);
    }

    internal static JsonNode BreakdownToJson(JqContext context, BrokenDown moment)
    {
        ArgumentNullException.ThrowIfNull(context);
        var array = new JsonArray(
            JsonValue.Create(moment.Year),
            JsonValue.Create(moment.Month - 1),
            JsonValue.Create(moment.Day),
            JsonValue.Create(moment.Hour),
            JsonValue.Create(moment.Minute),
            JsonValue.Create(moment.Second),
            JsonValue.Create(moment.Weekday),
            JsonValue.Create(moment.YearDay));
        for (int index = 0; index < 9; index++)
            context.Budget.ChargeNode();
        return array;
    }

    internal static JsonNode Gmtime(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TypeName(input) != "number")
            throw new JqException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") gmtime() requires numeric inputs");
        return BreakdownToJson(context, DecomposeUtc(Number(input)));
    }
    internal static JsonNode MktimeUtc(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (input is not JsonArray array)
            throw new JqException("mktime requires array inputs");
        if (!TryReadFields(array, out long year, out long month0, out long day, out long hour, out long minute, out long second))
            throw new JqException("mktime requires parsed datetime inputs");
        return JsonValue.Create(ToEpochSeconds(year, month0, day, hour, minute, second));
    }

    // Reads up to eight array elements like jv2tm: missing elements stop the
    // scan with zeros kept, while non-number or NaN elements fail the whole read.
    internal static bool TryReadFields(JsonNode? input, out long year, out long month0, out long day, out long hour, out long minute, out long second)
    {
        year = 1900;
        month0 = 0;
        day = 0;
        hour = 0;
        minute = 0;
        second = 0;
        if (input is not JsonArray array)
            return false;
        for (int index = 0; index < 8; index++)
        {
            if (index >= array.Count)
                break;
            JsonNode? element = array[index];
            if (element is null || TypeName(element) != "number")
                return false;
            double value = Number(element);
            if (double.IsNaN(value))
                return false;
            long whole = ClampInt(value);
            switch (index)
            {
                case 0: year = ClampInt(value - 1900.0) + 1900L; break;
                case 1: month0 = whole; break;
                case 2: day = whole; break;
                case 3: hour = whole; break;
                case 4: minute = whole; break;
                case 5: second = whole; break;
                default: break;
            }
        }
        return true;
    }

    private static int ClampInt(double value) =>
        value <= int.MinValue ? int.MinValue : value >= int.MaxValue ? int.MaxValue : (int)value;

    internal static JsonNode Localtime(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TypeName(input) != "number")
            throw new JqException(TypeName(input) + " (" + context.Runtime.Serialize(input, false, null, false) + ") localtime() requires numeric inputs");
        return BreakdownToJson(context, DecomposeLocal(context, Number(input)));
    }

    internal static BrokenDown DecomposeLocal(JqContext context, double epoch)
    {
        ArgumentNullException.ThrowIfNull(context);
        TimeZoneInfo zone = context.Clock?.LocalTimeZone ?? throw new JqException("localtime requires an explicit host time zone");
        if (!IsRepresentableEpoch(epoch))
            throw new JqException("error converting number of seconds since epoch to datetime");
        long whole = (long)epoch;
        double frac = epoch - Math.Floor(epoch);
        DateTimeOffset instant;
        try
        {
            instant = DateTimeOffset.FromUnixTimeSeconds(whole).AddSeconds(frac);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new JqException("error converting number of seconds since epoch to datetime");
        }
        DateTimeOffset local;
        try
        {
            local = TimeZoneInfo.ConvertTime(instant, zone);
        }
        catch (ArgumentException)
        {
            throw new JqException("error converting number of seconds since epoch to datetime");
        }
        return new BrokenDown(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second + frac, WeekdaySunday0(DaysFromCivil(local.Year, local.Month, local.Day)), local.DayOfYear - 1);
    }

    // Normalizes parsed fields in wall space and returns the wall plus its
    // epoch read as UTC, mirroring timegm normalization for formatting.
    internal static (BrokenDown Wall, long Epoch) NormalizeWall(long year, long month0, long day, long hour, long minute, long second)
    {
        long total = ToEpochSeconds(year, month0, day, hour, minute, second);
        return (FromEpochUtc(total, 0.0), total);
    }

    // Zone offset and name for wall fields. Ambiguous or skipped local times
    // resolve through TimeZoneInfo defaults deterministically.
    internal static (TimeSpan Offset, string Name) LocalZoneParts(TimeZoneInfo zone, BrokenDown wall)
    {
        DateTime moment;
        try
        {
            moment = new DateTime((int)wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, (int)wall.Second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new JqException("error converting number of seconds since epoch to datetime");
        }
        TimeSpan offset = zone.GetUtcOffset(moment);
        string name = zone.IsDaylightSavingTime(moment) ? zone.DaylightName : zone.StandardName;
        return (offset, name);
    }

    internal static string Strftime(JqContext context, JsonNode? input, JsonNode? formatNode, bool local)
    {
        ArgumentNullException.ThrowIfNull(context);
        string prefix = local ? "strflocaltime/1" : "strftime/1";
        BrokenDown moment;
        long epochSeconds;
        TimeSpan offset = TimeSpan.Zero;
        string zoneName = "UTC";
        // Like the reference, input shape and the format string validate
        // before any host time-zone lookup, so malformed formats report even
        // when no zone is configured.
        double epochNumber = 0;
        BrokenDown? wallFields = null;
        long wallTotal = 0;
        if (TypeName(input) == "number")
        {
            epochNumber = Number(input);
        }
        else if (input is JsonArray inputArray
            && TryReadFields(inputArray, out long year, out long month0, out long day, out long hour, out long minute, out long second))
        {
            (wallFields, wallTotal) = NormalizeWall(year, month0, day, hour, minute, second);
        }
        else
        {
            throw new JqException(prefix + " requires parsed datetime inputs");
        }
        if (!TryGetString(formatNode, out string? format) || format is null)
            throw new JqException(prefix + " requires a string format");
        if (wallFields is null)
        {
            if (local)
            {
                TimeZoneInfo zone = context.Clock?.LocalTimeZone ?? throw new JqException("strflocaltime requires an explicit host time zone");
                moment = DecomposeLocal(context, epochNumber);
                (offset, zoneName) = LocalZoneParts(zone, moment);
            }
            else
            {
                moment = DecomposeUtc(epochNumber);
            }
            epochSeconds = (long)epochNumber;
        }
        else
        {
            moment = wallFields;
            epochSeconds = wallTotal;
            if (local)
            {
                TimeZoneInfo zone = context.Clock?.LocalTimeZone ?? throw new JqException("strflocaltime requires an explicit host time zone");
                (offset, zoneName) = LocalZoneParts(zone, wallFields);
                epochSeconds = wallTotal - (long)offset.TotalSeconds;
            }
        }
        return RenderStrftime(context, moment, epochSeconds, offset, zoneName, format);
    }

    internal static string RenderStrftime(JqContext context, BrokenDown moment, long epochSeconds, TimeSpan offset, string zoneName, string format)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(moment);
        ArgumentNullException.ThrowIfNull(zoneName);
        ArgumentNullException.ThrowIfNull(format);
        var builder = new StringBuilder();
        for (int index = 0; index < format.Length; index++)
        {
            if (format[index] != (char)37 || index + 1 >= format.Length)
            {
                context.Budget.Append(builder, format.AsSpan(index, 1));
                continue;
            }
            char spec = format[index + 1];
            index++;
            switch (spec)
            {
                case (char)37: context.Budget.Append(builder, "%"); break;
                case (char)89: context.Budget.Append(builder, moment.Year.ToString(CultureInfo.InvariantCulture)); break;
                case (char)121: context.Budget.Append(builder, TwoDigitYear(moment.Year)); break;
                case (char)109: context.Budget.Append(builder, moment.Month.ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)100: context.Budget.Append(builder, moment.Day.ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)101: context.Budget.Append(builder, moment.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2, (char)32)); break;
                case (char)72: context.Budget.Append(builder, moment.Hour.ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)73: context.Budget.Append(builder, TwelveHour(moment.Hour).ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)77: context.Budget.Append(builder, moment.Minute.ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)83: context.Budget.Append(builder, ((int)moment.Second).ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)112: context.Budget.Append(builder, moment.Hour < 12 ? "AM" : "PM"); break;
                case (char)106: context.Budget.Append(builder, (moment.YearDay + 1).ToString("D3", CultureInfo.InvariantCulture)); break;
                case (char)85: context.Budget.Append(builder, WeekNumber(moment, false).ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)87: context.Budget.Append(builder, WeekNumber(moment, true).ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)119: context.Budget.Append(builder, ((char)((char)48 + moment.Weekday)).ToString()); break;
                case (char)117: context.Budget.Append(builder, (((moment.Weekday + 6) % 7) + 1).ToString(CultureInfo.InvariantCulture)); break;
                case (char)97: context.Budget.Append(builder, WeekdayAbbr(moment.Weekday)); break;
                case (char)65: context.Budget.Append(builder, WeekdayName(moment.Weekday)); break;
                case (char)98: context.Budget.Append(builder, MonthAbbr(moment.Month)); break;
                case (char)66: context.Budget.Append(builder, MonthName(moment.Month)); break;
                case (char)86: IsoWeek(moment, out int isoWeek, out _); context.Budget.Append(builder, isoWeek.ToString("D2", CultureInfo.InvariantCulture)); break;
                case (char)71: IsoWeek(moment, out _, out long isoYear); context.Budget.Append(builder, isoYear.ToString(CultureInfo.InvariantCulture)); break;
                case (char)122: context.Budget.Append(builder, FormatOffset(offset)); break;
                case (char)90: context.Budget.Append(builder, zoneName); break;
                case (char)115: context.Budget.Append(builder, epochSeconds.ToString(CultureInfo.InvariantCulture)); break;
                case (char)99: context.Budget.Append(builder, RenderStrftime(context, moment, epochSeconds, offset, zoneName, "%a %b %e %H:%M:%S %Y")); break;
                case (char)120: context.Budget.Append(builder, RenderStrftime(context, moment, epochSeconds, offset, zoneName, "%m/%d/%y")); break;
                case (char)88: context.Budget.Append(builder, RenderStrftime(context, moment, epochSeconds, offset, zoneName, "%H:%M:%S")); break;
                default: context.Budget.Append(builder, "%"); context.Budget.Append(builder, format.AsSpan(index, 1)); break;
            }
        }
        return context.Budget.Finish(builder);
    }

    private static string TwoDigitYear(long year)
    {
        long pair = year % 100L;
        return pair < 0 ? pair.ToString(CultureInfo.InvariantCulture) : pair.ToString("D2", CultureInfo.InvariantCulture);
    }

    private static int TwelveHour(int hour) => ((hour + 11) % 12) + 1;

    private static int WeekNumber(BrokenDown moment, bool mondayStart)
    {
        int weekday = mondayStart ? (moment.Weekday + 6) % 7 : moment.Weekday;
        return (moment.YearDay - weekday + 7) / 7;
    }

    internal static void IsoWeek(BrokenDown moment, out int week, out long isoYear)
    {
        long days = DaysFromCivil(moment.Year, moment.Month, moment.Day);
        int monday0 = (moment.Weekday + 6) % 7;
        long thursday = days - monday0 + 3;
        CivilFromDays(thursday, out isoYear, out _, out _);
        long jan4 = DaysFromCivil(isoYear, 1, 4);
        long week1monday = jan4 - ((WeekdaySunday0(jan4) + 6) % 7);
        week = (int)((days - week1monday) / 7) + 1;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        long totalMinutes = (long)offset.TotalMinutes;
        long sign = totalMinutes < 0 ? -1L : 1L;
        long absolute = totalMinutes * sign;
        return (sign < 0 ? "-" : "+") + (absolute / 60L).ToString("D2", CultureInfo.InvariantCulture) + (absolute % 60L).ToString("D2", CultureInfo.InvariantCulture);
    }

    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] MonthAbbrs = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] WeekdayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] WeekdayAbbrs = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    private static string MonthName(int month) => MonthNames[month - 1];
    private static string MonthAbbr(int month) => MonthAbbrs[month - 1];
    private static string WeekdayName(int weekday) => WeekdayNames[weekday];
    private static string WeekdayAbbr(int weekday) => WeekdayAbbrs[weekday];

    internal static JsonNode Strptime(JqContext context, JsonNode? input, JsonNode? formatNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetString(input, out string? text) || text is null || !TryGetString(formatNode, out string? format) || format is null)
            throw new JqException("strptime/1 requires string inputs and arguments");
        var parser = new DateParser(text, format);
        if (!parser.TryParse())
            throw new JqException("date \"" + text + "\" does not match format \"" + format + "\"");
        return parser.BuildResult(context);
    }

    internal static JsonNode FromDateIso(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetString(input, out string? text) || text is null)
            throw new JqException("strptime/1 requires string inputs and arguments");
        const string format = "%Y-%m-%dT%H:%M:%SZ";
        var parser = new DateParser(text, format);
        if (!parser.TryParse())
            throw new JqException("date \"" + text + "\" does not match format \"" + format + "\"");
        return JsonValue.Create(parser.ToEpochSeconds());
    }

    internal static string ToDateIso(JqContext context, JsonNode? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Strftime(context, input, JsonValue.Create("%Y-%m-%dT%H:%M:%SZ"), false);
    }

    // Manual C-locale strptime covering the portable directive set. Week-based
    // and day-of-year directives resolve through civil arithmetic; zone offsets
    // validate shape but stay unused downstream like the timegm code path.
    private sealed class DateParser(string text, string format)
    {
        private int _position;
        private bool _hasYear;
        private long _year;
        private bool _hasMonth;
        private int _month0;
        private bool _hasDay;
        private int _day;
        private bool _hasHour;
        private int _hour;
        private bool _hasHour12;
        private int _hour12;
        private bool _hasAmPm;
        private bool _isPm;
        private bool _hasMinute;
        private int _minute;
        private bool _hasSecond;
        private int _second;
        private bool _hasWeekday;
        private int _weekday;
        private bool _hasYearday;
        private int _yearday;
        private bool _hasWeek;
        private int _week;
        private bool _weekMonday;
        private bool _hasIsoWeek;
        private int _isoWeek;
        private bool _hasIsoYear;
        private long _isoYear;

        internal bool TryParse()
        {
            if (!ParseFormat(format))
                return false;
            for (int index = _position; index < text.Length; index++)
            {
                if (!IsSpace(text[index]))
                    return false;
            }
            return true;
        }

        internal JsonNode BuildResult(JqContext context)
        {
            long year = _hasIsoYear ? _isoYear : _hasYear ? _year : 1900L;
            int month0 = 0;
            int day = 0;
            if (_hasMonth)
            {
                month0 = _month0;
            }
            if (_hasDay)
            {
                day = _day;
            }
            if (_hasYearday)
            {
                CivilFromDays(DaysFromCivil(year, 1, 1) + _yearday, out _, out int resolvedMonth, out int resolvedDay);
                month0 = resolvedMonth - 1;
                day = resolvedDay;
            }
            else if (_hasWeek && _hasYear)
            {
                SundayWeekToCivil(year, out int resolvedMonth, out int resolvedDay, out int resolvedYearday);
                month0 = resolvedMonth - 1;
                day = resolvedDay;
                _yearday = resolvedYearday;
                _hasYearday = true;
            }
            else if (_hasIsoWeek && (_hasIsoYear || _hasYear))
            {
                IsoWeekToCivil(_hasIsoYear ? _isoYear : year, out int resolvedMonth, out int resolvedDay, out int resolvedYearday);
                month0 = resolvedMonth - 1;
                day = resolvedDay;
                _yearday = resolvedYearday;
                _hasYearday = true;
            }
            int hour = _hasHour ? _hour : 0;
            if (_hasHour12 && _hasAmPm)
                hour = (_hour12 % 12) + (_isPm ? 12 : 0);
            else if (_hasHour12)
                hour = _hour12;
            int minute = _hasMinute ? _minute : 0;
            int second = _hasSecond ? _second : 0;
            int weekday = 8;
            int yearday = 367;
            if (_hasWeekday)
                weekday = _weekday;
            if (_hasYearday)
                yearday = _yearday;
            if (weekday == 8 && day != 0 && month0 >= 0 && month0 <= 11)
                weekday = WeekdaySunday0(DaysFromCivil(year, month0 + 1, day));
            if (yearday == 367 && day != 0 && month0 >= 0 && month0 <= 11)
                yearday = (int)(DaysFromCivil(year, month0 + 1, day) - DaysFromCivil(year, 1, 1));
            var array = new JsonArray(
                JsonValue.Create(year),
                JsonValue.Create(month0),
                JsonValue.Create(day),
                JsonValue.Create(hour),
                JsonValue.Create(minute),
                JsonValue.Create(second),
                JsonValue.Create(weekday),
                JsonValue.Create(yearday));
            for (int index = 0; index < 9; index++)
                context.Budget.ChargeNode();
            if (_position < text.Length)
            {
                string rest = text[_position..];
                context.Budget.ChargeString(rest.Length);
                array.Add(JsonValue.Create(rest));
            }
            return array;
        }

        internal long ToEpochSeconds()
        {
            long year = _hasIsoYear ? _isoYear : _hasYear ? _year : 1900L;
            int month0 = 0;
            int day = 0;
            if (_hasMonth)
                month0 = _month0;
            if (_hasDay)
                day = _day;
            int hour = _hasHour ? _hour : 0;
            if (_hasHour12 && _hasAmPm)
                hour = (_hour12 % 12) + (_isPm ? 12 : 0);
            else if (_hasHour12)
                hour = _hour12;
            int minute = _hasMinute ? _minute : 0;
            int second = _hasSecond ? _second : 0;
            return JqTime.ToEpochSeconds(year, month0, day, hour, minute, second);
        }

        private bool ParseFormat(string pattern)
        {
            for (int index = 0; index < pattern.Length; index++)
            {
                if (pattern[index] != (char)37)
                {
                    if (_position >= text.Length || text[_position] != pattern[index])
                        return false;
                    _position++;
                    continue;
                }
                index++;
                if (index >= pattern.Length || !ParseDirective(pattern[index]))
                    return false;
            }
            return true;
        }

        private static bool IsSpace(char value) => value == (char)32 || (value >= (char)9 && value <= (char)13);

        private bool TakeDigits(int maxLength, out long value)
        {
            value = 0;
            int count = 0;
            while (count < maxLength && _position < text.Length && text[_position] >= (char)48 && text[_position] <= (char)57)
            {
                int digit = text[_position] - (char)48;
                if (value > (long.MaxValue - digit) / 10)
                    value = long.MaxValue;
                else
                    value = (value * 10) + digit;
                _position++;
                count++;
            }
            return count > 0;
        }

        private bool TakeSpaces()
        {
            while (_position < text.Length && text[_position] == (char)32)
                _position++;
            return true;
        }

        private static bool StartsWithIgnoreCase(string text, int position, string word)
        {
            if (position + word.Length > text.Length)
                return false;
            for (int index = 0; index < word.Length; index++)
            {
                char actual = text[position + index];
                char wanted = word[index];
                if (actual == wanted)
                    continue;
                if (actual >= (char)65 && actual <= (char)90)
                    actual = (char)(actual + 32);
                if (wanted >= (char)65 && wanted <= (char)90)
                    wanted = (char)(wanted + 32);
                if (actual != wanted)
                    return false;
            }
            return true;
        }

        private bool TakeName(string[] full, string[] abbr, out int index)
        {
            for (int value = 0; value < full.Length; value++)
            {
                if (StartsWithIgnoreCase(text, _position, full[value]))
                {
                    _position += full[value].Length;
                    index = value;
                    return true;
                }
            }
            for (int value = 0; value < abbr.Length; value++)
            {
                if (StartsWithIgnoreCase(text, _position, abbr[value]))
                {
                    _position += abbr[value].Length;
                    index = value;
                    return true;
                }
            }
            index = 0;
            return false;
        }

        private bool ParseDirective(char spec)
        {
            switch (spec.ToString())
            {
                case "%":
                    if (_position >= text.Length || text[_position] != (char)37)
                        return false;
                    _position++;
                    return true;
                case "Y":
                    {
                        bool negative = false;
                        if (_position < text.Length && (text[_position] == (char)45 || text[_position] == (char)43))
                            negative = text[_position++] == (char)45;
                        if (!TakeDigits(4, out long year))
                            return false;
                        _year = negative ? -year : year;
                        _hasYear = true;
                        return true;
                    }
                case "G":
                    {
                        bool negative = false;
                        if (_position < text.Length && (text[_position] == (char)45 || text[_position] == (char)43))
                            negative = text[_position++] == (char)45;
                        if (!TakeDigits(4, out long isoYear))
                            return false;
                        _isoYear = negative ? -isoYear : isoYear;
                        _hasIsoYear = true;
                        return true;
                    }
                case "y":
                    if (!TakeDigits(2, out long shortYear))
                        return false;
                    _year = shortYear >= 69 ? 1900L + shortYear : 2000L + shortYear;
                    _hasYear = true;
                    return true;
                case "m":
                    if (!TakeDigits(2, out long month) || month < 1 || month > 12)
                        return false;
                    _month0 = (int)month - 1;
                    _hasMonth = true;
                    return true;
                case "d":
                    if (!TakeDigits(2, out long day) || day < 1 || day > 31)
                        return false;
                    _day = (int)day;
                    _hasDay = true;
                    return true;
                case "e":
                    TakeSpaces();
                    if (!TakeDigits(2, out long spaceDay) || spaceDay < 1 || spaceDay > 31)
                        return false;
                    _day = (int)spaceDay;
                    _hasDay = true;
                    return true;
                case "H":
                    if (!TakeDigits(2, out long hour) || hour < 0 || hour > 23)
                        return false;
                    _hour = (int)hour;
                    _hasHour = true;
                    return true;
                case "I":
                    if (!TakeDigits(2, out long hour12) || hour12 < 1 || hour12 > 12)
                        return false;
                    _hour12 = (int)hour12;
                    _hasHour12 = true;
                    return true;
                case "M":
                    if (!TakeDigits(2, out long minute) || minute < 0 || minute > 59)
                        return false;
                    _minute = (int)minute;
                    _hasMinute = true;
                    return true;
                case "S":
                    if (!TakeDigits(2, out long second) || second < 0 || second > 61)
                        return false;
                    _second = (int)second;
                    _hasSecond = true;
                    return true;
                case "p":
                    if (StartsWithIgnoreCase(text, _position, "am"))
                    {
                        _position += 2;
                        _isPm = false;
                    }
                    else if (StartsWithIgnoreCase(text, _position, "pm"))
                    {
                        _position += 2;
                        _isPm = true;
                    }
                    else
                    {
                        return false;
                    }
                    _hasAmPm = true;
                    return true;
                case "z":
                    return TakeZoneOffset();
                case "Z":
                    return TakeZoneName();
                case "j":
                    if (!TakeDigits(3, out long yearday) || yearday < 1 || yearday > 366)
                        return false;
                    _yearday = (int)yearday - 1;
                    _hasYearday = true;
                    return true;
                case "U":
                    if (!TakeDigits(2, out long weekU) || weekU < 0 || weekU > 53)
                        return false;
                    _week = (int)weekU;
                    _weekMonday = false;
                    _hasWeek = true;
                    return true;
                case "W":
                    if (!TakeDigits(2, out long weekW) || weekW < 0 || weekW > 53)
                        return false;
                    _week = (int)weekW;
                    _weekMonday = true;
                    _hasWeek = true;
                    return true;
                case "V":
                    if (!TakeDigits(2, out long isoWeek) || isoWeek < 1 || isoWeek > 53)
                        return false;
                    _isoWeek = (int)isoWeek;
                    _hasIsoWeek = true;
                    return true;
                case "u":
                    if (_position >= text.Length || text[_position] < (char)49 || text[_position] > (char)55)
                        return false;
                    _weekday = (text[_position] - (char)48) % 7;
                    _position++;
                    _hasWeekday = true;
                    return true;
                case "w":
                    if (_position >= text.Length || text[_position] < (char)48 || text[_position] > (char)54)
                        return false;
                    _weekday = text[_position] - (char)48;
                    _position++;
                    _hasWeekday = true;
                    return true;
                case "a":
                case "A":
                    if (!TakeName(WeekdayNames, WeekdayAbbrs, out int weekday))
                        return false;
                    _weekday = weekday;
                    _hasWeekday = true;
                    return true;
                case "b":
                case "B":
                case "h":
                    if (!TakeName(MonthNames, MonthAbbrs, out int monthIndex))
                        return false;
                    _month0 = monthIndex;
                    _hasMonth = true;
                    return true;
                case "s":
                    {
                        bool negativeEpoch = false;
                        if (_position < text.Length && (text[_position] == (char)45 || text[_position] == (char)43))
                            negativeEpoch = text[_position++] == (char)45;
                        if (!TakeDigits(19, out long epoch))
                            return false;
                        if (negativeEpoch)
                            epoch = epoch == long.MaxValue ? long.MinValue : -epoch;
                        BrokenDown origin = FromEpochUtc(epoch, 0.0);
                        _year = origin.Year;
                        _hasYear = true;
                        _month0 = origin.Month - 1;
                        _hasMonth = true;
                        _day = origin.Day;
                        _hasDay = true;
                        _hour = origin.Hour;
                        _hasHour = true;
                        _minute = origin.Minute;
                        _hasMinute = true;
                        _second = (int)origin.Second;
                        _hasSecond = true;
                        _weekday = origin.Weekday;
                        _hasWeekday = true;
                        _yearday = origin.YearDay;
                        _hasYearday = true;
                        return true;
                    }
                case "c":
                    return ParseFormat("%a %b %e %H:%M:%S %Y");
                case "x":
                    return ParseFormat("%m/%d/%y");
                case "X":
                    return ParseFormat("%H:%M:%S");
                case "n":
                case "t":
                    while (_position < text.Length && IsSpace(text[_position]))
                        _position++;
                    return true;
                default:
                    return false;
            }
        }

        private bool TakeZoneOffset()
        {
            if (_position >= text.Length || (text[_position] != (char)43 && text[_position] != (char)45))
                return false;
            _position++;
            if (!TakeDigits(2, out long hours) || hours < 0 || hours > 23)
                return false;
            if (_position < text.Length && text[_position] == (char)58)
            {
                _position++;
                if (!TakeDigits(2, out long colonMinutes) || colonMinutes < 0 || colonMinutes > 59)
                    return false;
            }
            else
            {
                if (!TakeDigits(2, out long plainMinutes) || plainMinutes < 0 || plainMinutes > 59)
                    return false;
            }
            return true;
        }

        private bool TakeZoneName()
        {
            int start = _position;
            while (_position < text.Length && IsZoneChar(text[_position]))
                _position++;
            return _position > start;
        }

        private static bool IsZoneChar(char value) =>
            (value >= (char)65 && value <= (char)90) || (value >= (char)97 && value <= (char)122) || (value >= (char)48 && value <= (char)57) || value == (char)95 || value == (char)47 || value == (char)43 || value == (char)45;

        private void SundayWeekToCivil(long year, out int month, out int day, out int yearday)
        {
            int weekday = _hasWeekday ? _weekday : _weekMonday ? 1 : 0;
            int wanted = _weekMonday ? (weekday + 6) % 7 : weekday;
            long january = DaysFromCivil(year, 1, 1);
            int firstDow = _weekMonday ? (WeekdaySunday0(january) + 6) % 7 : WeekdaySunday0(january);
            int first = (7 - firstDow) % 7;
            long target = january + ((long)_week - 1) * 7 + first + wanted;
            CivilFromDays(target, out _, out month, out day);
            yearday = (int)(target - DaysFromCivil(year, 1, 1));
        }

        private void IsoWeekToCivil(long isoYear, out int month, out int day, out int yearday)
        {
            int weekday = _hasWeekday ? ((_weekday + 6) % 7) + 1 : 1;
            long january4 = DaysFromCivil(isoYear, 1, 4);
            long week1monday = january4 - ((WeekdaySunday0(january4) + 6) % 7);
            long target = week1monday + ((long)_isoWeek - 1) * 7 + (weekday - 1);
            CivilFromDays(target, out long actualYear, out month, out day);
            yearday = (int)(target - DaysFromCivil(actualYear, 1, 1));
        }
}
}
