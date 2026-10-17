namespace Dinamico.LoadEngine.Metrics;

/// <summary>A nearest-rank quantile with its population and explicit overflow.</summary>
public readonly record struct HistogramQuantile(ulong Count, ulong? UpperBoundNanoseconds, bool IsOverflow);

/// <summary>Fixed-memory histogram. Caller owns synchronization. Failed updates leave it unchanged.</summary>
public sealed class LogHistogram
{
    private readonly ulong[] counts = new ulong[LogHistogramSchema.BucketCount];

    public LogHistogram(uint schemaId = LogHistogramSchema.Id)
    {
        if (schemaId != LogHistogramSchema.Id)
            throw new ArgumentException("Unsupported histogram schema.", nameof(schemaId));
    }

    public uint SchemaId => LogHistogramSchema.Id;
    public ulong Count { get; private set; }
    public ulong SumNanoseconds { get; private set; }
    public ulong? MinimumNanoseconds { get; private set; }
    public ulong? MaximumNanoseconds { get; private set; }
    public ulong OverflowCount => counts[^1];
    public ReadOnlySpan<ulong> Counts => counts;
    public HistogramQuantile? P50 => Quantile(50);
    public HistogramQuantile? P95 => Quantile(95);
    public HistogramQuantile? P99 => Quantile(99);

    public void Record(ulong nanoseconds)
    {
        int bucket = LogHistogramSchema.Classify(nanoseconds);
        ulong nextCount = checked(Count + 1);
        ulong nextSum = checked(SumNanoseconds + nanoseconds);
        ulong nextBucket = checked(counts[bucket] + 1);
        counts[bucket] = nextBucket;
        Count = nextCount;
        SumNanoseconds = nextSum;
        MinimumNanoseconds = MinimumNanoseconds is { } min ? Math.Min(min, nanoseconds) : nanoseconds;
        MaximumNanoseconds = MaximumNanoseconds is { } max ? Math.Max(max, nanoseconds) : nanoseconds;
    }

    /// <summary>Percent is integral in [1,100]; null means an empty population.</summary>
    public HistogramQuantile? Quantile(int percent)
    {
        if (percent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
        if (Count == 0) return null;
        ulong rank = (ulong)(((UInt128)(uint)percent * Count + 99) / 100);
        ulong cumulative = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            cumulative = checked(cumulative + counts[i]);
            if (cumulative >= rank)
                return new(Count, i == counts.Length - 1 ? null : LogHistogramSchema.UpperBounds[i],
                    i == counts.Length - 1);
        }
        throw new InvalidOperationException("Histogram count invariant violated.");
    }

    /// <summary>Combines populations without modifying either input.</summary>
    public static LogHistogram Merge(LogHistogram left, LogHistogram right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.SchemaId != right.SchemaId)
            throw new ArgumentException("Incompatible histogram schemas.");
        var result = new LogHistogram(left.SchemaId)
        {
            Count = checked(left.Count + right.Count),
            SumNanoseconds = checked(left.SumNanoseconds + right.SumNanoseconds),
            MinimumNanoseconds = left.MinimumNanoseconds is not { } min ? right.MinimumNanoseconds
                : right.MinimumNanoseconds is not { } otherMin ? min : Math.Min(min, otherMin),
            MaximumNanoseconds = left.MaximumNanoseconds is not { } max ? right.MaximumNanoseconds
                : right.MaximumNanoseconds is not { } otherMax ? max : Math.Max(max, otherMax)
        };
        for (int i = 0; i < result.counts.Length; i++)
            result.counts[i] = checked(left.counts[i] + right.counts[i]);
        return result;
    }

    internal static LogHistogram Restore(ReadOnlySpan<ulong> buckets, ulong count, ulong sum, ulong min, ulong max)
    {
        var result = new LogHistogram
        {
            Count = count,
            SumNanoseconds = sum,
            MinimumNanoseconds = count == 0 ? null : min,
            MaximumNanoseconds = count == 0 ? null : max
        };
        buckets.CopyTo(result.counts);
        return result;
    }
}
