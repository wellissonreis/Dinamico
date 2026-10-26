using Dinamico.Persistence;

namespace Dinamico.Web;

/// <summary>Applies the canonical 7/30/90-day retention while the web host is running.</summary>
public sealed class RetentionService(DatabaseState database, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            if (database.Store is { } store)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await store.PruneAsync(DateTimeOffset.UtcNow, 200, deadline.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception) { logger.LogWarning("Limpeza de retenção indisponível; nova tentativa em um minuto."); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
