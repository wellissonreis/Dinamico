using System.Diagnostics;
using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Timing;

public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public static StopwatchMonotonicClock Instance { get; } = new();

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        ConvertElapsed(startingTimestamp, endingTimestamp, Stopwatch.Frequency);

    // Int128 prevents intermediate overflow; the final conversion is checked and rounds down
    // to the contract's 100 ns unit, without floating-point loss of precision.
    internal static TimeSpan ConvertElapsed(long start, long end, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(end), "Monotonic time moved backwards.");
        Int128 ticks = ((Int128)end - start) * TimeSpan.TicksPerSecond / frequency;
        return TimeSpan.FromTicks(checked((long)ticks));
    }
}
