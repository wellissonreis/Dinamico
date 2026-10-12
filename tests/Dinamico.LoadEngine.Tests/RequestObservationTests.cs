using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Tests;

public sealed class RequestObservationTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(500)]
    public void Complete_http_responses_include_error_statuses(int status)
    {
        var observation = new RequestObservation(12, RequestOutcome.HttpCompleted, TimeSpan.FromMilliseconds(80), status, 64);
        Assert.Equal(TimeSpan.FromMilliseconds(80), observation.HttpCompletedDuration);
        Assert.Equal(status, observation.StatusCode);
        Assert.Equal(RequestErrorKind.None, observation.ErrorKind);
    }

    [Theory]
    [InlineData(RequestOutcome.TransportFailed, RequestErrorKind.Dns)]
    [InlineData(RequestOutcome.TransportFailed, RequestErrorKind.Connect)]
    [InlineData(RequestOutcome.TransportFailed, RequestErrorKind.Tls)]
    [InlineData(RequestOutcome.TransportFailed, RequestErrorKind.Protocol)]
    [InlineData(RequestOutcome.TransportFailed, RequestErrorKind.ResponseRead)]
    [InlineData(RequestOutcome.RequestTimedOut, RequestErrorKind.RequestTimeout)]
    [InlineData(RequestOutcome.CancelledInFlight, RequestErrorKind.Cancellation)]
    [InlineData(RequestOutcome.ResponseLimitExceeded, RequestErrorKind.ResponseLimit)]
    [InlineData(RequestOutcome.InternalFailed, RequestErrorKind.Internal)]
    public void Non_http_outcomes_do_not_pollute_completed_latency(RequestOutcome outcome, RequestErrorKind kind)
    {
        var observation = new RequestObservation(0, outcome, TimeSpan.FromSeconds(10), null, 0, kind);
        Assert.Null(observation.HttpCompletedDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), observation.TerminalDuration);
    }

    [Fact]
    public void Headers_received_before_body_timeout_do_not_mean_http_completed()
    {
        var observation = new RequestObservation(0, RequestOutcome.RequestTimedOut, TimeSpan.FromSeconds(10), 200, 32, RequestErrorKind.RequestTimeout);
        Assert.Equal(200, observation.StatusCode);
        Assert.Null(observation.HttpCompletedDuration);
    }

    [Fact]
    public void Invalid_observations_fail_at_the_contract_boundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestObservation(-1, RequestOutcome.HttpCompleted, TimeSpan.Zero, 200, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.FromTicks(-1), 200, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 200, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 600, 0));
        Assert.Throws<ArgumentException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, null, 0));
        Assert.Throws<ArgumentException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 200, 0, RequestErrorKind.RequestTimeout));
        Assert.Throws<ArgumentException>(() => new RequestObservation(0, RequestOutcome.TransportFailed, TimeSpan.Zero, null, 0));
        Assert.Throws<ArgumentException>(() => new RequestObservation(0, (RequestOutcome)999, TimeSpan.Zero, null, 0));
        Assert.Equal(RequestOutcome.Unknown, default(RequestObservation).Outcome);
        Assert.Null(default(RequestObservation).HttpCompletedDuration);
    }
}
