using System.Globalization;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.LoadEngine.Metrics;
using Dinamico.Persistence;

namespace Dinamico.LoadEngine.Local;

/// <summary>Imports only the safe projection and lossless windows; never persists arbitrary report JSON.</summary>
public static class LocalReportReader
{
    public const int MaximumBytes = 16 * 1024 * 1024;

    public static async Task<ExecutionImport> ReadAsync(Stream input, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw Invalid();
            buffer.Write(chunk, 0, count);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            var root = document.RootElement;
            RejectDuplicates(root);
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw Invalid();
            Guid executionId = root.GetProperty("executionId").GetGuid();
            var plan = root.GetProperty("plan");
            if (plan.GetProperty("schemaVersion").GetInt32() != 2
                || plan.GetProperty("histogramSchema").GetString() != LogHistogramSchema.Name) throw Invalid();
            var snapshot = new LocalRunSnapshot(plan.GetProperty("requestsPerSecond").GetInt32(),
                plan.GetProperty("durationSeconds").GetInt32(), plan.GetProperty("maxConcurrency").GetInt32(),
                EnumValue<RateLimitMode>(plan.GetProperty("rateLimitMode")),
                EnumValue<LoadRetryPolicy>(plan.GetProperty("retryPolicy")),
                EnumValue<HttpSuccessRule>(plan.GetProperty("successRule")),
                plan.GetProperty("method").GetString() ?? throw Invalid(),
                Seconds(plan.GetProperty("requestTimeoutSeconds")).Ticks,
                plan.GetProperty("maxResponseBytes").GetInt32(),
                plan.GetProperty("targetScheme").GetString() ?? throw Invalid(),
                plan.GetProperty("targetPort").GetInt32());
            var windows = new List<MetricWindow>();
            var totals = MetricWindowTotals.Empty;
            foreach (var item in root.GetProperty("windows").EnumerateArray())
            {
                if (windows.Count == 133) throw Invalid();
                // Older presentation-only reports omit registration metadata: no lossy reconstruction.
                byte[] bytes = item.GetProperty("encodedWindow").GetBytesFromBase64();
                var window = MetricWindowCodec.Decode(bytes);
                if (item.GetProperty("index").GetUInt64() != window.WindowIndex) throw Invalid();
                CompareCounters(item.GetProperty("counters"), window.Counters);
                CompareStatuses(item.GetProperty("statuses"), window.StatusReceived);
                CompareHistogram(item.GetProperty("terminalDuration"), window.TerminalDuration);
                CompareHistogram(item.GetProperty("httpCompletedDuration"), window.HttpCompletedDuration);
                CompareRetryAfter(item.GetProperty("retryAfter"), window.RetryAfterKinds,
                    window.RetryAfterClamped, window.RetryAfterClockUncertain, window.MinimumRetryAfter, window.MaximumRetryAfter);
                if (Seconds(item.GetProperty("startSeconds")) != window.Start
                    || Seconds(item.GetProperty("endSeconds")) != window.End
                    || item.GetProperty("isFinal").GetBoolean() != window.IsFinal
                    || item.GetProperty("inFlight").GetUInt64() != window.InFlight
                    || EnumValue<MeasurementQuality>(item.GetProperty("quality")) != window.Quality) throw Invalid();
                totals = totals.Append(window);
                windows.Add(window);
            }
            var result = root.GetProperty("result");
            var reported = result.GetProperty("totals");
            CompareCounters(reported.GetProperty("counters"), totals.Counters);
            CompareStatuses(reported.GetProperty("statuses"), totals.StatusReceived);
            CompareHistogram(reported.GetProperty("terminalDuration"), totals.TerminalDuration);
            CompareHistogram(reported.GetProperty("httpCompletedDuration"), totals.HttpCompletedDuration);
            CompareRetryAfter(reported.GetProperty("retryAfter"), totals.RetryAfterKinds, totals.RetryAfterClamped,
                totals.RetryAfterClockUncertain, totals.MinimumRetryAfter, totals.MaximumRetryAfter);
            if (reported.GetProperty("lastInFlight").GetUInt64() != totals.LastInFlight) throw Invalid();
            var registration = reported.GetProperty("registrationDelay");
            if (registration.GetProperty("registrations").GetUInt64() != totals.Registrations
                || registration.GetProperty("registrationDelaySumNanoseconds").GetUInt64() != totals.RegistrationDelaySumNanoseconds
                || registration.GetProperty("registrationDelayMaxNanoseconds").GetUInt64() != totals.RegistrationDelayMaxNanoseconds)
                throw Invalid();
            var coverage = result.GetProperty("coverage");
            var manifestJson = coverage.GetProperty("manifest");
            MetricWindowManifest? manifest = manifestJson.ValueKind == JsonValueKind.Null ? null : new(
                manifestJson.GetProperty("lastWindowIndex").GetUInt64(), Seconds(manifestJson.GetProperty("endSeconds")),
                manifestJson.GetProperty("complete").GetBoolean(), EnumValue<MeasurementQuality>(manifestJson.GetProperty("quality")));
            if (coverage.GetProperty("windowCount").GetUInt64() != totals.WindowCount
                || Seconds(coverage.GetProperty("endSeconds")) != totals.End) throw Invalid();
            bool complete = result.GetProperty("dataComplete").GetBoolean();
            bool coverageComplete = manifest is { } m && totals.HasCompleteCoverage(m);
            if (coverage.GetProperty("metricsCoverageComplete").GetBoolean() != coverageComplete
                || complete != (coverage.GetProperty("operationsDrained").GetBoolean()
                    && coverageComplete && coverage.GetProperty("flushConfirmed").GetBoolean()
                    && coverage.GetProperty("outputStopped").GetBoolean())) throw Invalid();
            ulong planned = result.GetProperty("plannedTotal").GetUInt64();
            if (plan.GetProperty("plannedTotal").GetUInt64() != planned) throw Invalid();
            return new(projectId, executionId, root.GetProperty("startedUtc").GetDateTimeOffset(),
                root.GetProperty("finishedUtc").GetDateTimeOffset(), snapshot, windows, complete, planned,
                result.GetProperty("scheduledDue").GetUInt64(), result.GetProperty("notScheduledAfterStop").GetUInt64(), manifest)
            {
                Completion = EnumValue<LocalRunCompletion>(result.GetProperty("completion")),
                Reason = EnumValue<LocalRunReason>(result.GetProperty("reason")),
                Failures = EnumValue<LocalRunFailure>(result.GetProperty("failures")),
                Quality = EnumValue<MeasurementQuality>(result.GetProperty("quality"))
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException
            or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid();
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static T EnumValue<T>(JsonElement value) where T : struct, Enum
    {
        string text = value.GetString() ?? throw Invalid();
        if (text.Length == 0 || text.Split(',', StringSplitOptions.TrimEntries).Any(part => !Enum.GetNames<T>().Contains(part))
            || !Enum.TryParse<T>(text, false, out var parsed)) throw Invalid();
        if (!typeof(T).IsDefined(typeof(FlagsAttribute), false) && !Enum.IsDefined(parsed)) throw Invalid();
        ulong allowed = Enum.GetValues<T>().Aggregate(0UL, (all, item) => all | Convert.ToUInt64(item));
        if ((Convert.ToUInt64(parsed) & ~allowed) != 0) throw Invalid();
        return parsed;
    }

    private static TimeSpan Seconds(JsonElement value) => TimeSpan.FromTicks(checked((long)(value.GetDecimal() * TimeSpan.TicksPerSecond)));

    private static void CompareCounters(JsonElement json, IEnumerable<ulong> counters)
    {
        if (json.EnumerateObject().Count() != MetricWindow.CounterCount) throw Invalid();
        foreach (var pair in Enum.GetValues<MetricCounter>().Zip(counters))
            if (json.GetProperty(pair.First.ToString()).GetUInt64() != pair.Second) throw Invalid();
    }

    private static void CompareStatuses(JsonElement json, IEnumerable<ulong> statuses)
    {
        var values = statuses.ToArray();
        int nonzero = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == 0) continue;
            string name = i == 500 ? "other" : (i + 100).ToString(CultureInfo.InvariantCulture);
            if (json.GetProperty(name).GetUInt64() != values[i]) throw Invalid();
            nonzero++;
        }
        if (json.EnumerateObject().Count() != nonzero) throw Invalid();
    }

