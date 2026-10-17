using System.Text;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Observability;

namespace Dinamico.LoadEngine.Tests;

public sealed class BoundedObservabilityTests
{
    [Theory]
    [InlineData(RateLimitMode.Normal)]
    [InlineData(RateLimitMode.Authorized)]
    [InlineData(RateLimitMode.Diagnostic)]
    public async Task FloodIsBoundedAndRareTerminalCategorySurvives(RateLimitMode mode)
    {
        using var writer = new StringWriter();
        var time = new ClockTime();
        var observer = new BoundedRunObservability(writer, Guid.NewGuid(), time);
        observer.Start(mode);
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(clock, 0, sink);
        for (int index = 0; index < 125; index++)
        {
            // A large repeated 429/503 flood occupies only two category/status slots.
            for (int repeat = 0; repeat < 100; repeat++)
                foreach (int status in new[] { 429, 500, 501, 502, 503, 504 })
                {
                    collector.RecordStarted(clock.Ticks);
                    collector.RecordHeaders(new(0, status, default, clock.Ticks));
                    collector.RecordTerminal(new(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, status, 0), clock.Ticks);
                }
            collector.RecordStarted(clock.Ticks);
            collector.RecordTerminal(new(0, RequestOutcome.TransportFailed, TimeSpan.Zero, null, 0, RequestErrorKind.Tls), clock.Ticks);
            clock.Ticks += TimeSpan.FromSeconds(5).Ticks;
            collector.Advance();
            Assert.True(sink.TryRead(out var window));
            ulong statuses = window!.FinalStatuses;
            time.Timestamp = clock.Ticks;
            observer.ObserveWindow(window);
            Assert.Equal(statuses, window.FinalStatuses);
            Assert.Equal(600UL, statuses);
            // Allow the pump to progress; queue pressure is covered independently.
            await Task.Delay(5);
        }
        Assert.True(await observer.CompleteAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(100, observer.Snapshot.SamplesRecorded);
        Assert.True(observer.Snapshot.SamplesDiscarded > 0);
        var documents = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var samples = documents.Where(d => d.RootElement.GetProperty("event").GetString() == "error.sample").ToArray();
            Assert.Contains(samples, d => d.RootElement.GetProperty("data").GetProperty("detail").GetProperty("category").GetString() == "transport");
            foreach (var group in samples.GroupBy(d => d.RootElement.GetProperty("data").GetProperty("detail").GetProperty("window_index").GetUInt64()))
                Assert.True(group.Count() <= 5);
            foreach (var group in samples.GroupBy(d =>
            {
                var detail = d.RootElement.GetProperty("data").GetProperty("detail");
                return (detail.GetProperty("category").GetString(), detail.GetProperty("status").ToString());
            }))
            {
                ulong? previous = null;
                foreach (var sample in group)
                {
                    ulong index = sample.RootElement.GetProperty("data").GetProperty("detail").GetProperty("window_index").GetUInt64();
                    if (previous.HasValue) Assert.True(index - previous.Value >= 6);
                    previous = index;
                }
            }
            Assert.All(documents, d => Assert.True(Encoding.UTF8.GetByteCount(d.RootElement.GetRawText()) <= 4096));
            Assert.All(samples, d => Assert.True(Encoding.UTF8.GetByteCount(d.RootElement.GetRawText()) <= 512));
        }
        finally { foreach (var document in documents) document.Dispose(); }
    }

    [Fact]
    public async Task RateLimitReportsSuppressionAndRecoversAfterThirtySeconds()
    {
        using var writer = new StringWriter();
        var time = new ClockTime();
        var observer = new BoundedRunObservability(writer, Guid.NewGuid(), time);
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(clock, 0, sink);
        for (int index = 0; index < 9; index++)
        {
            if (index % 2 == 0) collector.RecordNotStarted(NotStartedReason.ConcurrencyLimit, 1, clock.Ticks);
            clock.Ticks += TimeSpan.FromSeconds(5).Ticks;
            time.Timestamp = clock.Ticks;
            collector.Advance();
            Assert.True(sink.TryRead(out var window));
            observer.ObserveWindow(window!);
            await Task.Delay(5);
        }
        Assert.True(await observer.CompleteAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observer.Snapshot.EventsSuppressed > 0);
        Assert.Contains("\"suppressed\":2", writer.ToString());
    }

    [Fact]
    public async Task BlockedOutputHasBoundedQueueAndDoesNotDelayCancellation()
    {
        var writer = new BlockedWriter();
        var observer = new BoundedRunObservability(writer, Guid.NewGuid());
        observer.Start(RateLimitMode.Normal);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(clock, 0, sink);
        for (int i = 0; i < 100; i++)
        {
            clock.Ticks += TimeSpan.FromSeconds(5).Ticks;
            collector.Advance();
            Assert.True(sink.TryRead(out var window));
            observer.ObserveWindow(window!);
        }
        Assert.Equal(BoundedRunObservability.QueueCapacity, observer.Snapshot.PendingLines);
        Assert.True(observer.Snapshot.LinesDropped > 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(await observer.CompleteAsync(TimeSpan.FromSeconds(30), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(observer.Snapshot.OutputFailed);
        writer.Release.TrySetResult();
    }

    [Fact]
    public async Task WriterExceptionTextCannotReachDiagnostics()
    {
        const string secret = "Bearer_canary_API_key_header_payload_URL_RetryAfter_exception";
        var observer = new BoundedRunObservability(new ThrowingWriter(secret), Guid.NewGuid());
        observer.Start(RateLimitMode.Diagnostic);
        Assert.False(await observer.CompleteAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observer.Snapshot.OutputFailed);
        Assert.DoesNotContain(secret, observer.Snapshot.ToString());
    }

    private sealed class Clock : IMonotonicClock
    {
        public long Ticks;
        public long GetTimestamp() => Ticks;
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
    }
    private sealed class ClockTime : TimeProvider
    {
        public long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
    private sealed class BlockedWriter : StringWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task; // Deliberately ignores cancellation.
        }
    }
    private sealed class ThrowingWriter(string secret) : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException(secret));
    }
}
