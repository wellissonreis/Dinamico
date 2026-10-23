using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Scheduling;

/// <summary>
/// Consumidor único. Estado O(1), salto O(1), sem HTTP/admissão/tasks de operações.
/// Poll consome todas as due no instante observado, inclusive os descartes por teto.
/// O chamador deve consumir a microbatelada prontamente, sem aguardar respostas.
/// </summary>
public sealed class ArrivalScheduler
{
    private readonly IMonotonicClock _clock;
    private readonly ISchedulerWait _wait;
    private long _nextOrdinal;
    private long _lastElapsedTicks = long.MinValue;

    public ArrivalScheduler(ArrivalSchedule schedule, IMonotonicClock clock,
        long startingTimestamp, ISchedulerWait? wait = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(clock);
        Schedule = schedule;
        _clock = clock;
        _wait = wait ?? new SchedulerWait();
        StartingTimestamp = startingTimestamp;
    }

    public ArrivalSchedule Schedule { get; }
    public long StartingTimestamp { get; }
    public long ScheduledDue { get; private set; }
    public long OfferedArrivals { get; private set; }
    public long SchedulerLate { get; private set; }
    public long WakeCount { get; private set; }
    public long LimitedWakeCount { get; private set; }
    public long ToleranceExceededDrops { get; private set; }
    public long WakeCapacityDrops { get; private set; }
    public long EndBoundaryDrops { get; private set; }
    public TimeSpan MaxObservedLateness { get; private set; }
    public bool IsCompleted { get; private set; }
    /// <summary>Restante atribuído; uma parada externa pode reportá-lo como não agendado.</summary>
    public long NotScheduled => Schedule.AssignedTotal - ScheduledDue;

    public SchedulerWake Poll()
    {
        if (IsCompleted)
            return new(Schedule, _nextOrdinal, 0, 0, 0, true);

        long elapsed = ReadElapsedTicks();
        long dueEnd = Schedule.CountDue(TimeSpan.FromTicks(elapsed));
        long dueCount = dueEnd - _nextOrdinal;
        bool completed = elapsed >= Schedule.Duration.Ticks;
        long firstOffered = _nextOrdinal;
        long offered = 0;
        long excess = 0;
        if (dueCount != 0)
        {
            long lateness = checked(elapsed - Schedule.GetArrival(_nextOrdinal).Due.Ticks);
            if (lateness > MaxObservedLateness.Ticks)
                MaxObservedLateness = TimeSpan.FromTicks(lateness);
            if (!completed)
            {
                // due < elapsed - tolerância é late; igualdade continua admissível.
                long strictThreshold = elapsed - Schedule.LatenessTolerance.Ticks;
                long lateEnd = strictThreshold <= 0 ? 0
                    : Schedule.CountDue(TimeSpan.FromTicks(strictThreshold - 1));
                firstOffered = Math.Max(_nextOrdinal, lateEnd);
                long acceptable = dueEnd - firstOffered;
                offered = Math.Min(acceptable, Schedule.MaxArrivalsPerWake);
                excess = acceptable - offered;
            }
        }

        long dropped = dueCount - offered;
        if (completed)
            EndBoundaryDrops = checked(EndBoundaryDrops + dropped);
        else
        {
            ToleranceExceededDrops = checked(ToleranceExceededDrops + dropped - excess);
            WakeCapacityDrops = checked(WakeCapacityDrops + excess);
        }
        _nextOrdinal = dueEnd; // Não deixar excedentes para o próximo despertar.
        ScheduledDue = checked(ScheduledDue + dueCount);
        OfferedArrivals = checked(OfferedArrivals + offered);
        SchedulerLate = checked(SchedulerLate + dropped);
        WakeCount = checked(WakeCount + 1);
        if (excess > 0)
            LimitedWakeCount = checked(LimitedWakeCount + 1);
        IsCompleted = completed;
        return new(Schedule, firstOffered, offered, dueCount, dropped, completed);
    }

    /// <summary>
    /// Espera na grade absoluta do quantum, pulando despertares vazios. Pode retornar
    /// batch vazio ao acordar cedo. Cancelamento não consome chegadas nem fecha o plano.
    /// </summary>
    public async ValueTask<SchedulerWake> WaitForNextWakeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCompleted)
        {
            long elapsed = ReadElapsedTicks();
            long target = Schedule.Duration.Ticks;
            if (_nextOrdinal < Schedule.AssignedTotal)
            {
                long due = Schedule.GetArrival(_nextOrdinal).Due.Ticks;
                Int128 grid = checked(((Int128)due + Schedule.Quantum.Ticks - 1)
                    / Schedule.Quantum.Ticks * Schedule.Quantum.Ticks);
                target = checked((long)Int128.Min(grid, Schedule.Duration.Ticks));
                if (target == Schedule.Duration.Ticks && due < target)
                {
                    // A última microbatelada deve acordar antes do fim, mesmo quando
                    // a grade do quantum coincidir com ele. Quantização sub-tick pode
                    // deixar as últimas intenções exatamente no fim: essas serão drops.
                    long lastDue = Schedule.GetArrival(Schedule.AssignedTotal - 1).Due.Ticks;
                    target = Math.Min(lastDue, Schedule.Duration.Ticks - 1);
                }
            }
            if (elapsed < target)
            {
                await _wait.DelayAsync(TimeSpan.FromTicks(checked(target - elapsed)), cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return Poll();
    }

    private long ReadElapsedTicks()
    {
        long elapsed = _clock.GetElapsedTime(StartingTimestamp, _clock.GetTimestamp()).Ticks;
        if (elapsed < _lastElapsedTicks)
            throw new InvalidOperationException("Monotonic clock moved backwards.");
        _lastElapsedTicks = elapsed;
        return elapsed;
    }
}

/// <summary>
/// Microbatelada imutável representada por intervalo de ordinais, sem array de intenções.
/// DueCount = ArrivalCount + SchedulerLate. Todos os descartes usam SchedulerLate,
/// inclusive o teto de despertar e o fim da duração. Nenhuma intenção é oferecida no fim.
/// </summary>
public readonly struct SchedulerWake
{
    private readonly ArrivalSchedule? _schedule;
    private readonly long _firstOrdinal;

    internal SchedulerWake(ArrivalSchedule schedule, long firstOrdinal, long arrivalCount,
        long dueCount, long schedulerLate, bool isCompleted)
    {
        _schedule = schedule;
        _firstOrdinal = firstOrdinal;
        ArrivalCount = arrivalCount;
        DueCount = dueCount;
        SchedulerLate = schedulerLate;
        IsCompleted = isCompleted;
    }

    public long ArrivalCount { get; }
    public long DueCount { get; }
    public long SchedulerLate { get; }
    public bool IsCompleted { get; }

    public ArrivalIntent this[long offset]
    {
        get
        {
            if (offset < 0 || offset >= ArrivalCount)
                throw new ArgumentOutOfRangeException(nameof(offset));
            return _schedule!.GetArrival(checked(_firstOrdinal + offset));
        }
    }
}
