using System.Collections;
using System.Globalization;
using System.Reflection;
using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Tests;

public sealed class RetryAfterObservationTests
{
    private static readonly DateTimeOffset Reference = new(1994, 11, 6, 8, 49, 37, TimeSpan.Zero);

    [Fact]
    public void Default_missing_and_not_applicable_are_distinct_empty_evidence()
    {
        var notObserved = default(RetryAfterObservation);
        var notApplicable = RetryAfterObservation.Parse(200, null, Reference);
        Assert.Equal(0, (int)RetryAfterKind.NotObserved);
        AssertEmpty(notObserved, RetryAfterKind.NotObserved);
        AssertEmpty(new RetryAfterObservation(), RetryAfterKind.NotObserved);
        AssertEmpty(RetryAfterObservation.Missing, RetryAfterKind.Missing);
        AssertEmpty(notApplicable, RetryAfterKind.NotApplicable);
        Assert.NotEqual(notObserved, RetryAfterObservation.Missing);
        Assert.NotEqual(notObserved, notApplicable);
        Assert.NotEqual(RetryAfterObservation.Missing, notApplicable);
        Assert.Equal(128, RetryAfterObservation.MaxFieldCharacters);
        Assert.Equal(86400, RetryAfterObservation.MaxDelaySeconds);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(404)]
    [InlineData(428)]
    [InlineData(430)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void Other_statuses_ignore_all_retry_after_evidence(int status)
    {
        IReadOnlyList<string>?[] fields =
        [
            null, [], ["120"], ["Sun, 06 Nov 1994 08:51:37 GMT"],
            ["120", "120"], [new string('9', 129)], [null!], new UnreadableValues(throwsOnCount: true)
        ];
        foreach (var values in fields)
        {
            var observation = RetryAfterObservation.Parse(status, values, DateTimeOffset.MaxValue);
            AssertEmpty(observation, RetryAfterKind.NotApplicable);
            Assert.NotEqual(default(RetryAfterObservation), observation);
        }
    }

    [Fact]
    public void Absent_list_or_empty_list_is_missing_but_null_field_is_invalid()
    {
        Assert.Equal(RetryAfterObservation.Missing, RetryAfterObservation.Parse(429, null, Reference));
        Assert.Equal(RetryAfterObservation.Missing, RetryAfterObservation.Parse(429, [], Reference));
        foreach (string? field in new string?[] { null, "", " \t ", "invalid" })
            AssertEmpty(RetryAfterObservation.Parse(429, [field!], Reference), RetryAfterKind.Invalid);
    }

    [Fact]
    public void Duplicate_fields_are_invalid_even_when_identical_and_are_never_read()
    {
        IReadOnlyList<string>[] fields =
        [
            ["120", "120"], ["120", "121"], ["", "0"], [null!, "0"],
            ["Sun, 06 Nov 1994 08:51:37 GMT", "Sun, 06 Nov 1994 08:51:37 GMT"],
            ["1", "2", "3"], new UnreadableValues(throwsOnCount: false)
        ];
        foreach (var values in fields)
            AssertEmpty(RetryAfterObservation.Parse(429, values, Reference), RetryAfterKind.Invalid);
    }

    [Theory]
    [InlineData("0", 0, false)]
    [InlineData("1", 1, false)]
    [InlineData("000000", 0, false)]
    [InlineData("000120", 120, false)]
    [InlineData("86399", 86399, false)]
    [InlineData("86400", 86400, false)]
    [InlineData("86401", 86400, true)]
    [InlineData("2147483648", 86400, true)]
    [InlineData("9223372036854775808", 86400, true)]
    [InlineData("18446744073709551616", 86400, true)]
    [InlineData(" \t120\t ", 120, false)]
    public void Ascii_delta_seconds_are_normalized_and_clamped(string field, int seconds, bool clamped)
    {
        var observation = Parse(field);
        Assert.Equal(RetryAfterKind.DeltaSeconds, observation.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), observation.Delay);
        Assert.Null(observation.ReferenceUtc);
        Assert.False(observation.ClockUncertain);
        Assert.Equal(clamped, observation.WasClamped);
    }

