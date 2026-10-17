using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Observability;

namespace Dinamico.LoadEngine.Local;

/// <summary>Streams windows without retaining request or window history. An unfinished file is never published.</summary>
internal sealed class JsonReportOutput : ILocalMetricOutput, IAsyncDisposable
{
    private readonly Stream stream;
    private readonly Utf8JsonWriter writer;
    private readonly string? destination, temporary;
    private readonly bool overwrite;
    private readonly BoundedRunObservability observability;

    private JsonReportOutput(Stream stream, string? destination, string? temporary, bool overwrite,
        BoundedRunObservability observability)
    {
        this.stream = stream;
        this.destination = destination;
        this.temporary = temporary;
        this.overwrite = overwrite;
        this.observability = observability;
        writer = new(stream);
    }

    public static async Task<JsonReportOutput> CreateAsync(string path, bool overwrite, LoadTestPlan plan,
        BoundedRunObservability observability)
    {
        string? destination = path == "-" ? null : Path.GetFullPath(path);
        if (destination is not null && !overwrite && File.Exists(destination))
            throw new IOException("Report already exists.");
        string? temporary = destination is null ? null : destination + ".incomplete-" + Guid.NewGuid().ToString("N");
        Stream stream = temporary is null ? OpenStandardOutput() : new FileStream(temporary,
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, FileOptions.Asynchronous);
        var output = new JsonReportOutput(stream, destination, temporary, overwrite, observability);
        try
        {
            output.writer.WriteStartObject();
            output.Write("schemaVersion", 1);
            output.Write("executionId", plan.ExecutionId);
            output.Write("startedUtc", DateTimeOffset.UtcNow);
            output.Write("environment", new
            {
                engineVersion = typeof(LocalRunController).Assembly.GetName().Version?.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                operatingSystem = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount
            });
            // Deliberate allowlist: no URL, path, query, header names/values or payload.
            output.Write("plan", new
            {
                plan.SchemaVersion, plan.HistogramSchema, plan.Method,
                httpVersion = plan.HttpVersion.ToString(),
                targetScheme = plan.Target.Scheme, targetPort = plan.Target.Port,
                plan.RequestsPerSecond, plan.DurationSeconds, plan.PlannedTotal,
                plan.MaxConcurrency, requestTimeoutSeconds = plan.RequestTimeout.TotalSeconds,
                plan.MaxResponseBytes, plan.RateLimitMode, plan.RetryPolicy, plan.SuccessRule,
                limits = new
                {
                    plan.MaxConnectionsPerServer, plan.ReadBufferBytes,
                    plan.MaxRequestHeaderBytes, plan.MaxResponseHeaderBytes,
                    connectTimeoutSeconds = plan.ConnectTimeout.TotalSeconds,
                    pooledConnectionLifetimeSeconds = plan.PooledConnectionLifetime.TotalSeconds,
                    pooledConnectionIdleTimeoutSeconds = plan.PooledConnectionIdleTimeout.TotalSeconds,
                    redirects = false, proxy = false, cookies = false, automaticDecompression = false,
                    metricWindowSeconds = MetricWindow.Width.TotalSeconds,
                    metricSinkCapacity = MetricWindowSink.MaximumCapacity,
                    flushTimeoutSeconds = LocalRunController.FlushTimeout.TotalSeconds
                }
            });
            output.writer.WriteStartArray("windows");
            await output.writer.FlushAsync();
            return output;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    public async ValueTask WriteAsync(MetricWindow window, CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        Write("index", window.WindowIndex);
        Write("startSeconds", window.Start.TotalSeconds);
        Write("endSeconds", window.End.TotalSeconds);
        Write("isFinal", window.IsFinal);
        Write("quality", window.Quality);
        Write("counters", Counters(window.Counters));
        Write("inFlight", window.InFlight);
        Write("statuses", Statuses(window.StatusReceived));
        Write("rateLimitedPercent", window.RateLimitedPercent);
        Write("retryAfter", RetryAfter(window.RetryAfterKinds, window.RetryAfterClamped,
            window.RetryAfterClockUncertain, window.MinimumRetryAfter, window.MaximumRetryAfter));
        Write("terminalDuration", Histogram(window.TerminalDuration, "all_terminal_operations"));
        Write("httpCompletedDuration", Histogram(window.HttpCompletedDuration, "http_completed_operations"));
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        observability.ObserveWindow(window);
    }

    public async ValueTask FlushAsync(MetricWindowManifest? manifest, CancellationToken cancellationToken)
    {
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task CompleteAsync(LocalRunResult result, CancellationToken cancellationToken)
    {
        writer.WriteEndArray();
        Write("finishedUtc", DateTimeOffset.UtcNow);
        Write("result", new
        {
            result.Completion, result.Reason, result.Failures, result.DataComplete, result.Quality,
            result.PlannedTotal, result.ScheduledDue, result.NotScheduledAfterStop,
            durationsSeconds = new { generation = result.GenerationDuration.TotalSeconds,
                drain = result.DrainDuration.TotalSeconds, flush = result.FlushDuration.TotalSeconds,
                total = result.TotalDuration.TotalSeconds },
            coverage = new
            {
                result.OperationsDrained, result.MetricsCoverageComplete, result.FlushConfirmed, result.OutputStopped,
                result.Totals.WindowCount, endSeconds = result.Totals.End.TotalSeconds,
                manifest = result.Manifest is { } m ? new { m.LastWindowIndex, endSeconds = m.End.TotalSeconds,
                    m.Complete, m.Quality } : null
            },
            totals = new
            {
                counters = Counters(result.Totals.Counters), statuses = Statuses(result.Totals.StatusReceived),
                result.Totals.FinalStatuses, result.Totals.OtherFinalStatuses, result.Totals.RateLimitedPercent,
                result.Totals.LastInFlight,
                rates = new
                {
                    scope = "observed_confirmed_prefix",
                    notStartedDenominator = "scheduled_due",
                    notStartedPercent = Percent(checked(result.Totals[MetricCounter.SchedulerLate]
                        + result.Totals[MetricCounter.ConcurrencyLimit] + result.Totals[MetricCounter.Policy]),
                        result.Totals[MetricCounter.ScheduledDue]),
                    unsuccessfulTerminalDenominator = "all_terminal_operations",
                    unsuccessfulTerminalPercent = Percent(checked(result.Totals.TerminalDuration.Count
                        - result.Totals[MetricCounter.HttpSuccess]), result.Totals.TerminalDuration.Count),
                    startedPerGenerationSecond = result.GenerationDuration > TimeSpan.Zero
                        ? result.Totals[MetricCounter.Started] / result.GenerationDuration.TotalSeconds : (double?)null,
                    httpCompletedPerObservedSecond = result.Totals.End > TimeSpan.Zero
                        ? result.Totals[MetricCounter.HttpCompleted] / result.Totals.End.TotalSeconds : (double?)null
                },
                retryAfter = RetryAfter(result.Totals.RetryAfterKinds, result.Totals.RetryAfterClamped,
                    result.Totals.RetryAfterClockUncertain, result.Totals.MinimumRetryAfter, result.Totals.MaximumRetryAfter),
                registrationDelay = new { result.Totals.Registrations, result.Totals.RegistrationDelaySumNanoseconds,
                    result.Totals.RegistrationDelayMaxNanoseconds },
                terminalDuration = Histogram(result.Totals.TerminalDuration, "all_terminal_operations"),
                httpCompletedDuration = Histogram(result.Totals.HttpCompletedDuration, "http_completed_operations")
            },
            interpretation = new[] { "Durations are observed by the client; quantiles are approximate upper bounds.",
                "No 429 does not prove absence of rate limits; partial coverage is inconclusive.",
                "HTTP success, including 202, does not prove business completion." }
        });
        Write("observability", observability.Snapshot);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
        if (destination is not null)
        {
            await stream.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary!, destination, overwrite);
        }
    }

    private void Write<T>(string name, T value)
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, LocalCli.JsonOptions);
    }

