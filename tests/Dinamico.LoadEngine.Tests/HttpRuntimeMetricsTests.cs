using System.Diagnostics.Metrics;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Observability;
using Dinamico.LoadEngine.Tests.Fixtures;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

// The production instruments are process-wide. Keep unrelated HTTP tests out of exact assertions.
[Collection("HTTP runtime metrics")]
public sealed class HttpRuntimeMetricsTests
{
    [Fact]
    public void Unmeasured_or_unsupported_instruments_remain_unknown()
    {
        using var metrics = new HttpRuntimeMetrics();
        using var meter = new Meter("System.Net.Http");
        var open = meter.CreateUpDownCounter<long>("http.client.open_connections");
        var active = meter.CreateUpDownCounter<long>("http.client.active_requests");
        var queue = meter.CreateHistogram<double>("http.client.request.time_in_queue");
        meter.CreateCounter<long>("http.client.open_connections").Add(5);
        meter.CreateHistogram<double>("http.client.request.duration").Record(1);
        using var other = new Meter("unrelated");
        other.CreateUpDownCounter<long>("http.client.active_requests").Add(5);

        Assert.Equal(default, metrics.Snapshot);
        open.Add(0);
        active.Add(0);
        queue.Record(0);
        Assert.Equal(new HttpRuntimeMetricsSnapshot(0, 0, 0, 0, 1, 0, 0), metrics.Snapshot);
    }

    [Fact]
    public void Aggregation_is_thread_safe_bounded_and_does_not_inspect_canary_tags()
    {
        const string canary = "SYNTHETIC_URL_HEADER_CREDENTIAL_CANARY";
        using var metrics = new HttpRuntimeMetrics();
        using var meter = new Meter("System.Net.Http");
        var open = meter.CreateUpDownCounter<long>("http.client.open_connections");
        var active = meter.CreateUpDownCounter<long>("http.client.active_requests");
        var queue = meter.CreateHistogram<double>("http.client.request.time_in_queue");
        var tag = new KeyValuePair<string, object?>(canary, new UnprintableTag());
        Parallel.For(0, 1_000, _ =>
        {
            open.Add(1, tag);
            active.Add(1, tag);
            queue.Record(0.25, tag);
            active.Add(-1, tag);
            open.Add(-1, tag);
        });

        HttpRuntimeMetricsSnapshot snapshot = metrics.Snapshot;
        Assert.Equal(0, snapshot.OpenConnectionsCurrent);
        Assert.Equal(0, snapshot.ActiveRequestsCurrent);
        Assert.True(snapshot.OpenConnectionsPeak > 0);
        Assert.True(snapshot.ActiveRequestsPeak > 0);
        Assert.Equal(1_000, snapshot.QueueCount);
        Assert.Equal(250, snapshot.QueueTotalSeconds);
        Assert.Equal(0.25, snapshot.QueueMaxSeconds);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(canary, json + snapshot);
        using var document = JsonDocument.Parse(json);
        Assert.All(document.RootElement.EnumerateObject(), property =>
            Assert.Equal(JsonValueKind.Number, property.Value.ValueKind));
        Assert.True(json.Length < 512);
    }

    [Fact]
    public void Negative_relative_deltas_and_extreme_measurements_do_not_throw_or_produce_nonfinite_output()
    {
        using var metrics = new HttpRuntimeMetrics();
        using var meter = new Meter("System.Net.Http");
        var open = meter.CreateUpDownCounter<long>("http.client.open_connections");
        var queue = meter.CreateHistogram<double>("http.client.request.time_in_queue");
        open.Add(-1); // A connection may predate the listener.
        Assert.Equal(-1, metrics.Snapshot.OpenConnectionsCurrent);
        Assert.Equal(0, metrics.Snapshot.OpenConnectionsPeak);
        open.Add(long.MinValue);
        Assert.Equal(long.MinValue, metrics.Snapshot.OpenConnectionsCurrent);
        open.Add(long.MaxValue);
        open.Add(long.MaxValue);
        open.Add(long.MaxValue);
        Assert.Equal(long.MaxValue, metrics.Snapshot.OpenConnectionsCurrent);
        queue.Record(double.NaN);
        queue.Record(double.PositiveInfinity);
        queue.Record(-1);
        Assert.Null(metrics.Snapshot.QueueCount);
        queue.Record(double.MaxValue);
        queue.Record(double.MaxValue);
        Assert.Equal(2, metrics.Snapshot.QueueCount);
        Assert.Equal(double.MaxValue, metrics.Snapshot.QueueTotalSeconds);
        Assert.Equal(double.MaxValue, metrics.Snapshot.QueueMaxSeconds);
        _ = JsonSerializer.Serialize(metrics.Snapshot);
    }

