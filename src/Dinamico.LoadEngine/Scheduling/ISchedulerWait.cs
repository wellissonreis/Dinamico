namespace Dinamico.LoadEngine.Scheduling;

/// <summary>Uma espera por despertar, substituível por tempo simulado; nunca por request.</summary>
public interface ISchedulerWait
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SchedulerWait : ISchedulerWait
{
    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        // Task.Delay tem um teto por chamada. A agenda relê o relógio após cada espera;
        // dividir uma espera longa não altera a origem nem acumula drift.
        var boundedDelay = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
        // Task.Delay quantiza em milissegundos: arredondar para cima evita uma
        // sequência de esperas efetivamente zero junto à borda do segmento.
        long milliseconds = boundedDelay.Ticks / TimeSpan.TicksPerMillisecond
            + (boundedDelay.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
        boundedDelay = TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond));
        await Task.Delay(boundedDelay, cancellationToken).ConfigureAwait(false);
    }
}