    private static double? Percent(ulong numerator, ulong denominator) =>
        denominator == 0 ? null : 100.0 * numerator / denominator;

    private static Dictionary<string, ulong> Counters(IEnumerable<ulong> values) =>
        Enum.GetValues<MetricCounter>().Zip(values).ToDictionary(p => p.First.ToString(), p => p.Second);

    private static Dictionary<string, ulong> Statuses(IEnumerable<ulong> values) => values.Select((count, index) =>
        (count, index)).Where(p => p.count != 0).ToDictionary(p => p.index == 500 ? "other" : (p.index + 100).ToString(
            System.Globalization.CultureInfo.InvariantCulture), p => p.count);

    private static object RetryAfter(IEnumerable<ulong> kinds, ulong clamped, ulong clockUncertain,
        TimeSpan? min, TimeSpan? max) => new
    {
        kinds = Enum.GetValues<RetryAfterKind>().Zip(kinds).ToDictionary(p => p.First.ToString(), p => p.Second),
        clamped, clockUncertain, minimumDelaySeconds = min?.TotalSeconds, maximumDelaySeconds = max?.TotalSeconds
    };

    private static object Histogram(HistogramSnapshot histogram, string population) => new
    {
        schema = LogHistogramSchema.Name, unit = "nanoseconds", population,
        approximation = "nearest_rank_bucket_upper_bound",
        approximationLimits = new
        {
            firstBucketAbsoluteErrorNanoseconds = 1000,
            aboveFirstBucketRelativeErrorPercent = 2.19,
            roundingErrorNanoseconds = 1,
            overflowAboveNanoseconds = LogHistogramSchema.MaximumFiniteNanoseconds
        },
        histogram.Count, histogram.SumNanoseconds,
        histogram.MinimumNanoseconds, histogram.MaximumNanoseconds, histogram.OverflowCount,
        histogram.P50, histogram.P95, histogram.P99, buckets = histogram.Counts.ToArray()
    };

    // ConsoleStream intentionally swallows broken-pipe errors; a report must observe them.
    private static FileStream OpenStandardOutput()
    {
        nint handle = OperatingSystem.IsWindows() ? GetStdHandle(-11) : 1;
        return new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write,
            bufferSize: 16384, isAsync: false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    public async ValueTask DisposeAsync()
    {
        // Dispose can flush buffered JSON: the final explicit flush owns error reporting.
        try { writer.Dispose(); } catch (IOException) { }
        await stream.DisposeAsync();
    }
}