    [Fact]
    public void Maximum_length_numbers_do_not_overflow_or_treat_leading_zeroes_as_clamping()
    {
        var huge = Parse(new string('9', 128));
        Assert.Equal(RetryAfterKind.DeltaSeconds, huge.Kind);
        Assert.Equal(TimeSpan.FromSeconds(86400), huge.Delay);
        Assert.True(huge.WasClamped);

        var zero = Parse(new string('0', 128));
        Assert.Equal(TimeSpan.Zero, zero.Delay);
        Assert.False(zero.WasClamped);
        var one = Parse(new string('0', 127) + "1");
        Assert.Equal(TimeSpan.FromSeconds(1), one.Delay);
        Assert.False(one.WasClamped);
        var exactLimit = Parse(new string('0', 123) + "86400");
        Assert.Equal(TimeSpan.FromSeconds(86400), exactLimit.Delay);
        Assert.False(exactLimit.WasClamped);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("+")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    [InlineData("\u0661")]
    [InlineData("\uff11")]
    public void Numeric_saturation_does_not_hide_invalid_suffixes(string suffix)
    {
        AssertEmpty(Parse(new string('9', 126) + suffix + "9"), RetryAfterKind.Invalid);
        if (suffix is not " " and not "\t")
            AssertEmpty(Parse(new string('9', 127) + suffix), RetryAfterKind.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("1 2")]
    [InlineData("1\t2")]
    [InlineData("1,2")]
    [InlineData("120, 120")]
    [InlineData("\"120\"")]
    [InlineData("\u0661\u0662\u0660")]
    [InlineData("\uff11\uff12\uff10")]
    [InlineData("\u00b2")]
    [InlineData("\u22121")]
    [InlineData("\u00a0120")]
    [InlineData("120\u00a0")]
    [InlineData("\u2003120")]
    [InlineData("120\u200b")]
    [InlineData("\v120")]
    [InlineData("120\f")]
    [InlineData("\r120")]
    [InlineData("120\n")]
    [InlineData("120\r\n")]
    [InlineData("120\0")]
    public void Non_ascii_digits_signs_and_non_ows_whitespace_are_invalid(string field)
    {
        AssertEmpty(Parse(field), RetryAfterKind.Invalid);
    }

    [Fact]
    public void Field_length_is_limited_before_trimming_ows()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), Parse(new string(' ', 127) + "1").Delay);
        AssertEmpty(Parse(new string(' ', 128) + "1"), RetryAfterKind.Invalid);
        AssertEmpty(Parse(new string('0', 129)), RetryAfterKind.Invalid);