    [Fact]
    public void Disposed_listener_stops_measurements_and_keeps_its_final_snapshot()
    {
        var metrics = new HttpRuntimeMetrics();
        using var meter = new Meter("System.Net.Http");
        var active = meter.CreateUpDownCounter<long>("http.client.active_requests");
        active.Add(1);
        HttpRuntimeMetricsSnapshot final = metrics.Snapshot;
        metrics.Dispose();
        metrics.Dispose();
        active.Add(-1);

        Assert.Equal(final, metrics.Snapshot);
        Assert.Equal(1, final.ActiveRequestsCurrent);
    }

    [Fact]
    public async Task Real_transport_is_active_before_headers_and_cancelled_request_returns_to_zero()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            HeaderDelay = TimeSpan.FromSeconds(30)
        });
        using var metrics = new HttpRuntimeMetrics();
        using var transport = CreateTransport(fixture.Address);
        using var stop = new CancellationTokenSource();
        Task<RequestObservation> pending = transport.SendAsync(0, stop.Token);
        try
        {
            await WaitUntilAsync(() => fixture.ReceivedCount == 1);
            Assert.Equal(1, metrics.Snapshot.ActiveRequestsCurrent);
            Assert.Equal(1, metrics.Snapshot.ActiveRequestsPeak);
            Assert.Equal(1, metrics.Snapshot.OpenConnectionsCurrent);
            Assert.False(pending.IsCompleted);
        }
        finally { stop.Cancel(); }

        Assert.Equal(RequestOutcome.CancelledInFlight, (await pending).Outcome);
        await WaitUntilAsync(() => metrics.Snapshot.ActiveRequestsCurrent == 0);
        await WaitUntilAsync(() => metrics.Snapshot.OpenConnectionsCurrent == 0);
    }

    [Fact]
    public async Task Disposing_transport_releases_the_idle_keep_alive_connection()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var metrics = new HttpRuntimeMetrics();
        using var transport = CreateTransport(fixture.Address);
        Assert.Equal(RequestOutcome.HttpCompleted, (await transport.SendAsync(0)).Outcome);
        Assert.Equal(1, metrics.Snapshot.OpenConnectionsCurrent);
        Assert.Equal(0, metrics.Snapshot.ActiveRequestsCurrent);

        transport.Dispose();

        await WaitUntilAsync(() => metrics.Snapshot.OpenConnectionsCurrent == 0);
        Assert.Equal(1, metrics.Snapshot.OpenConnectionsPeak);
        Assert.Equal(1, fixture.ConnectionCount);
    }

    [Fact]
    public async Task Real_transport_active_scope_ends_at_headers_while_body_and_connection_queue_remain()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            BodyBytes = 1024, TruncateBody = true
        });
        using var metrics = new HttpRuntimeMetrics();
        using var transport = CreateTransport(fixture.Address);
        using var bodyStop = new CancellationTokenSource();
        using var queueStop = new CancellationTokenSource();
        var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RequestObservation> body = transport.SendAsync(0, bodyStop.Token, _ => headers.TrySetResult());
        Task<RequestObservation>? queued = null;
        try
        {
            await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(body.IsCompleted);
            Assert.Equal(0, metrics.Snapshot.ActiveRequestsCurrent);
            Assert.Equal(1, metrics.Snapshot.ActiveRequestsPeak);
            Assert.Equal(1, metrics.Snapshot.OpenConnectionsCurrent);
            long initialQueueCount = metrics.Snapshot.QueueCount ?? 0;

            queued = transport.SendAsync(1, queueStop.Token);
            await WaitUntilAsync(() => metrics.Snapshot.ActiveRequestsCurrent == 1);
            Assert.Equal(1, fixture.ReceivedCount); // The first body still owns the only connection.
            queueStop.Cancel();
            Assert.Equal(RequestOutcome.CancelledInFlight, (await queued).Outcome);
            await WaitUntilAsync(() => metrics.Snapshot.ActiveRequestsCurrent == 0);
            Assert.True(metrics.Snapshot.QueueCount > initialQueueCount);
            Assert.True(metrics.Snapshot.QueueMaxSeconds > 0);
            Assert.True(metrics.Snapshot.QueueTotalSeconds >= metrics.Snapshot.QueueMaxSeconds);
        }
        finally
        {
            queueStop.Cancel();
            bodyStop.Cancel();
            fixture.ReleaseTruncation();
            if (queued is not null) await queued;
            await body;
        }
    }

    private static LocalHttpTransport CreateTransport(Uri address)
    {
        var validation = LocalPlanValidator.Validate(new LoadTestConfiguration
        {
            Url = address.AbsoluteUri, MaxConcurrency = 1, RequestTimeout = TimeSpan.FromSeconds(20)
        }, Guid.NewGuid());
        Assert.True(validation.IsValid);
        return new(validation.Plan!);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class UnprintableTag
    {
        public override string ToString() => throw new InvalidOperationException("Tags must never be inspected.");
    }
}

[CollectionDefinition("HTTP runtime metrics", DisableParallelization = true)]
public sealed class HttpRuntimeMetricsCollection { }
