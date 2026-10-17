using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class MetricWindowTests
{
    [Fact]
    public void HeadersAndTerminalCrossWindowsWithoutDuplicatingStatusOrHttpPopulation()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.RecordStarted(0);
        clock.SetSeconds(4);
        metrics.RecordHeaders(new(0, 429, RetryAfterObservation.Missing, clock.GetTimestamp()));
        clock.SetSeconds(6);
        metrics.RecordTerminal(Terminal(RequestOutcome.RequestTimedOut, 429), clock.GetTimestamp());
        var manifest = metrics.Close();
        var windows = ReadAll(sink);
        Assert.Equal(2, windows.Count);
        Assert.Equal(1UL, windows[0].StatusReceived[329]);
        Assert.Equal(1UL, windows[0].InFlight);
        Assert.Equal(0UL, windows[0].TerminalDuration.Count);
        Assert.Equal(0UL, windows[1].FinalStatuses);
        Assert.Equal(1UL, windows[1][MetricCounter.RequestTimedOut]);
        Assert.Equal(0UL, windows[1].HttpCompletedDuration.Count);
        Assert.Equal(0UL, windows[1].InFlight);
        Assert.Equal(TimeSpan.FromSeconds(1), windows[1].End - windows[1].Start);
        var totals = Fold(windows);
        Assert.True(totals.HasCompleteCoverage(manifest));
        Assert.Equal(1UL, totals.TerminalDuration.Count);
        Assert.Equal(100.0, totals.RateLimitedPercent);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(10, 2)]
    [InlineData(12, 3)]
    public void EmptyWindowsFinalPartialAndBoundaryCloseHaveProvenCoverage(int end, int expected)
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        clock.SetSeconds(end);
        var manifest = metrics.Close();
        Assert.Equal(manifest, metrics.Close());
        var windows = ReadAll(sink);
        Assert.Equal(expected, windows.Count);
        Assert.True(Fold(windows).HasCompleteCoverage(manifest));
        Assert.Equal((ulong)(expected - 1), manifest.LastWindowIndex);
        Assert.Equal(TimeSpan.FromSeconds(end), manifest.End);
        Assert.All(windows, window => Assert.Null(window.RateLimitedPercent));
        Assert.True(windows[^1].IsFinal);
        Assert.Throws<InvalidOperationException>(() => metrics.RecordStarted(clock.GetTimestamp()));
    }

    [Fact]
    public void AdvanceAtBoundaryThenCloseDoesNotEmitZeroWidthDuplicate()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        clock.SetSeconds(5);
        metrics.Advance();
        var manifest = metrics.Close();
        var windows = ReadAll(sink);
        Assert.Single(windows);
        Assert.False(windows[0].IsFinal); // Final manifest proves closure of the already immutable interval.
        Assert.True(Fold(windows).HasCompleteCoverage(manifest));
    }

    [Fact]
    public void AllDropReasonsAreExclusiveAndGaugeIsNotSummedDuringDrain()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.RecordNotStarted(NotStartedReason.SchedulerLate, 10, 0);
        metrics.RecordNotStarted(NotStartedReason.ConcurrencyLimit, 20, 0);
        metrics.RecordNotStarted(NotStartedReason.Policy, 30, 0);
        metrics.RecordStarted(0);
        clock.SetSeconds(10);
        metrics.Advance();
        clock.SetSeconds(11);
        metrics.RecordTerminal(Terminal(RequestOutcome.HttpCompleted, 202), clock.GetTimestamp());
        var manifest = metrics.Close();
        var windows = ReadAll(sink);
        Assert.Equal(1UL, windows[0].InFlight);
        Assert.Equal(1UL, windows[1].InFlight);
        var totals = Fold(windows);
        Assert.Equal(0UL, totals.LastInFlight);
        Assert.Equal(61UL, totals[MetricCounter.ScheduledDue]);
        Assert.Equal(1UL, totals[MetricCounter.HttpCompleted]);
        Assert.Equal(1UL, totals[MetricCounter.HttpSuccess]);
        Assert.True(totals.Quality.HasFlag(MeasurementQuality.GeneratorLimited));
        Assert.True(totals.HasCompleteCoverage(manifest));
    }

    [Fact]
    public void RetryAfterCategoriesClampClockAndExtremaHaveFixedCardinality()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        var utc = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        RetryAfterObservation[] evidence =
        [
            default, RetryAfterObservation.Missing,
            RetryAfterObservation.Parse(429, ["bad"], utc),
            RetryAfterObservation.Parse(429, ["0"], utc),
            RetryAfterObservation.Parse(429, ["9999999"], utc),
            RetryAfterObservation.Parse(429, ["Sat, 17 Oct 2026 00:00:17 GMT"], utc),
            RetryAfterObservation.Parse(503, ["25"], utc)
        ];
        for (int i = 0; i < evidence.Length; i++)
        {
            metrics.RecordStarted(0);
            metrics.RecordHeaders(new(i, i == 6 ? 503 : 429, evidence[i], 0));
            metrics.RecordTerminal(Terminal(RequestOutcome.HttpCompleted, i == 6 ? 503 : 429), 0);
        }
        metrics.Close();
        var window = Assert.Single(ReadAll(sink));
        Assert.Equal(new ulong[] { 1, 1, 1, 1, 2, 1 }, window.RetryAfterKinds.ToArray());
        Assert.Equal(1UL, window.RetryAfterClamped);
        Assert.Equal(1UL, window.RetryAfterClockUncertain);
        Assert.Equal(TimeSpan.Zero, window.MinimumRetryAfter);
        Assert.Equal(TimeSpan.FromDays(1), window.MaximumRetryAfter);
        Assert.Equal(1UL, window.OtherFinalStatuses);
        Assert.Equal(100.0 * 6 / 7, window.RateLimitedPercent);
        Assert.Equal(7UL, window.HttpCompletedDuration.Count);
        Assert.Equal(0UL, window[MetricCounter.HttpSuccess]);
        Assert.True(window.Quality.HasFlag(MeasurementQuality.ClockUncertain));
        Assert.Equal(MetricWindowCodec.Encode(window), MetricWindowCodec.Encode(MetricWindowCodec.Decode(MetricWindowCodec.Encode(window))));
    }

    [Theory]
    [InlineData(RequestOutcome.HttpCompleted, 202, 1)]
    [InlineData(RequestOutcome.RequestTimedOut, 200, 0)]
    [InlineData(RequestOutcome.TransportFailed, 200, 0)]
    [InlineData(RequestOutcome.CancelledInFlight, 200, 0)]
    [InlineData(RequestOutcome.ResponseLimitExceeded, 200, 0)]
    [InlineData(RequestOutcome.InternalFailed, 200, 0)]
    public void TerminalPopulationsPreserveBodyBytesAndSuccessRule(RequestOutcome outcome, int status, ulong expected)
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.RecordStarted(0);
        metrics.RecordHeaders(new(0, status, RetryAfterObservation.Parse(status, [], DateTimeOffset.UnixEpoch), 0));
        metrics.RecordTerminal(Terminal(outcome, status), 0);
        metrics.Close();
        var window = Assert.Single(ReadAll(sink));
        Assert.Equal(1UL, window.FinalStatuses);
        Assert.Equal(1UL, window.TerminalDuration.Count);
        Assert.Equal(expected, window.HttpCompletedDuration.Count);
        Assert.Equal(expected, window[MetricCounter.HttpSuccess]);
        Assert.Equal(17UL, window[MetricCounter.ResponseBodyBytes]);
        Assert.Equal(1_000_000UL, window.TerminalDuration.SumNanoseconds);
        Assert.Equal(MetricWindowCodec.Encode(window), MetricWindowCodec.Encode(MetricWindowCodec.Decode(MetricWindowCodec.Encode(window))));
    }

    [Fact]
    public void RegistrationTimeAssignsWindowWithoutInflatingIoDuration()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.RecordStarted(0);
        long ioTimestamp = TimeSpan.FromMilliseconds(4900).Ticks;
        clock.SetSeconds(6);
        metrics.RecordTerminal(Terminal(RequestOutcome.HttpCompleted, 200), ioTimestamp);
        metrics.Close();
        var windows = ReadAll(sink);
        Assert.Equal(0UL, windows[0].TerminalDuration.Count);
        Assert.Equal(1_100_000_000UL, windows[1].RegistrationDelayMaxNanoseconds);
        Assert.Equal(1_000_000UL, windows[1].TerminalDuration.SumNanoseconds);
    }

    [Fact]
    public async Task ConcurrentRotationAndUpdatesPreserveAllEventsAndImmutableSnapshots()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 100; i++)
            {
                metrics.RecordStarted(clock.GetTimestamp());
                metrics.RecordHeaders(new(worker * 100 + i, 200, default, clock.GetTimestamp()));
                metrics.RecordTerminal(Terminal(RequestOutcome.HttpCompleted, 200), clock.GetTimestamp());
            }
        })).ToArray();
        start.Set();
        clock.SetSeconds(5);
        metrics.Advance();
        clock.SetSeconds(10);
        metrics.Advance();
        await Task.WhenAll(tasks);
        clock.SetSeconds(11);
        metrics.Close();
        var windows = ReadAll(sink);
        var before = MetricWindowCodec.Encode(windows[0]);
        var totals = Fold(windows);
        Assert.Equal(800UL, totals[MetricCounter.Started]);
        Assert.Equal(800UL, totals.FinalStatuses);
        Assert.Equal(800UL, totals.TerminalDuration.Count);
        Assert.Equal(0UL, totals.LastInFlight);
        Assert.Equal(before, MetricWindowCodec.Encode(windows[0]));
    }

    [Fact]
    public void BlockedSinkLatchesPartialAtSixWindowsWithoutGrowing()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        clock.SetSeconds(35);
        Assert.Throws<MetricBackpressureException>(() => metrics.Advance());
        Assert.Equal(6, sink.Count);
        Assert.True(MetricWindowSink.MaximumReservedBytes <= 256 * 1024);
        Assert.True(MetricWindowCodec.EncodedSize * 2 < MetricWindowSink.ReservedBytesPerWindow);
        Assert.True(metrics.IsFaulted);
        Assert.True(metrics.Quality.HasFlag(MeasurementQuality.MetricsPartial));
        Assert.False(metrics.Manifest!.Value.Complete);
        Assert.Throws<InvalidOperationException>(() => metrics.RecordStarted(clock.GetTimestamp()));
        Assert.False(Fold(ReadAll(sink)).HasCompleteCoverage(metrics.Manifest.Value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricWindowSink(7));
    }

    [Fact]
    public void OverflowAndBackwardsClockAreExplicitFailures()
    {
        var clock = new Clock();
        var metrics = new MetricWindowCollector(clock, 0, new());
        metrics.RecordNotStarted(NotStartedReason.SchedulerLate, ulong.MaxValue, 0);
        Assert.Throws<OverflowException>(() => metrics.RecordStarted(0));
        Assert.True(metrics.IsFaulted);
        var other = new MetricWindowCollector(clock, 0, new());
        clock.SetSeconds(1);
        other.Advance();
        clock.SetSeconds(0);
        Assert.Throws<InvalidOperationException>(() => other.Advance());
        Assert.True(other.IsFaulted);
    }

    [Fact]
    public void MissingReplayAndOrderErrorsAreNotZeroCoverage()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        clock.SetSeconds(12);
        var manifest = metrics.Close();
        var windows = ReadAll(sink);
        Assert.Throws<InvalidDataException>(() => MetricWindowTotals.Empty.Append(windows[1]));
        var first = MetricWindowTotals.Empty.Append(windows[0]);
        Assert.Throws<InvalidDataException>(() => first.Append(windows[0]));
        Assert.Throws<InvalidDataException>(() => first.Append(windows[2]));
        Assert.False(first.HasCompleteCoverage(manifest));
    }

    [Fact]
    public void CodecRoundTripsHasStableHashAndRejectsTruncationCorruptionAndUnknownSchema()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.Close();
        var window = Assert.Single(ReadAll(sink));
        byte[] bytes = MetricWindowCodec.Encode(window);
        Assert.Equal(17832, bytes.Length);
        Assert.Equal("DNWIN001"u8.ToArray(), bytes[..8]);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 1, 0, 0, 0 }, bytes[8..16]);
        Assert.Equal(bytes, MetricWindowCodec.Encode(MetricWindowCodec.Decode(bytes)));
        Assert.Equal(Convert.ToHexString(bytes[^32..]), MetricWindowCodec.ContentHash(window));
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => MetricWindowCodec.Decode(bytes.AsSpan(0, length)));
        foreach (int offset in new[] { 0, 8, 12, 16, 24, 32, 40, 56, 60, 64, 120, 224, 4280, 17831 })
        {
            var corrupted = bytes.ToArray();
            corrupted[offset] ^= 1;
            Assert.Throws<InvalidDataException>(() => MetricWindowCodec.Decode(corrupted));
        }
        Assert.Throws<InvalidDataException>(() => MetricWindowCodec.Decode(bytes.Concat(new byte[1]).ToArray()));
        foreach (int offset in new[] { 8, 12, 16, 24, 32, 40, 56, 60, 64, 72, 80, 88, 96, 104, 112, 120, 224 })
        {
            var inconsistent = bytes.ToArray();
            inconsistent[offset] ^= 2;
            if (offset == 56) inconsistent[offset] = 128; // Unknown quality flag.
            if (offset == 40) BinaryPrimitives.WriteUInt64LittleEndian(inconsistent.AsSpan(40), (ulong)TimeSpan.FromSeconds(6).Ticks);
            SHA256.HashData(inconsistent.AsSpan(0, inconsistent.Length - 32), inconsistent.AsSpan(inconsistent.Length - 32));
            Assert.Throws<InvalidDataException>(() => MetricWindowCodec.Decode(inconsistent));
        }
    }

    [Fact]
    public void CodecRejectsIncoherentGaugeClampExtremaAndFlagsWithValidChecksum()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.RecordStarted(0);
        metrics.RecordHeaders(new(0, 429, RetryAfterObservation.Parse(429, ["17"], DateTimeOffset.UnixEpoch), 0));
        metrics.RecordTerminal(Terminal(RequestOutcome.RequestTimedOut, 429), 0);
        metrics.Close();
        byte[] valid = MetricWindowCodec.Encode(Assert.Single(ReadAll(sink)));
        foreach (int offset in new[] { 48, 56, 88, 104 })
        {
            byte[] invalid = valid.ToArray();
            if (offset == 56) BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(offset), 0);
            else BinaryPrimitives.WriteUInt64LittleEndian(invalid.AsSpan(offset), offset == 104 ? 0UL : 1UL);
            SHA256.HashData(invalid.AsSpan(0, invalid.Length - 32), invalid.AsSpan(invalid.Length - 32));
            Assert.Throws<InvalidDataException>(() => MetricWindowCodec.Decode(invalid));
        }
    }

    [Fact]
    public void TotalsDetectCounterOverflowWithoutChangingPreviousResult()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        metrics.RecordNotStarted(NotStartedReason.SchedulerLate, ulong.MaxValue, 0);
        clock.SetSeconds(5);
        metrics.RecordNotStarted(NotStartedReason.SchedulerLate, 1, clock.GetTimestamp());
        clock.SetSeconds(6);
        metrics.Close();
        var windows = ReadAll(sink);
        var first = MetricWindowTotals.Empty.Append(windows[0]);
        Assert.Throws<OverflowException>(() => first.Append(windows[1]));
        Assert.Equal(ulong.MaxValue, first[MetricCounter.ScheduledDue]);
    }

    [Fact]
    public async Task TransportPublishesHeadersBeforeBlockedBodyAndPropagatesObserverFailure()
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        var stream = new GatedStream();
        var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StreamContent(stream) };
        response.Headers.TryAddWithoutValidation("Retry-After", "17");
        using var transport = new LocalHttpTransport(Plan(), clock, TimeProvider.System, new Handler(response));
        metrics.RecordStarted(0);
        int headers = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RequestObservation> pending = transport.SendAsync(0, default, observation =>
        {
            metrics.RecordHeaders(observation);
            Interlocked.Increment(ref headers);
            received.TrySetResult();
        });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        clock.SetSeconds(6);
        stream.Release.TrySetResult();
        var terminal = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        metrics.RecordTerminal(terminal, clock.GetTimestamp());
        metrics.Close();
        var windows = ReadAll(sink);
        Assert.Equal(1, headers);
        Assert.Equal(1UL, windows[0].StatusReceived[329]);
        Assert.Equal(0UL, windows[1].FinalStatuses);
        Assert.Equal(1UL, windows[1][MetricCounter.HttpCompleted]);
        Assert.Equal(17, windows[0].MinimumRetryAfter!.Value.TotalSeconds);

        using var brokenObserver = new LocalHttpTransport(Plan(), clock, TimeProvider.System,
            new Handler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }));
        var error = await Assert.ThrowsAsync<TransportObserverException>(() => brokenObserver.SendAsync(0, default,
            _ => throw new MetricBackpressureException()));
        Assert.IsType<MetricBackpressureException>(error.InnerException);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponseHeadersObservation(0, 101, default, 0));
    }

    [Fact]
    public async Task Transport429HeadersRemainInFirstWindowWhenControlledDeadlineExpiresInNext()
    {
        var clock = new Clock();
        var time = new ManualTime();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StreamContent(new GatedStream()) };
        response.Headers.TryAddWithoutValidation("Retry-After", "17");
        using var transport = new LocalHttpTransport(Plan(TimeSpan.FromSeconds(5)), clock, time, new Handler(response));
        metrics.RecordStarted(0);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = transport.SendAsync(0, default, observation =>
        {
            metrics.RecordHeaders(observation);
            received.TrySetResult();
        });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.SetSeconds(6); // Beyond the 5 s deadline; manual timer avoids wall-clock waits.
        time.Timer!.Fire();
        var terminal = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RequestOutcome.RequestTimedOut, terminal.Outcome);
        metrics.RecordTerminal(terminal, clock.GetTimestamp());
        metrics.Close();
        var windows = ReadAll(sink);
        Assert.Equal(2, windows.Count);
        Assert.Equal(1UL, windows[0].StatusReceived[329]);
        Assert.Equal(0UL, windows[1].FinalStatuses);
        Assert.Equal(1UL, windows[1][MetricCounter.RequestTimedOut]);
        var totals = Fold(windows);
        Assert.Equal(1UL, totals.FinalStatuses);
        Assert.Equal(1UL, totals.TerminalDuration.Count);
        Assert.Equal(0UL, totals.HttpCompletedDuration.Count);
    }

    private sealed class ManualTime : TimeProvider
    {
        public ManualTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ManualTimer(callback, state);
        public sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static LoadTestPlan Plan(TimeSpan? timeout = null) => LocalPlanValidator.Validate(new LoadTestConfiguration
    {
        Url = "http://127.0.0.1:12345/", RequestTimeout = timeout ?? TimeSpan.FromSeconds(10)
    }, Guid.NewGuid()).Plan!;

    private static RequestObservation Terminal(RequestOutcome outcome, int status) => new(0, outcome,
        TimeSpan.FromMilliseconds(1), status, 17, outcome switch
        {
            RequestOutcome.HttpCompleted => RequestErrorKind.None,
            RequestOutcome.RequestTimedOut => RequestErrorKind.RequestTimeout,
            RequestOutcome.CancelledInFlight => RequestErrorKind.Cancellation,
            RequestOutcome.ResponseLimitExceeded => RequestErrorKind.ResponseLimit,
            RequestOutcome.InternalFailed => RequestErrorKind.Internal,
            _ => RequestErrorKind.ResponseRead
        });
    private static List<MetricWindow> ReadAll(MetricWindowSink sink)
    {
        var windows = new List<MetricWindow>();
        while (sink.TryRead(out var window)) windows.Add(window!);
        return windows;
    }
    private static MetricWindowTotals Fold(IEnumerable<MetricWindow> windows) =>
        windows.Aggregate(MetricWindowTotals.Empty, (totals, window) => totals.Append(window));
    private sealed class Clock : IMonotonicClock
    {
        private long ticks;
        public long GetTimestamp() => Interlocked.Read(ref ticks);
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(checked(end - start));
        public void SetSeconds(int seconds) => Interlocked.Exchange(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
    private sealed class GatedStream : Stream
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Release.Task.WaitAsync(cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