        const string date = "Sun, 06 Nov 1994 08:51:37 GMT";
        Assert.Equal(RetryAfterKind.HttpDate, Parse(date.PadRight(128, '\t')).Kind);
        AssertEmpty(Parse(date.PadRight(129, '\t')), RetryAfterKind.Invalid);
    }

    [Theory]
    [InlineData("Sun, 06 Nov 1994 08:51:37 GMT")]
    [InlineData("Sunday, 06-Nov-94 08:51:37 GMT")]
    [InlineData("Sun Nov  6 08:51:37 1994")]
    [InlineData("Sun Nov 06 08:51:37 1994")]
    [InlineData(" \tSun, 06 Nov 1994 08:51:37 GMT\t ")]
    public void All_http_date_formats_use_the_supplied_reference(string field)
    {
        AssertDate(Parse(field), Reference, TimeSpan.FromSeconds(120), clamped: false);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void English_months_and_weekdays_work_in_each_http_format(int month)
    {
        var target = new DateTimeOffset(2000, month, 15, 12, 34, 56, TimeSpan.Zero);
        var reference = target.AddSeconds(-1);
        string[] fields =
        [
            HttpDate(target), ObsoleteDate(target),
            target.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)
        ];
        foreach (string field in fields)
            AssertDate(RetryAfterObservation.Parse(429, [field], reference), reference, TimeSpan.FromSeconds(1), false);
    }

    [Fact]
    public void Gregorian_leap_days_are_valid_and_overdue_dates_keep_clock_evidence()
    {
        var target = new DateTimeOffset(2000, 2, 29, 0, 0, 0, TimeSpan.Zero);
        var reference = target.AddSeconds(1);
        AssertDate(RetryAfterObservation.Parse(429, [HttpDate(target)], reference),
            reference, TimeSpan.Zero, false);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(target)], reference),
            reference, TimeSpan.Zero, false);
    }

    [Theory]
    [InlineData(-1, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(86400, 86400, false)]
    [InlineData(86401, 86400, true)]
    public void Date_delays_floor_past_dates_at_zero_and_clamp_only_above_the_limit(
        int dateOffset, int delay, bool clamped)
    {
        var observation = Parse(HttpDate(Reference.AddSeconds(dateOffset)));
        AssertDate(observation, Reference, TimeSpan.FromSeconds(delay), clamped);
    }

    [Fact]
    public void Date_delays_preserve_fractional_reference_ticks_and_normalize_its_offset()
    {
        var target = Reference.AddSeconds(1);
        var offsetReference = Reference.AddTicks(1).ToOffset(TimeSpan.FromHours(-3));
        string[] fields =
        [
            HttpDate(target), ObsoleteDate(target),
            target.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)
        ];
        foreach (string field in fields)
        {
            var observation = RetryAfterObservation.Parse(429, [field], offsetReference);
            AssertDate(observation, Reference.AddTicks(1), TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1), false);
            Assert.Equal(TimeSpan.Zero, observation.ReferenceUtc!.Value.Offset);
        }

        var justAboveLimit = Reference.AddTicks(-1);
        AssertDate(RetryAfterObservation.Parse(429, [HttpDate(Reference.AddDays(1))], justAboveLimit),
            justAboveLimit, TimeSpan.FromDays(1), true);
    }

    [Fact]
    public void Two_digit_years_follow_the_reference_century_including_century_rollover()
    {
        foreach (int referenceYear in new[] { 1945, 2045, 2145 })
        {
            var reference = new DateTimeOffset(referenceYear, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var target = new DateTimeOffset(referenceYear + 49, 11, 6, 8, 49, 37, TimeSpan.Zero);
            AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(target)], reference),
                reference, TimeSpan.FromDays(1), true);
        }

        var centuryEnd = new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(centuryEnd.AddSeconds(1))], centuryEnd),
            centuryEnd, TimeSpan.FromSeconds(1), false);
    }

    [Fact]
    public void Two_digit_years_use_the_exact_fifty_year_boundary()
    {
        var reference = new DateTimeOffset(2044, 11, 6, 8, 49, 37, TimeSpan.Zero);
        var boundary = reference.AddYears(50);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(boundary)], reference),
            reference, TimeSpan.FromDays(1), true);

        // The weekday belongs to the resolved past year, not the provisional future century.
        var past = boundary.AddSeconds(1).AddYears(-100);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(past)], reference),
            reference, TimeSpan.Zero, false);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(boundary)], reference.AddTicks(1)),
            reference.AddTicks(1), TimeSpan.FromDays(1), true);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(boundary)], reference.ToOffset(TimeSpan.FromHours(14))),
            reference, TimeSpan.FromDays(1), true);
    }

    [Fact]
    public void Leap_day_reference_and_calendar_extremes_do_not_overflow()
    {
        var leapReference = new DateTimeOffset(2000, 2, 29, 12, 0, 0, TimeSpan.Zero);
        var boundary = leapReference.AddYears(50);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(boundary)], leapReference),
            leapReference, TimeSpan.FromDays(1), true);
        var past = boundary.AddSeconds(1).AddYears(-100);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(past)], leapReference),
            leapReference, TimeSpan.Zero, false);

        var first = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        AssertDate(RetryAfterObservation.Parse(429, [HttpDate(first)], DateTimeOffset.MinValue),
            DateTimeOffset.MinValue, TimeSpan.FromDays(1), true);
        AssertDate(RetryAfterObservation.Parse(429, [HttpDate(first)], DateTimeOffset.MaxValue),
            DateTimeOffset.MaxValue, TimeSpan.Zero, false);
        var last = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        AssertDate(RetryAfterObservation.Parse(429, [HttpDate(last)], DateTimeOffset.MaxValue),
            DateTimeOffset.MaxValue, TimeSpan.Zero, false);
        AssertDate(RetryAfterObservation.Parse(429, [ObsoleteDate(last)], last),
            last, TimeSpan.Zero, false);
        AssertEmpty(RetryAfterObservation.Parse(429, ["Monday, 01-Jan-00 00:00:00 GMT"], last),
            RetryAfterKind.Invalid);
    }

    [Theory]
    [InlineData("Sat, 31 Dec 2016 23:59:60 GMT")]
    [InlineData("Saturday, 31-Dec-16 23:59:60 GMT")]
    [InlineData("Sat Dec 31 23:59:60 2016")]
    public void Http_leap_seconds_are_normalized_without_reading_a_clock(string field)
    {
        var reference = new DateTimeOffset(2016, 12, 31, 23, 59, 59, TimeSpan.Zero);
        AssertDate(RetryAfterObservation.Parse(429, [field], reference), reference, TimeSpan.FromSeconds(1), false);
    }

    [Fact]
    public void Leap_second_at_the_upper_calendar_boundary_is_still_bounded()
    {
        var reference = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        AssertDate(RetryAfterObservation.Parse(429, ["Fri, 31 Dec 9999 23:59:60 GMT"], reference),
            reference, TimeSpan.FromSeconds(1), false);
    }

    [Theory]
    [InlineData("Sun, 06 Nov 1994 08:51:37 GMT")]
    [InlineData("Sunday, 06-Nov-94 08:51:37 GMT")]
    [InlineData("Sun Nov  6 08:51:37 1994")]
    public void Truncated_dates_and_embedded_control_characters_are_invalid(string field)
    {
        for (int length = 0; length < field.Length; length++)
            AssertEmpty(Parse(field[..length]), RetryAfterKind.Invalid);

        for (int index = 0; index < field.Length; index++)
        {
            char[] corrupted = field.ToCharArray();
            corrupted[index] = '\0';
            AssertEmpty(Parse(new string(corrupted)), RetryAfterKind.Invalid);
        }
    }

    [Theory]
    [InlineData("sun, 06 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 06 nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 gmt")]
    [InlineData("Mon, 06 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 UTC")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 +0000")]
    [InlineData("Sun, 6 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 94 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 8:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49:37.0 GMT")]
    [InlineData("Sun,\t06 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun,  06 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 GMT\r\n")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 GMT\u00a0")]
    [InlineData("Sun, 0\u0666 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 GMT, 120")]
    [InlineData("1994-11-06T08:49:37Z")]
    [InlineData("Sun, 00 Nov 1994 08:49:37 GMT")]
    [InlineData("Sun, 31 Nov 1994 08:49:37 GMT")]
    [InlineData("Thu, 29 Feb 1900 08:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 24:49:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:60:37 GMT")]
    [InlineData("Sun, 06 Nov 1994 08:49:61 GMT")]
    [InlineData("Sat, 31 Dec 2016 12:00:60 GMT")]
    [InlineData("Saturday, 31-Dec-16 12:00:60 GMT")]
    [InlineData("Sat Dec 31 12:00:60 2016")]
    [InlineData("Sat, 31 Dec 2016 23:58:60 GMT")]
    [InlineData("Sat, 31 Dec 2016 22:59:60 GMT")]
    [InlineData("Mon, 01 Jan 0001 00:00:00 GMT")]
    [InlineData("Sun, 06 Nov 0000 08:49:37 GMT")]
    [InlineData("Sun, 06 Xxx 1994 08:49:37 GMT")]
    [InlineData("Sunday, 06-Nov-1994 08:49:37 GMT")]
    [InlineData("Sun, 06-Nov-94 08:49:37 GMT")]
    [InlineData("Sunday, 6-Nov-94 08:49:37 GMT")]
    [InlineData("Sunday, 06-nov-94 08:49:37 GMT")]
    [InlineData("Monday, 06-Nov-94 08:49:37 GMT")]
    [InlineData("Sunday, 31-Nov-94 08:49:37 GMT")]
    [InlineData("Sunday, 06-Nov-94 24:49:37 GMT")]
    [InlineData("Sunday, 06-Nov-94 08:60:37 GMT")]
    [InlineData("Sunday, 06-Nov-94 08:49:61 GMT")]
    [InlineData("Sunday, 06-Nov-94 08:49:37 UTC")]
    [InlineData("Sun Nov 6 08:49:37 1994")]
    [InlineData("Sun Nov   6 08:49:37 1994")]
    [InlineData("Sun Nov\t 6 08:49:37 1994")]
    [InlineData("Sun Nov  6 08:49:37 1994 GMT")]
    [InlineData("Mon Nov  6 08:49:37 1994")]
    [InlineData("Sun Nov 31 08:49:37 1994")]
    [InlineData("Sun Nov  6 24:49:37 1994")]
    [InlineData("Sun Nov  6 08:60:37 1994")]
    [InlineData("Sun Nov  6 08:49:61 1994")]
    public void Non_http_formats_and_semantically_invalid_dates_are_invalid(string field)
    {
        AssertEmpty(Parse(field), RetryAfterKind.Invalid);
    }

    [Fact]
    public void Parsing_is_independent_of_current_culture_and_evidence_does_not_retain_raw_text()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            AssertDate(Parse("Sun, 06 Nov 1994 08:51:37 GMT"), Reference, TimeSpan.FromSeconds(120), false);
            Assert.Equal(TimeSpan.FromSeconds(120), Parse("120").Delay);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        var type = typeof(RetryAfterObservation);
        Assert.DoesNotContain(type.GetConstructors(), constructor => constructor.GetParameters().Length != 0);
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.DoesNotContain(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => field.FieldType == typeof(string));
        const string rawCanary = "secret-invalid-header-canary";
        Assert.DoesNotContain(rawCanary, Parse(rawCanary).ToString());
    }

    private static RetryAfterObservation Parse(string field) =>
        RetryAfterObservation.Parse(429, [field], Reference);

    private static string HttpDate(DateTimeOffset date) => date.ToString("r", CultureInfo.InvariantCulture);

    private static string ObsoleteDate(DateTimeOffset date) =>
        date.ToString("dddd, dd-MMM-yy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    private static void AssertEmpty(RetryAfterObservation observation, RetryAfterKind kind)
    {
        Assert.Equal(kind, observation.Kind);
        Assert.Null(observation.Delay);
        Assert.Null(observation.ReferenceUtc);
        Assert.False(observation.ClockUncertain);
        Assert.False(observation.WasClamped);
    }

    private static void AssertDate(RetryAfterObservation observation, DateTimeOffset reference,
        TimeSpan delay, bool clamped)
    {
        Assert.Equal(RetryAfterKind.HttpDate, observation.Kind);
        Assert.Equal(delay, observation.Delay);
        Assert.Equal(reference, observation.ReferenceUtc);
        Assert.True(observation.ClockUncertain);
        Assert.Equal(clamped, observation.WasClamped);
    }

    private sealed class UnreadableValues(bool throwsOnCount) : IReadOnlyList<string>
    {
        public int Count => throwsOnCount ? throw new InvalidOperationException() : 2;
        public string this[int index] => throw new InvalidOperationException();
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
