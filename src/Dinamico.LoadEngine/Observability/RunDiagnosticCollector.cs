using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;

namespace Dinamico.LoadEngine.Observability;

/// <summary>At most 1000 sampled or 2000 complete operations. No external I/O on completion.</summary>
public sealed class RunDiagnosticCollector(RequestCaptureMode mode, bool correlateRequests)
{
    private readonly object gate = new();
    private readonly List<RequestSample> first = [], failures = [];
    private readonly SortedSet<RequestSample> slow = new(Comparer<RequestSample>.Create((a, b) =>
    {
        int duration = a.DurationMilliseconds.CompareTo(b.DurationMilliseconds);
        return duration != 0 ? duration : a.ArrivalIndex.CompareTo(b.ArrivalIndex);
    }));
    private readonly List<ResourceSample> resources = [];
    private long observed;

    public void Record(RequestObservation observation, double startSeconds, double? headersMilliseconds)
    {
        lock (gate)
        {
            observed++;
            if (mode == RequestCaptureMode.Off) return;
            var sample = new RequestSample(observation.ArrivalIndex, startSeconds, observation.TerminalDuration.TotalMilliseconds,
                headersMilliseconds, observation.StatusCode, observation.Outcome.ToString(), observation.ErrorKind.ToString(),
                observation.TraceId, observation.SpanId);
            if (mode == RequestCaptureMode.Complete)
            {
                if (first.Count < RunDiagnostics.MaximumRequests) first.Add(sample);
                return;
            }
            if (first.Count < 250) first.Add(sample);
            if ((observation.Outcome != RequestOutcome.HttpCompleted || observation.StatusCode >= 400) && failures.Count < 250)
                failures.Add(sample);
            slow.Add(sample);
            if (slow.Count > 500) slow.Remove(slow.Min!);
        }
    }

    public void Record(ResourceSample resource)
    {
        lock (gate)
            if (resources.Count < RunDiagnostics.MaximumResources) resources.Add(resource);
    }

    public RunDiagnostics Snapshot(bool drained = false, SchedulerDiagnostics? scheduler = null)
    {
        lock (gate)
        {
            var requests = first.Concat(failures).Concat(slow).DistinctBy(r => r.ArrivalIndex).OrderBy(r => r.ArrivalIndex).ToArray();
            return new(1, mode.ToString(), correlateRequests, observed, requests.Length, observed - requests.Length,
                drained && mode == RequestCaptureMode.Complete && observed == requests.Length,
                resources.ToArray(), requests, scheduler, correlateRequests ? "pending" : "disabled");
        }
    }
}
