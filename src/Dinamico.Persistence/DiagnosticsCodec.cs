using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Observability;

namespace Dinamico.Persistence;

/// <summary>Only bounded, typed diagnostics cross the persistence boundary.</summary>
public static class DiagnosticsCodec
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { type => { foreach (var property in type.Properties) property.IsRequired = true; } }
        }
    };

    public static string Encode(RunDiagnostics value)
    {
        Validate(value, false);
        string json = JsonSerializer.Serialize(value, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid();
        return json;
    }

    public static RunDiagnostics Decode(string json, bool detailsPruned = false)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid();
        try
        {
            var value = JsonSerializer.Deserialize<RunDiagnostics>(json, Options) ?? throw Invalid();
            Validate(value, detailsPruned);
            return value;
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void Validate(RunDiagnostics value, bool detailsPruned)
    {
        if (value.SchemaVersion != 1 || value.CaptureMode is not ("Off" or "Sampled" or "Complete")
            || value.ExportState is not ("disabled" or "pending" or "exported" or "failed" or "not-configured")
            || value.Resources is null || value.Requests is null
            || value.Resources.Length > RunDiagnostics.MaximumResources || value.Requests.Length > RunDiagnostics.MaximumRequests
            || value.Observed < 0 || value.Retained < 0 || value.Retained > RunDiagnostics.MaximumRequests
            || value.Retained > value.Observed || value.Discarded != value.Observed - value.Retained
            || (!detailsPruned && value.Retained != value.Requests.Length) || (detailsPruned && value.Requests.Length != 0)
            || (value.CaptureMode == "Off" && (value.Requests.Length != 0 || value.Retained != 0 || value.CaptureComplete))
            || (value.CaptureComplete && (value.CaptureMode != "Complete" || value.Discarded != 0))) throw Invalid();
        ValidateTemplate(value.TraceUrlTemplate);
        ulong? previousWindow = null;
        double previousElapsed = 0;
        foreach (var resource in value.Resources)
        {
            if (resource is null || resource.WindowIndex >= RunDiagnostics.MaximumResources
                || (previousWindow is { } previous && resource.WindowIndex <= previous)
                || !Nonnegative(resource.ElapsedSeconds) || resource.ElapsedSeconds < previousElapsed
                || resource.WorkingSetBytes < 0 || resource.ManagedHeapBytes < 0 || !OptionalNonnegative(resource.CpuPercentOfOneCore)
                || resource.TotalAllocatedBytes < 0 || resource.GcPauseTicks < 0 || resource.Gen0 < 0 || resource.Gen1 < 0 || resource.Gen2 < 0
                || resource.ThreadPoolPending < 0 || resource.ThreadPoolThreads < 0
                || !OptionalNonnegative(resource.HttpQueueMaxMilliseconds)) throw Invalid();
            previousWindow = resource.WindowIndex;
            previousElapsed = resource.ElapsedSeconds;
        }
        var arrivals = new HashSet<long>();
        foreach (var request in value.Requests)
        {
            if (request is null || request.ArrivalIndex < 0 || !arrivals.Add(request.ArrivalIndex)
                || !Nonnegative(request.StartSeconds) || !Nonnegative(request.DurationMilliseconds)
                || !OptionalNonnegative(request.HeadersMilliseconds) || request.HeadersMilliseconds > request.DurationMilliseconds
                || request.StatusCode is < 200 or > 599
                || !Enum.TryParse<RequestOutcome>(request.Outcome, out var outcome) || outcome.ToString() != request.Outcome
                || !Enum.TryParse<RequestErrorKind>(request.ErrorKind, out var error) || error.ToString() != request.ErrorKind
                || !Enum.IsDefined(outcome) || !Enum.IsDefined(error)
                || !ValidOutcome(outcome, error, request.StatusCode)
                || !ValidId(request.TraceId, 32) || !ValidId(request.SpanId, 16)
                || ((request.TraceId is null) != (request.SpanId is null))
                || (!value.CorrelationEnabled && request.TraceId is not null)) throw Invalid();
        }
        if (value.Scheduler is { } scheduler && (scheduler.WakeCount < 0 || scheduler.LimitedWakeCount < 0
            || scheduler.LimitedWakeCount > scheduler.WakeCount || scheduler.ToleranceExceededDrops < 0
            || scheduler.WakeCapacityDrops < 0 || scheduler.EndBoundaryDrops < 0 || scheduler.MaxObservedLateness < TimeSpan.Zero
            || scheduler.Quantum < TimeSpan.Zero || scheduler.LatenessTolerance < TimeSpan.Zero)) throw Invalid();
    }

    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool OptionalNonnegative(double? value) => value is null || Nonnegative(value.Value);
    private static bool ValidId(string? value, int length) => value is null || (value.Length == length
        && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0'));
    private static bool ValidOutcome(RequestOutcome outcome, RequestErrorKind error, int? status) => outcome switch
    {
        RequestOutcome.HttpCompleted => status.HasValue && error == RequestErrorKind.None,
        RequestOutcome.TransportFailed => error is RequestErrorKind.Dns or RequestErrorKind.Connect or RequestErrorKind.Tls or RequestErrorKind.Protocol or RequestErrorKind.ResponseRead,
        RequestOutcome.RequestTimedOut => error == RequestErrorKind.RequestTimeout,
        RequestOutcome.CancelledInFlight => error == RequestErrorKind.Cancellation,
        RequestOutcome.ResponseLimitExceeded => error == RequestErrorKind.ResponseLimit,
        RequestOutcome.InternalFailed => error == RequestErrorKind.Internal,
        _ => false
    };

    private static void ValidateTemplate(string? template)
    {
        if (template is null) return;
        const string placeholder = "{traceId}";
        if (template.Length > 2048 || !template.Contains(placeholder, StringComparison.Ordinal)) throw Invalid();
        string replaced = template.Replace(placeholder, new string('a', 32), StringComparison.Ordinal);
        if (template.IndexOf(placeholder, StringComparison.Ordinal) != template.LastIndexOf(placeholder, StringComparison.Ordinal)
            || replaced.Any(c => char.IsControl(c) || c is '{' or '}')
            || !Uri.TryCreate(replaced, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.Contains(new string('a', 32), StringComparison.Ordinal)) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid or unsupported bounded diagnostics.");
}
