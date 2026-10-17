using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Tests.Fixtures;
using Dinamico.LoadEngine.Timing;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class TimingDeadlineTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(1);
    // Real I/O is cooperative; allow 5 s of OS/runtime scheduling beyond the configured ceiling.
    private static readonly TimeSpan SchedulingTolerance = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0, 1, 3, 3333333)]
    [InlineData(10, 13, 3, 10000000)]
    [InlineData(0, 1, 1000000000, 0)]
    [InlineData(long.MinValue, long.MaxValue, long.MaxValue, 20000000)]
    [InlineData(0, long.MaxValue, 10000000, long.MaxValue)]
    public void Tick_conversion_uses_checked_integer_math(long start, long end, long frequency, long expected)
    {
        Assert.Equal(expected, StopwatchMonotonicClock.ConvertElapsed(start, end, frequency).Ticks);
    }

    [Fact]
    public void Tick_conversion_rejects_backwards_time_invalid_frequency_and_final_overflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StopwatchMonotonicClock.ConvertElapsed(2, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StopwatchMonotonicClock.ConvertElapsed(0, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StopwatchMonotonicClock.ConvertElapsed(0, 1, -1));
        Assert.Throws<OverflowException>(() => StopwatchMonotonicClock.ConvertElapsed(0, long.MaxValue, 1));
    }

    [Theory]
    [InlineData("timeout", "cancel", "eof", RequestOutcome.RequestTimedOut)]
    [InlineData("cancel", "timeout", "eof", RequestOutcome.CancelledInFlight)]
    [InlineData("eof", "cancel", "timeout", RequestOutcome.HttpCompleted)]
    [InlineData("eof", "timeout", "cancel", RequestOutcome.HttpCompleted)]
    [InlineData("cancel", "eof", "timeout", RequestOutcome.CancelledInFlight)]
    [InlineData("timeout", "eof", "cancel", RequestOutcome.RequestTimedOut)]
    public void Every_ordering_latches_one_terminal_and_one_end(string first, string second, string third,
        RequestOutcome expected)
    {
        var time = new ManualTiming();
        using var stop = new CancellationTokenSource();
        using var operation = new OperationDeadline(time, time, TestDeadline, stop.Token);
        time.Advance(TimeSpan.FromMilliseconds(100));
        operation.MarkHeaders();
        operation.MarkHeaders();
        Assert.Equal(TimeSpan.FromMilliseconds(100).Ticks, operation.HeadersTimestamp);

        void Apply(string reason)
        {
            if (reason == "timeout") time.Advance(TestDeadline);
            else if (reason == "cancel") stop.Cancel();
            else operation.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
        }

        Apply(first);
        var terminal = operation.Result;
        long? end = operation.EndedTimestamp;
        Apply(second);
        Apply(third);
        operation.Complete(RequestOutcome.TransportFailed, RequestErrorKind.ResponseRead);

        Assert.Equal(expected, terminal.Outcome);
        Assert.Equal(terminal, operation.Result);
        Assert.Equal(end, operation.EndedTimestamp);
        Assert.Equal(expected != RequestOutcome.HttpCompleted, operation.Token.IsCancellationRequested);
    }

    [Fact]
    public void Exact_deadline_and_late_EOF_timeout_even_if_timer_callback_is_delayed()
    {
        foreach (TimeSpan elapsed in new[] { TestDeadline, TestDeadline + TimeSpan.FromTicks(1) })
        {
            var time = new ManualTiming();
            using var operation = new OperationDeadline(time, time, TestDeadline, default);
            time.Advance(elapsed, fireTimers: false);
            operation.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
            Assert.Equal(RequestOutcome.RequestTimedOut, operation.Result.Outcome);
            Assert.Equal(elapsed, operation.Result.Duration);
        }
    }

    [Fact]
    public void Early_timer_rearms_remaining_monotonic_time_and_UTC_jumps_do_not_expire_it()
    {
        var time = new ManualTiming();
        using var operation = new OperationDeadline(time, time, TestDeadline, default);
        time.Advance(TimeSpan.FromMilliseconds(200), fireTimers: false);
        time.Utc = time.Utc.AddYears(20);
        time.LastTimer.Fire();
        Assert.Equal(RequestOutcome.Unknown, operation.Result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(800), time.LastTimer.DueTime);
        time.Utc = time.Utc.AddYears(-40);
        time.Advance(TimeSpan.FromMilliseconds(800));
        Assert.Equal(RequestOutcome.RequestTimedOut, operation.Result.Outcome);
        Assert.Equal(TestDeadline, operation.Result.Duration);
    }

    [Fact]
    public void Submillisecond_timer_is_rounded_up_but_completion_checks_exact_deadline()
    {
        var time = new ManualTiming();
        using var operation = new OperationDeadline(time, time, TimeSpan.FromTicks(1), default);
        Assert.Equal(TimeSpan.FromMilliseconds(1), time.LastTimer.DueTime);
        time.Advance(TimeSpan.FromTicks(1), fireTimers: false);
        operation.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
        Assert.Equal(RequestOutcome.RequestTimedOut, operation.Result.Outcome);
    }

    [Fact]
    public void Disposal_unregisters_cancellation_and_ignores_already_queued_timer_callback()
    {
        var time = new ManualTiming();
        using var stop = new CancellationTokenSource();
        var operation = new OperationDeadline(time, time, TestDeadline, stop.Token);
        operation.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
        var terminal = operation.Result;
        operation.Dispose();
        operation.Dispose();
        stop.Cancel();
        time.Advance(TestDeadline);
        time.LastTimer.Fire(); // Deliberately simulate a callback queued before disposal.
        Assert.Equal(0, time.ActiveTimers);
        Assert.Equal(terminal, operation.Result);
    }

    [Fact]
    public void Backwards_monotonic_time_is_an_internal_failure_with_nonnegative_duration()
    {
        var time = new ManualTiming();
        time.Advance(TestDeadline);
        using var operation = new OperationDeadline(time, time, TestDeadline, default);
        time.Timestamp = 0;
        operation.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
        Assert.Equal(RequestOutcome.InternalFailed, operation.Result.Outcome);
        Assert.Equal(RequestErrorKind.Internal, operation.Result.Error);
        Assert.Equal(TimeSpan.Zero, operation.Result.Duration);
        Assert.True(operation.Token.IsCancellationRequested);
    }

    [Fact]
    public void Failed_timer_setup_disposes_timer_and_unregisters_external_stop()
    {
        var time = new ManualTiming { FailArming = true };
        using var stop = new CancellationTokenSource();
        Assert.Throws<InvalidOperationException>(() => new OperationDeadline(time, time, TestDeadline, stop.Token));
        Assert.Equal(0, time.ActiveTimers);
        stop.Cancel();
        time.LastTimer.Fire();
    }

    [Fact]
    public void Reentrant_external_cancel_cannot_override_the_timeout_that_signalled_it()
    {
        var time = new ManualTiming();
        using var stop = new CancellationTokenSource();
        using var operation = new OperationDeadline(time, time, TestDeadline, stop.Token);
        using var callback = operation.Token.Register(stop.Cancel);
        time.Advance(TestDeadline);
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(RequestOutcome.RequestTimedOut, operation.Result.Outcome);
    }

    [Fact]
    public void Cancellation_callback_can_complete_and_dispose_from_another_thread_without_blocking_the_latch()
    {
        var time = new ManualTiming();
        using var operation = new OperationDeadline(time, time, TestDeadline, default);
        bool completedDuringCallback = false;
        using var callback = operation.Token.Register(() =>
        {
            Task continuation = Task.Run(() =>
            {
                operation.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
                operation.Dispose();
            });
            completedDuringCallback = continuation.Wait(SchedulingTolerance);
        });
        time.Advance(TestDeadline);
        Assert.True(completedDuringCallback);
        Assert.Equal(RequestOutcome.RequestTimedOut, operation.Result.Outcome);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Theory]
    [InlineData(false, RequestOutcome.RequestTimedOut)]
    [InlineData(true, RequestOutcome.CancelledInFlight)]
    public async Task Body_EOF_cannot_override_a_latched_stop_and_headers_survive(bool cancelFirst,
        RequestOutcome expected)
    {
        var time = new ManualTiming();
        using var stop = new CancellationTokenSource();
        using var stream = new ProbeStream(_ =>
        {
            if (cancelFirst) stop.Cancel();
            time.Advance(TestDeadline);
            stop.Cancel();
            return ValueTask.FromResult(0); // I/O raced with cancellation and still reports EOF.
        });
        using var handler = new DelegateHandler((_, _) => Task.FromResult(Response(429, stream, "17")));
        using var transport = new LocalHttpTransport(Plan(new Uri("http://127.0.0.1/"), TestDeadline), time, time, handler);

        RequestObservation result = await transport.SendAsync(2, stop.Token);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.True(result.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter.Delay);
        Assert.Null(result.HttpCompletedDuration);
        Assert.False(result.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.True(stream.Disposed);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public async Task Deadline_during_send_has_no_invented_header_evidence_and_disposes_resources()
    {
        var time = new ManualTiming();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpRequestMessage? capturedRequest = null;
        var plan = Plan(new Uri("http://127.0.0.1/"), TestDeadline, payload: [1, 2, 3]);
        using var handler = new DelegateHandler(async (request, token) =>
        {
            capturedRequest = request;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var transport = new LocalHttpTransport(plan, time, time, handler);
        Task<RequestObservation> pending = transport.SendAsync(0);
        await entered.Task.WaitAsync(SchedulingTolerance);
        time.Advance(TestDeadline);
        RequestObservation result = await pending.WaitAsync(SchedulingTolerance);
        Assert.Equal(RequestOutcome.RequestTimedOut, result.Outcome);
        Assert.Equal(RequestErrorKind.RequestTimeout, result.ErrorKind);
        Assert.Equal(TestDeadline, result.TerminalDuration);
        Assert.Null(result.StatusCode);
        Assert.Equal(RetryAfterKind.NotObserved, result.RetryAfter.Kind);
        Assert.Equal(0, time.ActiveTimers);
        Assert.NotNull(capturedRequest?.Content);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capturedRequest.Content.ReadAsStreamAsync());
        transport.Dispose();
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task Sending_and_body_read_share_one_budget_and_preserve_partial_byte_count()
    {
        var time = new ManualTiming();
        int reads = 0;
        using var stream = new ProbeStream(_ =>
        {
            if (++reads == 1) return ValueTask.FromResult(7);
            time.Advance(TimeSpan.FromMilliseconds(800));
            return ValueTask.FromResult(0);
        });
        using var handler = new DelegateHandler((_, _) =>
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            return Task.FromResult(Response(429, stream, "9"));
        });
        using var transport = new LocalHttpTransport(Plan(new Uri("http://127.0.0.1/"), TestDeadline), time, time, handler);
        RequestObservation result = await transport.SendAsync(0);
        Assert.Equal(RequestOutcome.RequestTimedOut, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(1100), result.TerminalDuration);
        Assert.Equal(7, result.ResponseBodyBytes);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(9), result.RetryAfter.Delay);
        Assert.True(stream.Disposed);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public async Task Cancellation_and_timeout_during_disposal_cannot_replace_an_observed_EOF()
    {
        var time = new ManualTiming();
        using var stop = new CancellationTokenSource();
        using var stream = new ProbeStream(_ => ValueTask.FromResult(0), () =>
        {
            time.Advance(TestDeadline);
            stop.Cancel();
        });
        using var handler = new DelegateHandler((_, _) => Task.FromResult(Response(200, stream, "9")));
        using var transport = new LocalHttpTransport(Plan(new Uri("http://127.0.0.1/"), TestDeadline), time, time, handler);
        RequestObservation result = await transport.SendAsync(0, stop.Token);
        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(TimeSpan.Zero, result.TerminalDuration);
        Assert.True(result.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.True(stream.Disposed);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Theory]
    [InlineData(false, RequestOutcome.TransportFailed, RetryAfterKind.Missing)]
    [InlineData(true, RequestOutcome.InternalFailed, RetryAfterKind.NotObserved)]
    public async Task Read_failure_preserves_captured_missing_or_capture_not_performed(bool failCapture,
        RequestOutcome expected, RetryAfterKind evidence)
    {
        var time = new ManualTiming { FailUtc = failCapture };
        using var stream = new ProbeStream(_ => throw new IOException("synthetic read failure"));
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StreamContent(stream)
        }));
        using var transport = new LocalHttpTransport(Plan(new Uri("http://127.0.0.1/")), time, time, handler);
        RequestObservation result = await transport.SendAsync(0);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(evidence, result.RetryAfter.Kind);
        Assert.Equal(429, result.StatusCode);
        Assert.True(result.IsRateLimited);
        Assert.Null(result.HttpCompletedDuration);
        Assert.True(stream.Disposed);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public async Task Connection_timeout_is_distinct_from_unexplained_transport_cancellation()
    {
        foreach (bool connectionTimeout in new[] { true, false })
        {
            var time = new ManualTiming();
            using var handler = new DelegateHandler((_, _) => throw new OperationCanceledException(
                "synthetic", connectionTimeout ? new TimeoutException() : null));
            using var transport = new LocalHttpTransport(Plan(new Uri("http://127.0.0.1/")), time, time, handler);
            RequestObservation result = await transport.SendAsync(0);
            Assert.Equal(connectionTimeout ? RequestOutcome.RequestTimedOut : RequestOutcome.TransportFailed,
                result.Outcome);
            Assert.Equal(connectionTimeout ? RequestErrorKind.RequestTimeout : RequestErrorKind.Connect,
                result.ErrorKind);
            Assert.Equal(0, time.ActiveTimers);
        }
    }

    [Fact]
    public async Task UTC_jump_during_body_changes_neither_duration_nor_captured_retry_after_date()
    {
        var time = new ManualTiming();
        DateTimeOffset reference = time.Utc;
        using var stream = new ProbeStream(_ =>
        {
            time.Utc = reference.AddYears(-30);
            time.Advance(TimeSpan.FromMilliseconds(250));
            return ValueTask.FromResult(0);
        });
        using var handler = new DelegateHandler((_, _) => Task.FromResult(Response(429, stream,
            reference.AddSeconds(30).ToString("r"))));
        using var transport = new LocalHttpTransport(Plan(new Uri("http://127.0.0.1/")), time, time, handler);
        RequestObservation result = await transport.SendAsync(0);
        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(250), result.TerminalDuration);
        Assert.Equal(result.TerminalDuration, result.HttpCompletedDuration);
        Assert.Equal(reference, result.RetryAfter.ReferenceUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter.Delay);
        Assert.True(result.RetryAfter.ClockUncertain);
        Assert.Equal(0, time.ActiveTimers);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Real_fast_headers_stalled_body_obeys_total_deadline(int status)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = status, RetryAfterValues = ["8"], BodyBytes = 64,
            ChunkDelay = TimeSpan.FromSeconds(10)
        });
        using var transport = new LocalHttpTransport(Plan(fixture.Address, TestDeadline));
        var wall = Stopwatch.StartNew();
        RequestObservation result = await transport.SendAsync(0).WaitAsync(TestDeadline + SchedulingTolerance);
        Assert.Equal(RequestOutcome.RequestTimedOut, result.Outcome);
        Assert.Equal(RequestErrorKind.RequestTimeout, result.ErrorKind);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(0, result.ResponseBodyBytes);
        Assert.Equal(status == 429, result.IsRateLimited);
        Assert.Equal(status == 429 ? RetryAfterKind.DeltaSeconds : RetryAfterKind.NotApplicable, result.RetryAfter.Kind);
        Assert.Null(result.HttpCompletedDuration);
        Assert.False(result.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.InRange(result.TerminalDuration, TestDeadline, TestDeadline + SchedulingTolerance);
        Assert.True(wall.Elapsed <= TestDeadline + SchedulingTolerance);
        Assert.Equal(1, fixture.ReceivedCount);
        await WaitUntilAsync(() => fixture.ActiveRequests == 0);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("send")]
    [InlineData("body")]
    public async Task Real_cancellation_before_send_during_send_and_during_body(string phase)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429, RetryAfterValues = ["5"], BodyBytes = 64,
            HeaderDelay = phase == "send" ? TimeSpan.FromSeconds(10) : TimeSpan.Zero,
            ChunkDelay = TimeSpan.FromSeconds(10)
        });
        using var stop = new CancellationTokenSource();
        var time = new HeaderSignalTimeProvider();
        using var transport = new LocalHttpTransport(Plan(fixture.Address), StopwatchMonotonicClock.Instance, time);
        if (phase == "before") stop.Cancel();
        Task<RequestObservation> pending = transport.SendAsync(0, stop.Token);
        if (phase == "send") await WaitUntilAsync(() => fixture.ReceivedCount == 1);
        if (phase == "body") await time.Headers.Task.WaitAsync(SchedulingTolerance);
        stop.Cancel();
        RequestObservation result = await pending.WaitAsync(SchedulingTolerance);
        Assert.Equal(RequestOutcome.CancelledInFlight, result.Outcome);
        Assert.Equal(RequestErrorKind.Cancellation, result.ErrorKind);
        Assert.Null(result.HttpCompletedDuration);
        Assert.True(result.TerminalDuration >= TimeSpan.Zero);
        if (phase == "body")
        {
            Assert.Equal(429, result.StatusCode);
            Assert.True(result.IsRateLimited);
            Assert.Equal(TimeSpan.FromSeconds(5), result.RetryAfter.Delay);
        }
        else
        {
            Assert.Null(result.StatusCode);
            Assert.Equal(RetryAfterKind.NotObserved, result.RetryAfter.Kind);
        }
        Assert.Equal(phase == "before" ? 0 : 1, fixture.ReceivedCount);
        await WaitUntilAsync(() => fixture.ActiveRequests == 0);
    }

    [Fact]
    public async Task Real_503_retry_after_is_completed_without_rate_limit_or_retry()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 503, RetryAfterValues = ["30"], BodyBytes = 19
        });
        using var transport = new LocalHttpTransport(Plan(fixture.Address, TestDeadline));
        RequestObservation result = await transport.SendAsync(0).WaitAsync(TestDeadline + SchedulingTolerance);
        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(19, result.ResponseBodyBytes);
        Assert.False(result.IsRateLimited);
        Assert.False(result.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.Equal(RetryAfterKind.NotApplicable, result.RetryAfter.Kind);
        Assert.Equal(result.TerminalDuration, result.HttpCompletedDuration);
        Assert.Equal(1, fixture.ReceivedCount);
    }

    [Fact]
    public async Task Real_connection_ceiling_covers_stalled_TLS_before_the_total_deadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var plan = Plan(new Uri($"https://127.0.0.1:{port}/"), TimeSpan.FromSeconds(10));
        using var transport = new LocalHttpTransport(plan);
        Task<RequestObservation> pending = transport.SendAsync(0);
        using TcpClient peer = await listener.AcceptTcpClientAsync().WaitAsync(SchedulingTolerance);
        // Keep the socket open without answering ClientHello, so ConnectTimeout includes TLS.
        RequestObservation result = await pending.WaitAsync(plan.ConnectTimeout + SchedulingTolerance);
        Assert.Equal(TimeSpan.FromSeconds(3), plan.ConnectTimeout);
        Assert.Equal(RequestOutcome.RequestTimedOut, result.Outcome);
        Assert.Equal(RequestErrorKind.RequestTimeout, result.ErrorKind);
        Assert.Null(result.StatusCode);
        Assert.Equal(RetryAfterKind.NotObserved, result.RetryAfter.Kind);
        Assert.InRange(result.TerminalDuration, plan.ConnectTimeout, plan.ConnectTimeout + SchedulingTolerance);
        Assert.True(result.TerminalDuration < plan.RequestTimeout);
    }

    private static LoadTestPlan Plan(Uri address, TimeSpan? timeout = null, byte[]? payload = null)
    {
        var result = LocalPlanValidator.Validate(new LoadTestConfiguration
        {
            Url = address.AbsoluteUri, RequestTimeout = timeout ?? TimeSpan.FromSeconds(10),
            Method = payload is null ? "GET" : "POST", Payload = payload ?? []
        }, Guid.NewGuid());
        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(error => error.Code)));
        return result.Plan!;
    }

    private static HttpResponseMessage Response(int status, Stream stream, string retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(SchedulingTolerance);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class HeaderSignalTimeProvider : TimeProvider
    {
        internal TaskCompletionSource Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow()
        {
            Headers.TrySetResult();
            return TimeProvider.System.GetUtcNow();
        }
    }

    private sealed class ManualTiming : TimeProvider, IMonotonicClock
    {
        private readonly List<ManualTimer> _timers = [];
        internal long Timestamp { get; set; }
        internal DateTimeOffset Utc { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        internal bool FailArming { get; init; }
        internal bool FailUtc { get; init; }
        internal ManualTimer LastTimer => _timers[^1];
        internal int ActiveTimers => _timers.Count(timer => !timer.Disposed);
        public override DateTimeOffset GetUtcNow() => FailUtc ? throw new InvalidOperationException("synthetic UTC failure") : Utc;
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        TimeSpan IMonotonicClock.GetElapsedTime(long start, long end) =>
            StopwatchMonotonicClock.ConvertElapsed(start, end, TimeSpan.TicksPerSecond);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            Timestamp = checked(Timestamp + elapsed.Ticks);
            if (fireTimers)
                foreach (ManualTimer timer in _timers.ToArray())
                    if (!timer.Disposed && timer.DueTimestamp <= Timestamp) timer.Fire();
        }

        internal sealed class ManualTimer(ManualTiming time, TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed { get; private set; }
            internal long DueTimestamp { get; private set; } = long.MaxValue;
            internal TimeSpan DueTime { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                if (Disposed) return false;
                if (time.FailArming && dueTime != Timeout.InfiniteTimeSpan)
                    throw new InvalidOperationException("synthetic timer failure");
                DueTime = dueTime;
                DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : time.Timestamp + dueTime.Ticks;
                return true;
            }
            internal void Fire()
            {
                DueTimestamp = long.MaxValue;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ProbeStream(Func<CancellationToken, ValueTask<int>> read, Action? onDispose = null) : Stream
    {
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => read(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                Disposed = true;
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
