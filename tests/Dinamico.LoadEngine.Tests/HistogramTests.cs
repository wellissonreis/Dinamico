using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Dinamico.LoadEngine.Metrics;

namespace Dinamico.LoadEngine.Tests;

public sealed class HistogramTests
{
    [Fact]
    public void CanonicalTableUsesExactCeilingAndStrictlyIncreasingBounds()
    {
        var bounds = LogHistogramSchema.UpperBounds;
        Assert.Equal(833, bounds.Length);
        Assert.Equal(1000UL, bounds[0]);
        Assert.Equal(67_108_864_000UL, bounds[^1]);
        for (int i = 0; i < bounds.Length; i++)
        {
            BigInteger exactPower = BigInteger.Pow(1000, 32) << i;
            Assert.True(BigInteger.Pow(bounds[i], 32) >= exactPower);
            Assert.True(BigInteger.Pow(bounds[i] - 1, 32) < exactPower);
            if (i > 0) Assert.True(bounds[i] > bounds[i - 1]);
        }
    }

    [Fact]
    public void EveryBoundaryAndItsNeighborsIncrementExactlyOneExpectedBucket()
    {
        var bounds = LogHistogramSchema.UpperBounds.ToArray();
        for (int i = 0; i < bounds.Length; i++)
        {
            VerifySingle(bounds[i] - 1, i);
            VerifySingle(bounds[i], i);
            VerifySingle(bounds[i] + 1, i + 1);
        }
        VerifySingle(0, 0);
        VerifySingle(999, 0);
        VerifySingle(60_000_000_000, 827);
        VerifySingle(ulong.MaxValue, 833);
    }

    [Fact]
    public void EmptyPopulationIsNullAndRoundTrips()
    {
        var empty = HistogramCodec.Decode(HistogramCodec.Encode(new()));
        Assert.Equal(0UL, empty.Count);
        Assert.Equal(0UL, empty.SumNanoseconds);
        Assert.Null(empty.MinimumNanoseconds);
        Assert.Null(empty.MaximumNanoseconds);
        Assert.Null(empty.P50);
        Assert.Null(empty.P95);
        Assert.Null(empty.P99);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Quantile(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Quantile(101));
    }

    [Fact]
    public void KnownPopulationsRespectNearestRankAndApproximationError()
    {
        CheckDistribution(Enumerable.Repeat(12345UL, 1000).ToArray());
        CheckDistribution(Enumerable.Repeat(100UL, 500).Concat(Enumerable.Repeat(1_000_000UL, 500)).ToArray());
        CheckDistribution(Enumerable.Repeat(10_000_000UL, 9900).Concat(Enumerable.Repeat(1_000_000_000UL, 100)).ToArray());
        CheckDistribution(Enumerable.Range(0, 10000).Select(i => (ulong)i * 6_710_887).ToArray());
        var rareTail = new LogHistogram();
        for (int i = 0; i < 99; i++) rareTail.Record(1000);
        rareTail.Record(LogHistogramSchema.MaximumFiniteNanoseconds + 1);
        Assert.False(rareTail.P99!.Value.IsOverflow);
        var overflow = rareTail.Quantile(100)!.Value;
        Assert.True(overflow.IsOverflow);
        Assert.Null(overflow.UpperBoundNanoseconds);
        Assert.Equal(100UL, overflow.Count);
        Assert.Equal(1UL, rareTail.OverflowCount);
    }

    [Fact]
    public void UnequalWorkersMergeDistributionsInsteadOfAveragingPercentiles()
    {
        var fast = Population(9900, 10_000_000);
        var slow = Population(100, 1_000_000_000);
        var tail = Population(3, 2_000_000_000);
        byte[] before = HistogramCodec.Encode(fast);
        var merged = LogHistogram.Merge(fast, slow);
        Assert.Equal(fast.P95!.Value.UpperBoundNanoseconds, merged.P95!.Value.UpperBoundNanoseconds);
        Assert.Equal(10000UL, merged.Count);
        Assert.Equal(199_000_000_000UL, merged.SumNanoseconds);
        Assert.Equal(10_000_000UL, merged.MinimumNanoseconds);
        Assert.Equal(1_000_000_000UL, merged.MaximumNanoseconds);
        Assert.Equal(HistogramCodec.Encode(merged), HistogramCodec.Encode(LogHistogram.Merge(slow, fast)));
        Assert.Equal(HistogramCodec.Encode(LogHistogram.Merge(merged, tail)),
            HistogramCodec.Encode(LogHistogram.Merge(fast, LogHistogram.Merge(slow, tail))));
        Assert.Equal(before, HistogramCodec.Encode(fast));
        Assert.Equal(before, HistogramCodec.Encode(LogHistogram.Merge(fast, new())));
        Assert.Equal(before, HistogramCodec.Encode(LogHistogram.Merge(new(), fast)));
        Assert.Throws<ArgumentException>(() => new LogHistogram(2));
    }

