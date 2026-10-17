using System.Collections.Immutable;
using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Metrics;

/// <summary>Pure, fixed-memory fold of one contiguous local stream. Replays/gaps are errors, never zeros.</summary>
public sealed class MetricWindowTotals
{
    private MetricWindowTotals() { }
    public ulong WindowCount { get; private init; }
    public TimeSpan End { get; private init; }
    public ulong LastInFlight { get; private init; }
    public MeasurementQuality Quality { get; private init; }
    public ImmutableArray<ulong> Counters { get; private init; } = ImmutableArray.Create(new ulong[MetricWindow.CounterCount]);
    public ImmutableArray<ulong> StatusReceived { get; private init; } = ImmutableArray.Create(new ulong[MetricWindow.StatusCount]);
    public ImmutableArray<ulong> RetryAfterKinds { get; private init; } = ImmutableArray.Create(new ulong[MetricWindow.RetryKindCount]);
    public ulong Registrations { get; private init; }
    public ulong RegistrationDelaySumNanoseconds { get; private init; }
    public ulong RegistrationDelayMaxNanoseconds { get; private init; }
    public ulong RetryAfterClamped { get; private init; }
    public ulong RetryAfterClockUncertain { get; private init; }
    public TimeSpan? MinimumRetryAfter { get; private init; }
    public TimeSpan? MaximumRetryAfter { get; private init; }
    public HistogramSnapshot TerminalDuration { get; private init; } = new(new());
    public HistogramSnapshot HttpCompletedDuration { get; private init; } = new(new());
    private bool final;
    public static MetricWindowTotals Empty => new();
    public ulong this[MetricCounter counter] => Counters[(int)counter];
    public ulong FinalStatuses => MetricWindowCodec.Sum(StatusReceived);
    public ulong OtherFinalStatuses => checked(FinalStatuses - StatusReceived[329]);
    public double? RateLimitedPercent => FinalStatuses == 0 ? null : 100.0 * StatusReceived[329] / FinalStatuses;

    public MetricWindowTotals Append(MetricWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (final || window.WindowIndex != WindowCount || window.Start != End)
            throw new InvalidDataException("Missing, repeated or out-of-order metric window.");
        var result = new MetricWindowTotals
        {
            WindowCount = checked(WindowCount + 1), End = window.End, LastInFlight = window.InFlight,
            Quality = Quality | window.Quality, Counters = Add(Counters, window.Counters),
            StatusReceived = Add(StatusReceived, window.StatusReceived), RetryAfterKinds = Add(RetryAfterKinds, window.RetryAfterKinds),
            Registrations = checked(Registrations + window.Registrations),
            RegistrationDelaySumNanoseconds = checked(RegistrationDelaySumNanoseconds + window.RegistrationDelaySumNanoseconds),
            RegistrationDelayMaxNanoseconds = Math.Max(RegistrationDelayMaxNanoseconds, window.RegistrationDelayMaxNanoseconds),
            RetryAfterClamped = checked(RetryAfterClamped + window.RetryAfterClamped),
            RetryAfterClockUncertain = checked(RetryAfterClockUncertain + window.RetryAfterClockUncertain),
            MinimumRetryAfter = Min(MinimumRetryAfter, window.MinimumRetryAfter),
            MaximumRetryAfter = Max(MaximumRetryAfter, window.MaximumRetryAfter),
            TerminalDuration = new(LogHistogram.Merge(TerminalDuration.Histogram, window.TerminalDuration.Histogram)),
            HttpCompletedDuration = new(LogHistogram.Merge(HttpCompletedDuration.Histogram, window.HttpCompletedDuration.Histogram)),
            final = window.IsFinal
        };
        // Identities apply to the accumulated stream, not to individual windows.
        ulong terminalCount = result.TerminalDuration.Count;
        if (result[MetricCounter.Started] != checked(result.LastInFlight + terminalCount))
            throw new InvalidDataException("Inconsistent in-flight gauge and terminal population.");
        return result;
    }

    public bool HasCompleteCoverage(MetricWindowManifest manifest) => manifest.Complete && WindowCount != 0
        && manifest.LastWindowIndex == WindowCount - 1 && manifest.End == End;

    private static ImmutableArray<ulong> Add(ImmutableArray<ulong> left, ImmutableArray<ulong> right)
    {
        if (left.Length != right.Length) throw new InvalidDataException("Incompatible metric schema.");
        var values = new ulong[left.Length];
        for (int i = 0; i < values.Length; i++) values[i] = checked(left[i] + right[i]);
        return ImmutableArray.Create(values);
    }
    private static TimeSpan? Min(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a < b ? a : b;
    private static TimeSpan? Max(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
