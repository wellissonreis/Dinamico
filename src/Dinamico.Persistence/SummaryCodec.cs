using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dinamico.LoadEngine.Metrics;

namespace Dinamico.Persistence;

/// <summary>Versioned summary envelope. JSON metadata has no histogram buckets; dense buckets are little-endian UInt64.</summary>
internal static class SummaryCodec
{
    private const int MaximumMetadataBytes = 32768;
    internal static byte[] Encode(RunSummary summary)
    {
        var metadata = summary with
        {
            TerminalDuration = summary.TerminalDuration with { Counts = [] },
            HttpCompletedDuration = summary.HttpCompletedDuration with { Counts = [] }
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata);
        if (json.Length > MaximumMetadataBytes) throw new InvalidDataException("Summary metadata limit exceeded.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write("DNSUM001"u8); writer.Write(LogHistogramSchema.Id); writer.Write(json.Length); writer.Write(json);
            foreach (ulong count in summary.TerminalDuration.Counts) writer.Write(count);
            foreach (ulong count in summary.HttpCompletedDuration.Counts) writer.Write(count);
        }
        byte[] content = stream.ToArray();
        return [.. content, .. SHA256.HashData(content)];
    }
    internal static RunSummary Decode(byte[] bytes)
    {
        const int header = 16, checksum = 32, buckets = 2 * LogHistogramSchema.BucketCount * 8;
        if (bytes.Length < header + checksum + buckets || bytes.Length > header + checksum + buckets + MaximumMetadataBytes
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - checksum)), bytes.AsSpan(bytes.Length - checksum)))
            throw new InvalidDataException("Invalid summary checksum or length.");
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("DNSUM001"u8) || reader.ReadUInt32() != LogHistogramSchema.Id)
            throw new InvalidDataException("Invalid summary schema.");
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumMetadataBytes || bytes.Length != header + length + buckets + checksum)
            throw new InvalidDataException("Invalid summary metadata length.");
        var result = JsonSerializer.Deserialize<RunSummary>(reader.ReadBytes(length)) ?? throw new InvalidDataException("Missing summary metadata.");
        ulong[] terminal = ReadBuckets(reader), completed = ReadBuckets(reader);
        return result with { TerminalDuration = result.TerminalDuration with { Counts = terminal },
            HttpCompletedDuration = result.HttpCompletedDuration with { Counts = completed } };
    }
    private static ulong[] ReadBuckets(BinaryReader reader)
    {
        var values = new ulong[LogHistogramSchema.BucketCount];
        for (int i = 0; i < values.Length; i++) values[i] = reader.ReadUInt64();
        return values;
    }
}
