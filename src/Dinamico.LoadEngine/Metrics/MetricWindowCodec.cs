using System.Buffers.Binary;
using System.Security.Cryptography;
using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Metrics;

/// <summary>Canonical fixed-size local window v1, little-endian, SHA-256 checksum. No network protocol.</summary>
public static class MetricWindowCodec
{
    public const int EncodedSize = 17832;
    private const int ChecksumOffset = EncodedSize - 32;
    private const int TerminalOffset = 4280;

    public static byte[] Encode(MetricWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        byte[] bytes = new byte[EncodedSize];
        "DNWIN001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), MetricWindow.SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), LogHistogramSchema.Id);
        Write(bytes, 16, window.WindowIndex);
        Write(bytes, 24, window.Sequence);
        Write(bytes, 32, checked((ulong)window.Start.Ticks));
        Write(bytes, 40, checked((ulong)window.End.Ticks));
        Write(bytes, 48, window.InFlight);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), (uint)window.Quality);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), window.IsFinal ? 1U : 0);
        Write(bytes, 64, window.Registrations);
        Write(bytes, 72, window.RegistrationDelaySumNanoseconds);
        Write(bytes, 80, window.RegistrationDelayMaxNanoseconds);
        Write(bytes, 88, window.RetryAfterClamped);
        Write(bytes, 96, window.RetryAfterClockUncertain);
        Write(bytes, 104, window.MinimumRetryAfter is { } min ? (ulong)min.Ticks : ulong.MaxValue);
        Write(bytes, 112, window.MaximumRetryAfter is { } max ? (ulong)max.Ticks : ulong.MaxValue);
        int offset = 120;
        foreach (ulong count in window.Counters) { Write(bytes, offset, count); offset += 8; }
        foreach (ulong count in window.StatusReceived) { Write(bytes, offset, count); offset += 8; }
        foreach (ulong count in window.RetryAfterKinds) { Write(bytes, offset, count); offset += 8; }
        HistogramCodec.Encode(window.TerminalDuration.Histogram).CopyTo(bytes, TerminalOffset);
        HistogramCodec.Encode(window.HttpCompletedDuration.Histogram).CopyTo(bytes, TerminalOffset + HistogramCodec.EncodedSize);
        SHA256.HashData(bytes.AsSpan(0, ChecksumOffset), bytes.AsSpan(ChecksumOffset));
        return bytes;
    }

    public static string ContentHash(MetricWindow window) => Convert.ToHexString(Encode(window).AsSpan(ChecksumOffset));

    public static MetricWindow Decode(ReadOnlySpan<byte> bytes)
    {
        try { return DecodeCore(bytes); }
        catch (OverflowException) { throw Invalid(); }
    }

    private static MetricWindow DecodeCore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedSize || !bytes[..8].SequenceEqual("DNWIN001"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != MetricWindow.SchemaVersion
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != LogHistogramSchema.Id) throw Invalid();
        Span<byte> checksum = stackalloc byte[32];
        SHA256.HashData(bytes[..ChecksumOffset], checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, bytes[ChecksumOffset..])) throw Invalid();
        ulong index = Read(bytes, 16), sequence = Read(bytes, 24);
        long start = checked((long)Read(bytes, 32)), end = checked((long)Read(bytes, 40));
        var quality = (MeasurementQuality)BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]);
        uint final = BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]);
        if (sequence != index || start != checked((long)index * MetricWindow.Width.Ticks)
            || end < start || end - start > MetricWindow.Width.Ticks || final > 1
            || (final == 0 && end - start != MetricWindow.Width.Ticks)
            || (end == start && index != 0) || ((uint)quality & ~31U) != 0) throw Invalid();
        ulong registrations = Read(bytes, 64), delaySum = Read(bytes, 72), delayMax = Read(bytes, 80);
        if ((registrations == 0 && (delaySum != 0 || delayMax != 0))
            || delayMax > delaySum || (UInt128)delayMax * registrations < delaySum) throw Invalid();
        ulong clamped = Read(bytes, 88), uncertain = Read(bytes, 96);
        ulong min = Read(bytes, 104), max = Read(bytes, 112);
        int offset = 120;
        var counters = ReadArray(bytes, ref offset, MetricWindow.CounterCount);
        var statuses = ReadArray(bytes, ref offset, MetricWindow.StatusCount);
        var retryKinds = ReadArray(bytes, ref offset, MetricWindow.RetryKindCount);
        if (statuses.Take(100).Any(value => value != 0) || statuses[^1] != 0) throw Invalid();
        ulong finalStatuses = Sum(statuses), retryCount = Sum(retryKinds);
        ulong retryValid = checked(retryKinds[4] + retryKinds[5]);
        ulong status429 = statuses[329];
        if (finalStatuses != retryCount || clamped > retryValid || uncertain != retryKinds[5]
            || retryKinds[1] > checked(finalStatuses - status429)
            || checked(retryKinds[2] + retryKinds[3] + retryValid) > status429
            || (retryValid == 0 ? min != ulong.MaxValue || max != ulong.MaxValue
                : min > max || max > (ulong)TimeSpan.FromSeconds(RetryAfterObservation.MaxDelaySeconds).Ticks)) throw Invalid();
        if ((clamped != 0 && max != (ulong)TimeSpan.FromSeconds(RetryAfterObservation.MaxDelaySeconds).Ticks)
            || (retryValid == 1 && min != max)
            || (uncertain != 0 && !quality.HasFlag(MeasurementQuality.ClockUncertain))
            || (statuses.Skip(300).Any(value => value != 0) && !quality.HasFlag(MeasurementQuality.TargetErrors))
            || ((counters[2] != 0 || counters[3] != 0 || counters[4] != 0)
                && !quality.HasFlag(MeasurementQuality.GeneratorLimited))) throw Invalid();
        ulong terminalCount = checked(counters[5] + counters[7] + counters[8] + counters[9] + counters[10] + counters[11]);
        if (counters[0] != checked(counters[1] + counters[2] + counters[3] + counters[4])
            || counters[6] > counters[5]) throw Invalid();
        if (index == 0 && (counters[1] != checked(Read(bytes, 48) + terminalCount)
            || finalStatuses > counters[1])) throw Invalid();
        UInt128 eventRegistrations = (UInt128)counters[1] + finalStatuses + terminalCount;
        UInt128 minimumRegistrations = eventRegistrations, maximumRegistrations = eventRegistrations;
        for (int i = 2; i <= 4; i++)
        {
            if (counters[i] != 0) minimumRegistrations++;
            maximumRegistrations += counters[i];
        }
        if (registrations < minimumRegistrations || registrations > maximumRegistrations) throw Invalid();
        var terminal = HistogramCodec.Decode(bytes.Slice(TerminalOffset, HistogramCodec.EncodedSize));
        var completed = HistogramCodec.Decode(bytes.Slice(TerminalOffset + HistogramCodec.EncodedSize, HistogramCodec.EncodedSize));
        if (terminal.Count != terminalCount || completed.Count != counters[5]
            || completed.SumNanoseconds > terminal.SumNanoseconds) throw Invalid();
        if (completed.Count != 0 && (completed.MinimumNanoseconds < terminal.MinimumNanoseconds
            || completed.MaximumNanoseconds > terminal.MaximumNanoseconds)) throw Invalid();
        if (completed.Count == terminal.Count && (completed.SumNanoseconds != terminal.SumNanoseconds
            || completed.MinimumNanoseconds != terminal.MinimumNanoseconds
            || completed.MaximumNanoseconds != terminal.MaximumNanoseconds)) throw Invalid();
        for (int i = 0; i < terminal.Counts.Length; i++)
            if (completed.Counts[i] > terminal.Counts[i]) throw Invalid();
        return new(index, start, end, final == 1, Read(bytes, 48), quality, counters, statuses, retryKinds,
            clamped, uncertain, min == ulong.MaxValue ? null : min, max == ulong.MaxValue ? null : max,
            registrations, delaySum, delayMax, terminal, completed);
    }

    private static ulong[] ReadArray(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        var result = new ulong[count];
        for (int i = 0; i < count; i++, offset += 8) result[i] = Read(bytes, offset);
        return result;
    }
    internal static ulong Sum(IEnumerable<ulong> values) => values.Aggregate(0UL, (a, b) => checked(a + b));
    private static ulong Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void Write(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
    private static InvalidDataException Invalid() => new("Invalid metric window (protocol_integrity).");
}
