namespace Dinamico.LoadEngine.Contracts;

public enum RetryAfterKind
{
    NotObserved = 0,
    NotApplicable,
    Missing,
    Invalid,
    DeltaSeconds,
    HttpDate
}

/// <summary>Immutable Retry-After evidence without field text; default means capture was not performed.</summary>
public readonly record struct RetryAfterObservation
{
    public const int MaxFieldCharacters = 128;
    public const int MaxDelaySeconds = 86400;

    private RetryAfterObservation(RetryAfterKind kind, TimeSpan? delay = null,
        DateTimeOffset? referenceUtc = null, bool wasClamped = false)
    {
        bool valid = kind switch
        {
            RetryAfterKind.NotApplicable or RetryAfterKind.Missing or RetryAfterKind.Invalid =>
                delay is null && referenceUtc is null && !wasClamped,
            RetryAfterKind.DeltaSeconds => delay.HasValue && referenceUtc is null,
            RetryAfterKind.HttpDate => delay.HasValue && referenceUtc.HasValue
                && referenceUtc.Value.Offset == TimeSpan.Zero,
            _ => false
        };
        if (!valid || delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(MaxDelaySeconds)
            || (wasClamped && delay != TimeSpan.FromSeconds(MaxDelaySeconds)))
            throw new ArgumentException("Inconsistent Retry-After observation.");

        Kind = kind;
        Delay = delay;
        ReferenceUtc = referenceUtc;
        WasClamped = wasClamped;
    }

    public RetryAfterKind Kind { get; }
    public TimeSpan? Delay { get; }
    public DateTimeOffset? ReferenceUtc { get; }
    public bool ClockUncertain => Kind == RetryAfterKind.HttpDate;
    public bool WasClamped { get; }

    public static RetryAfterObservation Missing => new(RetryAfterKind.Missing);

    public static RetryAfterObservation Parse(int statusCode, IReadOnlyList<string>? values,
        DateTimeOffset referenceUtc)
    {
        if (statusCode != 429)
            return new(RetryAfterKind.NotApplicable);
        if (values is null || values.Count == 0)
            return Missing;
        if (values.Count != 1)
            return new(RetryAfterKind.Invalid);

        string? value = values[0];
        if (value is null || value.Length > MaxFieldCharacters)
            return new(RetryAfterKind.Invalid);

        ReadOnlySpan<char> field = value.AsSpan().Trim(" \t");
        if (field.IsEmpty)
            return new(RetryAfterKind.Invalid);

        if (field[0] is >= '0' and <= '9')
        {
            int seconds = 0;
            foreach (char character in field)
            {
                if (character is < '0' or > '9')
                    return new(RetryAfterKind.Invalid);
                // Keep one sentinel above the limit and validate every remaining character.
                seconds = Math.Min(MaxDelaySeconds + 1, seconds * 10 + character - '0');
            }

            return new(RetryAfterKind.DeltaSeconds,
                TimeSpan.FromSeconds(Math.Min(seconds, MaxDelaySeconds)),
                wasClamped: seconds > MaxDelaySeconds);
        }

        referenceUtc = referenceUtc.ToUniversalTime();
        if (!TryParseHttpDate(field, referenceUtc, out long dateTicks))
            return new(RetryAfterKind.Invalid);

        long delayTicks = Math.Max(0, dateTicks - referenceUtc.Ticks);
        long maxTicks = MaxDelaySeconds * TimeSpan.TicksPerSecond;
        return new(RetryAfterKind.HttpDate, TimeSpan.FromTicks(Math.Min(delayTicks, maxTicks)),
            referenceUtc, delayTicks > maxTicks);
    }

    // RFC 9110 section 5.6.7: IMF-fixdate, rfc850-date and asctime-date.
    // Parse components ourselves: general date helpers can consult the current clock for yy.
    private static bool TryParseHttpDate(ReadOnlySpan<char> field, DateTimeOffset referenceUtc,
        out long dateTicks)
    {
        dateTicks = 0;
        int weekday, day, month, year;
        ReadOnlySpan<char> time;
        bool twoDigitYear = false;

        if (field.Length == 29 && field[3] == ',')
        {
            if (field[4] != ' ' || field[7] != ' ' || field[11] != ' '
                || field[16] != ' ' || field[25] != ' ' || !field[26..].SequenceEqual("GMT"))
                return false;
            weekday = ShortWeekday(field[..3]);
            day = Digits(field.Slice(5, 2));
            month = Month(field.Slice(8, 3));
            year = Digits(field.Slice(12, 4));
            time = field.Slice(17, 8);
        }
        else if (field.Length == 24 && field[3] == ' ')
        {
            if (field[7] != ' ' || field[10] != ' ' || field[19] != ' ')
                return false;
            weekday = ShortWeekday(field[..3]);
            month = Month(field.Slice(4, 3));
            day = field[8] == ' ' ? Digits(field.Slice(9, 1)) : Digits(field.Slice(8, 2));
            year = Digits(field.Slice(20, 4));
            time = field.Slice(11, 8);
        }
        else
        {
            int comma = field.IndexOf(',');
            if (comma is < 6 or > 9 || field.Length != comma + 24 || field[comma + 1] != ' ')
                return false;
            weekday = LongWeekday(field[..comma]);
            ReadOnlySpan<char> tail = field[(comma + 2)..];
            if (tail[2] != '-' || tail[6] != '-' || tail[9] != ' '
                || tail[18] != ' ' || !tail[19..].SequenceEqual("GMT"))
                return false;
            day = Digits(tail[..2]);
            month = Month(tail.Slice(3, 3));
            year = Digits(tail.Slice(7, 2));
            time = tail.Slice(10, 8);
            twoDigitYear = true;
        }

        if (time[2] != ':' || time[5] != ':')
            return false;
        int hour = Digits(time[..2]);
        int minute = Digits(time.Slice(3, 2));
        int second = Digits(time.Slice(6, 2));
        if (weekday < 0 || month is < 1 or > 12 || day is < 1 or > 31 || year < 0
            || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 60
            || (second == 60 && (hour != 23 || minute != 59)))
            return false;

        if (twoDigitYear)
            year = ResolveYear(year, month, day, hour, minute, second, referenceUtc);

        // RFC 5322 section 3.3 supplies calendar validity, weekday consistency and year >= 1900.
        if (year is < 1900 or > 9999 || day > DateTime.DaysInMonth(year, month))
            return false;
        var date = new DateTime(year, month, day, hour, minute, Math.Min(second, 59), DateTimeKind.Utc);
        if ((int)date.DayOfWeek != weekday)
            return false;

        // Represent a leap second by the following second, including the upper calendar boundary.
        dateTicks = date.Ticks + (second == 60 ? TimeSpan.TicksPerSecond : 0);
        return true;
    }

    private static int ResolveYear(int year, int month, int day, int hour, int minute, int second,
        DateTimeOffset referenceUtc)
    {
        // Choose the latest matching year no later than reference + 50 years. Compare components
        // so the fifty-year boundary is exact and reference years near 9999 cannot overflow.
        int limitYear = referenceUtc.Year + 50;
        int limitDay = referenceUtc.Day;
        if (referenceUtc.Month == 2 && limitDay == 29
            && !(limitYear % 4 == 0 && (limitYear % 100 != 0 || limitYear % 400 == 0)))
            limitDay = 28;

        int resolved = limitYear / 100 * 100 + year;
        if ((resolved, month, day, hour, minute, second).CompareTo(
            (limitYear, referenceUtc.Month, limitDay, referenceUtc.Hour, referenceUtc.Minute,
                referenceUtc.Second)) > 0)
            resolved -= 100;
        return resolved;
    }

    private static int Digits(ReadOnlySpan<char> digits)
    {
        int number = 0;
        foreach (char character in digits)
        {
            if (character is < '0' or > '9')
                return -1;
            number = number * 10 + character - '0';
        }
        return number;
    }

    private static int ShortWeekday(ReadOnlySpan<char> name) => name switch
    {
        "Sun" => 0,
        "Mon" => 1,
        "Tue" => 2,
        "Wed" => 3,
        "Thu" => 4,
        "Fri" => 5,
        "Sat" => 6,
        _ => -1
    };

    private static int LongWeekday(ReadOnlySpan<char> name) => name switch
    {
        "Sunday" => 0,
        "Monday" => 1,
        "Tuesday" => 2,
        "Wednesday" => 3,
        "Thursday" => 4,
        "Friday" => 5,
        "Saturday" => 6,
        _ => -1
    };

    private static int Month(ReadOnlySpan<char> name) => name switch
    {
        "Jan" => 1,
        "Feb" => 2,
        "Mar" => 3,
        "Apr" => 4,
        "May" => 5,
        "Jun" => 6,
        "Jul" => 7,
        "Aug" => 8,
        "Sep" => 9,
        "Oct" => 10,
        "Nov" => 11,
        "Dec" => 12,
        _ => -1
    };
}
