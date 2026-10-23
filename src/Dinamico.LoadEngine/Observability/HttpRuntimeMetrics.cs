using System.Diagnostics.Metrics;

namespace Dinamico.LoadEngine.Observability;

internal readonly record struct HttpRuntimeMetricsSnapshot(
    long? OpenConnectionsCurrent, long? OpenConnectionsPeak,
    long? ActiveRequestsCurrent, long? ActiveRequestsPeak,
    long? QueueCount, double? QueueTotalSeconds, double? QueueMaxSeconds);

/// <summary>
/// Bounded, tag-free System.Net.Http deltas for .NET 10. Start before creating the transport and
/// dispose after its final diagnostics. All listeners see process-wide traffic, including other
/// concurrent executions. Connections/requests already present at subscription can contribute
/// negative deltas: these are relative observations, not an absolute census or kernel socket count.
/// Active requests end at response headers, while the transport may still be consuming the body.
/// </summary>
internal sealed class HttpRuntimeMetrics : IDisposable
{
    private enum Measurement { OpenConnections, ActiveRequests, Queue }

    private readonly object gate = new();
    private readonly MeterListener listener = new();
    private long openConnections, openPeak, activeRequests, activePeak, queueCount;
    private double queueTotal, queueMax;
    private bool openObserved, activeObserved, queueObserved, disposed;

    public HttpRuntimeMetrics()
    {
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name != "System.Net.Http") return;
            Measurement? measurement = instrument switch
            {
                UpDownCounter<long> when instrument.Name == "http.client.open_connections" => Measurement.OpenConnections,
                UpDownCounter<long> when instrument.Name == "http.client.active_requests" => Measurement.ActiveRequests,
                Histogram<double> when instrument.Name == "http.client.request.time_in_queue" => Measurement.Queue,
                _ => null
            };
            if (measurement is null) return;
            lock (gate)
            {
                if (!disposed) owner.EnableMeasurementEvents(instrument, measurement.Value);
            }
        };
        // Never inspect, copy or retain measurement tags: they can contain destinations and text.
        listener.SetMeasurementEventCallback<long>((_, value, _, state) =>
        {
            lock (gate)
            {
                if (disposed) return;
                if (state is Measurement.OpenConnections)
                {
                    openObserved = true;
                    openConnections = AddSaturated(openConnections, value);
                    openPeak = Math.Max(openPeak, openConnections);
                }
                else if (state is Measurement.ActiveRequests)
                {
                    activeObserved = true;
                    activeRequests = AddSaturated(activeRequests, value);
                    activePeak = Math.Max(activePeak, activeRequests);
                }
            }
        });
        listener.SetMeasurementEventCallback<double>((_, value, _, state) =>
        {
            if (state is not Measurement.Queue || !double.IsFinite(value) || value < 0) return;
            lock (gate)
            {
                if (disposed) return;
                queueObserved = true;
                queueCount = AddSaturated(queueCount, 1);
                queueTotal = Math.Min(double.MaxValue, queueTotal + value);
                queueMax = Math.Max(queueMax, value);
            }
        });
        listener.Start();
    }

    public HttpRuntimeMetricsSnapshot Snapshot
    {
        get
        {
            lock (gate)
                return new(openObserved ? openConnections : null, openObserved ? openPeak : null,
                    activeObserved ? activeRequests : null, activeObserved ? activePeak : null,
                    queueObserved ? queueCount : null, queueObserved ? queueTotal : null,
                    queueObserved ? queueMax : null);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        listener.Dispose();
    }

    private static long AddSaturated(long current, long delta) =>
        delta > 0 && current > long.MaxValue - delta ? long.MaxValue :
        delta < 0 && current < long.MinValue - delta ? long.MinValue : current + delta;
}
