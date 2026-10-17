using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dinamico.LoadEngine.Metrics;

/// <summary>Fixed-size v1 little-endian codec. SHA-256 detects corruption, not malicious senders.</summary>
public static class HistogramCodec
{
    public const int EncodedSize = 6760;
    private const int HeaderSize = 56;
    private const int ChecksumOffset = EncodedSize - 32;

    public static byte[] Encode(LogHistogram histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        byte[] bytes = new byte[EncodedSize];
        "DNHIST01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), histogram.SchemaId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), LogHistogramSchema.BucketCount);
        Write(bytes, 24, histogram.Count);
        Write(bytes, 32, histogram.SumNanoseconds);
        Write(bytes, 40, histogram.MinimumNanoseconds ?? 0);
        Write(bytes, 48, histogram.MaximumNanoseconds ?? 0);
        for (int i = 0; i < histogram.Counts.Length; i++)
            Write(bytes, HeaderSize + i * 8, histogram.Counts[i]);
        SHA256.HashData(bytes.AsSpan(0, ChecksumOffset), bytes.AsSpan(ChecksumOffset));
        return bytes;
    }

    public static LogHistogram Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedSize || !bytes[..8].SequenceEqual("DNHIST01"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != LogHistogramSchema.Id
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != LogHistogramSchema.BucketCount
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != 0)
            throw Invalid();
        Span<byte> checksum = stackalloc byte[32];
        SHA256.HashData(bytes[..ChecksumOffset], checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, bytes[ChecksumOffset..]))
            throw Invalid();

        ulong count = Read(bytes, 24), sum = Read(bytes, 32), min = Read(bytes, 40), max = Read(bytes, 48);
        Span<ulong> buckets = stackalloc ulong[LogHistogramSchema.BucketCount];
        ulong total = 0;
        try
        {
            for (int i = 0; i < buckets.Length; i++)
            {
                buckets[i] = Read(bytes, HeaderSize + i * 8);
                total = checked(total + buckets[i]);
            }
        }
        catch (OverflowException) { throw Invalid(); }
        if (total != count) throw Invalid();
        if (count == 0)
        {
            if (sum != 0 || min != 0 || max != 0) throw Invalid();
        }
        else
        {
            if (min > max) throw Invalid();
            int minBucket = LogHistogramSchema.Classify(min), maxBucket = LogHistogramSchema.Classify(max);
            if (buckets[minBucket] == 0 || buckets[maxBucket] == 0
                || (min != max && minBucket == maxBucket && buckets[minBucket] < 2)) throw Invalid();
            UInt128 least = 0, most = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                if (buckets[i] == 0) continue;
                if (i < minBucket || i > maxBucket) throw Invalid();
                ulong lower = i == 0 ? 0 : LogHistogramSchema.UpperBounds[i - 1] + 1;
                ulong upper = i == buckets.Length - 1 ? ulong.MaxValue : LogHistogramSchema.UpperBounds[i];
                lower = Math.Max(lower, min);
                upper = Math.Min(upper, max);
                least += (UInt128)lower * buckets[i];
                most += (UInt128)upper * buckets[i];
                // Both observed extrema must occur at least once, not merely fall inside a bucket.
                if (i == maxBucket) least += max - lower;
                if (i == minBucket) most -= upper - min;
            }
            if ((UInt128)sum < least || (UInt128)sum > most) throw Invalid();
        }
        return LogHistogram.Restore(buckets, count, sum, min, max);
    }

    private static ulong Read(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void Write(Span<byte> bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
    private static InvalidDataException Invalid() => new("Invalid histogram payload (protocol_integrity).");
}