    private static void CompareHistogram(JsonElement json, HistogramSnapshot histogram)
    {
        if (json.GetProperty("schema").GetString() != LogHistogramSchema.Name
            || json.GetProperty("unit").GetString() != "nanoseconds"
            || json.GetProperty("count").GetUInt64() != histogram.Count
            || json.GetProperty("sumNanoseconds").GetUInt64() != histogram.SumNanoseconds
            || OptionalUInt(json.GetProperty("minimumNanoseconds")) != histogram.MinimumNanoseconds
            || OptionalUInt(json.GetProperty("maximumNanoseconds")) != histogram.MaximumNanoseconds
            || json.GetProperty("overflowCount").GetUInt64() != histogram.OverflowCount
            || !json.GetProperty("buckets").EnumerateArray().Select(v => v.GetUInt64()).SequenceEqual(histogram.Counts.ToArray()))
            throw Invalid();
    }

    private static void CompareRetryAfter(JsonElement json, IEnumerable<ulong> kinds, ulong clamped,
        ulong uncertain, TimeSpan? minimum, TimeSpan? maximum)
    {
        var counts = json.GetProperty("kinds");
        if (counts.EnumerateObject().Count() != MetricWindow.RetryKindCount) throw Invalid();
        foreach (var pair in Enum.GetValues<RetryAfterKind>().Zip(kinds))
            if (counts.GetProperty(pair.First.ToString()).GetUInt64() != pair.Second) throw Invalid();
        if (json.GetProperty("clamped").GetUInt64() != clamped
            || json.GetProperty("clockUncertain").GetUInt64() != uncertain
            || OptionalSeconds(json.GetProperty("minimumDelaySeconds")) != minimum
            || OptionalSeconds(json.GetProperty("maximumDelaySeconds")) != maximum) throw Invalid();
    }

    private static TimeSpan? OptionalSeconds(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Seconds(value);
    private static ulong? OptionalUInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetUInt64();
    private static InvalidDataException Invalid() => new("Invalid or unsupported local report (report_integrity).");
}
