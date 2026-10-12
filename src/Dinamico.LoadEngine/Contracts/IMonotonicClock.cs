namespace Dinamico.LoadEngine.Contracts;

/// <summary>Marcos do mesmo relógio; ticks não são datas nem comparáveis entre processos.</summary>
public interface IMonotonicClock
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}