    [Fact]
    public void RecordAndMergeDetectOverflowWithoutChangingInputs()
    {
        var sumFull = Population(1, ulong.MaxValue);
        byte[] sumBefore = HistogramCodec.Encode(sumFull);
        Assert.Throws<OverflowException>(() => sumFull.Record(1));
        Assert.Throws<OverflowException>(() => LogHistogram.Merge(sumFull, Population(1, 1)));
        Assert.Equal(sumBefore, HistogramCodec.Encode(sumFull));
        var countFull = HistogramCodec.Decode(Payload(ulong.MaxValue, 0, 0, 0, (0, ulong.MaxValue)));
        byte[] countBefore = HistogramCodec.Encode(countFull);
        Assert.Equal(1000UL, countFull.P99!.Value.UpperBoundNanoseconds);
        Assert.Throws<OverflowException>(() => countFull.Record(0));
        Assert.Throws<OverflowException>(() => LogHistogram.Merge(countFull, Population(1, 0)));
        Assert.Equal(countBefore, HistogramCodec.Encode(countFull));
    }

    [Fact]
    public void CodecHasFixedSizeExplicitLittleEndianAndStableRoundTrip()
    {
        var histogram = new LogHistogram();
        foreach (ulong value in new ulong[] { 0, 1, 1000, 1001, 60_000_000_000, 67_108_864_001 }) histogram.Record(value);
        byte[] bytes = HistogramCodec.Encode(histogram);
        Assert.Equal(6760, bytes.Length);
        Assert.Equal("DNHIST01"u8.ToArray(), bytes[..8]);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes[8..12]);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes[12..16]);
        Assert.Equal(new byte[] { 66, 3, 0, 0 }, bytes[16..20]);
        Assert.Equal(6, bytes[24]);
        Assert.Equal(3, bytes[56]);
        Assert.Equal(bytes, HistogramCodec.Encode(HistogramCodec.Decode(bytes)));
        Assert.Equal(SHA256.HashData(bytes[..^32]), bytes[^32..]);
        var maximum = Population(1, ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, HistogramCodec.Decode(HistogramCodec.Encode(maximum)).SumNanoseconds);
    }

    [Fact]
    public void CodecRejectsEveryTruncationTrailingDataAndCorruption()
    {
        byte[] valid = HistogramCodec.Encode(Population(1, 1001));
        for (int length = 0; length < valid.Length; length++)
            Assert.Throws<InvalidDataException>(() => HistogramCodec.Decode(valid.AsSpan(0, length)));
        Assert.Throws<InvalidDataException>(() => HistogramCodec.Decode(valid.Concat(new byte[1]).ToArray()));
        for (int i = 0; i < valid.Length; i++)
        {
            valid[i] ^= 1;
            Assert.Throws<InvalidDataException>(() => HistogramCodec.Decode(valid));
            valid[i] ^= 1;
        }
    }

    [Theory]
    [InlineData(0, 0)] // magic
    [InlineData(8, 2)] // codec version
    [InlineData(12, 2)] // schema
    [InlineData(16, 1)] // bucket count
    [InlineData(20, 1)] // reserved
    public void CodecRejectsUnsupportedHeadersEvenWithValidChecksum(int offset, byte value)
    {
        byte[] bytes = HistogramCodec.Encode(new());
        bytes[offset] = value;
        Seal(bytes);
        Assert.Throws<InvalidDataException>(() => HistogramCodec.Decode(bytes));
    }

    [Fact]
    public void CodecRejectsInconsistentCountsSumAndExtremaEvenWithValidChecksum()
    {
        var invalid = new[]
        {
            Payload(0, 0, 0, 0, (0, 1UL)), // count mismatch
            Payload(0, 1, 0, 0), // noncanonical empty sum
            Payload(0, 0, 1, 1), // noncanonical empty extrema
            Payload(1, 2, 2, 1, (0, 1UL)), // reversed extrema
            Payload(1, 1001, 1001, 1001, (0, 1UL)), // extrema in unoccupied bucket
            Payload(2, 2001, 0, 1000, (0, 2UL)), // sum outside bounds
            Payload(2, 500, 0, 1000, (0, 2UL)), // missing observed max
            Payload(2, 1500, 0, 1000, (0, 2UL)), // missing observed min
            Payload(1, 500, 0, 1000, (0, 1UL)), // one value cannot have distinct extrema
            Payload(2, 2000, 1000, 1000, (0, 1UL), (1, 1UL)), // bucket outside extrema
            Payload(ulong.MaxValue, 0, 0, 1001, (0, ulong.MaxValue), (1, 1UL)), // bucket count sum overflow
            Payload(ulong.MaxValue, ulong.MaxValue, 1000, 1000, (0, ulong.MaxValue)) // duration sum impossible/overflow
        };
        foreach (byte[] bytes in invalid)
            Assert.Throws<InvalidDataException>(() => HistogramCodec.Decode(bytes));
    }

    [Fact]
    public void CodecMatchesIndependentEmptyGoldenVector()
    {
        byte[] bytes = HistogramCodec.Encode(new());
        Assert.Equal("6773F1999A08F40AFEF2849E65CEA86B06749ACBE5960DCAB4DC633636EDF575", Convert.ToHexString(bytes[^32..]));
        Assert.All(bytes[20..^32], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void RecordAndQuantileAllocateNoMemoryAsPopulationGrows()
    {
        var histogram = new LogHistogram();
        histogram.Record(1);
        _ = histogram.P99;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            histogram.Record((ulong)i);
            _ = histogram.P99;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(834, histogram.Counts.Length);
        Assert.Equal(10001UL, histogram.Count);
    }

    [Fact]
    public void RankNearUInt64MaximumAndDistinctExtremaInSameBucketAreValid()
    {
        var large = HistogramCodec.Decode(Payload(ulong.MaxValue, 1001, 0, 1001,
            (0, ulong.MaxValue - 1), (1, 1UL)));
        Assert.Equal(1000UL, large.P99!.Value.UpperBoundNanoseconds);
        Assert.Equal(LogHistogramSchema.UpperBounds[1], large.Quantile(100)!.Value.UpperBoundNanoseconds);
        var sameBucket = HistogramCodec.Decode(Payload(2, 1000, 0, 1000, (0, 2UL)));
        Assert.Equal(0UL, sameBucket.MinimumNanoseconds);
        Assert.Equal(1000UL, sameBucket.MaximumNanoseconds);
    }

    private static void VerifySingle(ulong value, int expectedBucket)
    {
        var histogram = Population(1, value);
        var counts = histogram.Counts.ToArray();
        Assert.Equal(1UL, counts[expectedBucket]);
        Assert.Equal(1UL, counts.Aggregate(0UL, (a, b) => checked(a + b)));
        Assert.Equal(value, histogram.SumNanoseconds);
        Assert.Equal(value, histogram.MinimumNanoseconds);
        Assert.Equal(value, histogram.MaximumNanoseconds);
        Assert.Equal(HistogramCodec.Encode(histogram), HistogramCodec.Encode(HistogramCodec.Decode(HistogramCodec.Encode(histogram))));
    }

    private static LogHistogram Population(int count, ulong value)
    {
        var histogram = new LogHistogram();
        for (int i = 0; i < count; i++) histogram.Record(value);
        return histogram;
    }

    private static void CheckDistribution(ulong[] values)
    {
        var histogram = new LogHistogram();
        foreach (ulong value in values.Reverse()) histogram.Record(value);
        Array.Sort(values);
        Assert.Equal((ulong)values.Length, histogram.Count);
        Assert.Equal(values.Aggregate(0UL, (a, b) => checked(a + b)), histogram.SumNanoseconds);
        foreach (int percent in new[] { 1, 50, 95, 99, 100 })
        {
            ulong exact = values[(percent * values.Length + 99) / 100 - 1];
            var quantile = histogram.Quantile(percent)!.Value;
            Assert.Equal((ulong)values.Length, quantile.Count);
            Assert.False(quantile.IsOverflow);
            ulong upper = quantile.UpperBoundNanoseconds!.Value;
            Assert.True(upper >= exact);
            Assert.True(exact <= 1000 ? upper - exact <= 1000 : upper - exact < exact * (Math.Pow(2, 1.0 / 32) - 1) + 1);
        }
    }

    private static byte[] Payload(ulong count, ulong sum, ulong min, ulong max, params (int Index, ulong Count)[] buckets)
    {
        byte[] bytes = HistogramCodec.Encode(new());
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), sum);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), min);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), max);
        foreach (var bucket in buckets)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56 + bucket.Index * 8), bucket.Count);
        Seal(bytes);
        return bytes;
    }

    private static void Seal(byte[] bytes) => SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
}
