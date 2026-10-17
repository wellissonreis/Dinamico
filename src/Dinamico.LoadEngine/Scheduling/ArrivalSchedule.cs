using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Scheduling;

/// <summary>
/// Agenda constante, imutável e sem materializar chegadas. Due é um offset absoluto
/// desde o timestamp inicial do scheduler, arredondado para cima em ticks de TimeSpan
/// (erro inferior a 100 ns). Nunca somar intervalos para calcular o próximo due.
/// </summary>
public sealed class ArrivalSchedule
{
    public ArrivalSchedule(long requestsPerSecond, long durationSeconds,
        TimeSpan? quantum = null, TimeSpan? latenessTolerance = null,
        int workerCount = 1, int workerSlot = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        if (workerSlot < 0 || workerSlot >= workerCount)
            throw new ArgumentOutOfRangeException(nameof(workerSlot));
        Quantum = quantum ?? TimeSpan.FromMilliseconds(1);
        LatenessTolerance = latenessTolerance ?? TimeSpan.FromMilliseconds(5);
        if (Quantum <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quantum));
        if (LatenessTolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(latenessTolerance));

        RequestsPerSecond = requestsPerSecond;
        Duration = TimeSpan.FromTicks(checked(durationSeconds * TimeSpan.TicksPerSecond));
        PlannedTotal = checked(requestsPerSecond * durationSeconds);
        WorkerCount = workerCount;
        WorkerSlot = workerSlot;
        AssignedTotal = CountAssignedBefore(PlannedTotal);
        // ceil(R / W) é um teto conservador; não arredondar a taxa de cada parcela.
        long localRate = checked((long)(((Int128)requestsPerSecond + workerCount - 1) / workerCount));
        MaxArrivalsPerWake = checked((long)DivideUp(
            checked((Int128)localRate * Quantum.Ticks), TimeSpan.TicksPerSecond) + 1);
    }

    public static ArrivalSchedule FromPlan(LoadTestPlan plan,
        TimeSpan? quantum = null, TimeSpan? latenessTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Modos, status, Retry-After e duração de respostas não são entradas da agenda.
        return new(plan.RequestsPerSecond, plan.DurationSeconds, quantum, latenessTolerance);
    }

    public long RequestsPerSecond { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Quantum { get; }
    public TimeSpan LatenessTolerance { get; }
    public long PlannedTotal { get; }
    public long AssignedTotal { get; }
    public int WorkerCount { get; }
    public int WorkerSlot { get; }
    public long MaxArrivalsPerWake { get; }

    public ArrivalIntent GetArrival(long assignedOrdinal)
    {
        if (assignedOrdinal < 0 || assignedOrdinal >= AssignedTotal)
            throw new ArgumentOutOfRangeException(nameof(assignedOrdinal));
        long index = checked((long)checked((Int128)assignedOrdinal * WorkerCount + WorkerSlot));
        Int128 numerator = checked(checked(2 * (Int128)index + 1) * TimeSpan.TicksPerSecond);
        long ticks = checked((long)DivideUp(numerator, checked(2 * (Int128)RequestsPerSecond)));
        return new(index, TimeSpan.FromTicks(ticks));
    }

    /// <summary>Quantidade atribuída com due quantizado menor ou igual ao offset.</summary>
    public long CountDue(TimeSpan elapsed)
    {
        if (elapsed.Ticks < 0 || PlannedTotal == 0)
            return 0;
        if (elapsed >= Duration)
            return AssignedTotal;
        Int128 numerator = checked(checked(2 * (Int128)RequestsPerSecond * elapsed.Ticks)
            + TimeSpan.TicksPerSecond);
        long globalCount = checked((long)(numerator / (2 * TimeSpan.TicksPerSecond)));
        return CountAssignedBefore(Math.Min(globalCount, PlannedTotal));
    }

    private long CountAssignedBefore(long globalExclusive) => globalExclusive <= WorkerSlot
        ? 0 : 1 + (globalExclusive - 1 - WorkerSlot) / WorkerCount;

    private static Int128 DivideUp(Int128 numerator, Int128 denominator) =>
        checked(numerator / denominator + (numerator % denominator == 0 ? 0 : 1));
}

/// <summary>Intenção de tentativa de admissão; não comprova início nem envio HTTP.</summary>
public readonly record struct ArrivalIntent(long Index, TimeSpan Due);
