using System.Diagnostics;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.LoadEngine.Metrics;
using Dinamico.Persistence;

namespace Dinamico.Web;

public sealed class DatabaseState(string? connectionString) : IHostedService
{
    private bool migrated;
    public PostgresRunStore? Store { get; } = string.IsNullOrWhiteSpace(connectionString) ? null : new(connectionString);
    public bool Available { get; private set; }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Store is null) return;
        try { await Store.MigrateAsync(cancellationToken); migrated = true; Available = true; }
        catch (Exception) { Available = false; }
    }
    public async Task<bool> ProbeAsync(CancellationToken token)
    {
        if (Store is null) return false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            if (!migrated) { await Store.MigrateAsync(deadline.Token); migrated = true; }
            await Store.MeasureAsync(deadline.Token);
            Available = true;
        }
        catch (Exception) { Available = false; }
        return Available;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ExecutionService(DatabaseState database) : IHostedService
{
    private readonly object gate = new();
    private readonly Queue<Session> completed = new();
    private Session? active;
    private bool stopping;
    internal ExecutionView? Active(Guid project) { lock (gate) return active?.Project == project ? active.View() : null; }
    internal ExecutionView? Find(Guid project, Guid id)
    {
        lock (gate) return (active?.Id == id && active.Project == project ? active : completed.FirstOrDefault(x => x.Id == id && x.Project == project))?.View();
    }
    internal ExecutionView? Start(Guid project, LoadTestPlan plan)
    {
        lock (gate)
        {
            if (active is not null || stopping) return null;
            var session = new Session(project, plan);
            active = session;
            var initial = session.View();
            session.Work = Task.Run(() => RunAsync(session, plan));
            return initial;
        }
    }
    internal ExecutionView? Cancel(Guid project, Guid id)
    {
        lock (gate)
        {
            if (active?.Project != project || active.Id != id) return Find(project, id);
            active.Cancel.Cancel();
            return active.View();
        }
    }
    private async Task RunAsync(Session session, LoadTestPlan plan)
    {
        try
        {
            var result = await new LocalRunController().RunAsync(plan, session, session.Cancel.Token);
            session.Finish(result);
            try
            {
                var snapshot = new LocalRunSnapshot((int)plan.RequestsPerSecond, (int)plan.DurationSeconds,
                    plan.MaxConcurrency, plan.RateLimitMode, plan.RetryPolicy, plan.SuccessRule, plan.Method,
                    plan.RequestTimeout.Ticks, plan.MaxResponseBytes, plan.Target.Scheme, plan.Target.Port);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await database.Store!.ImportAsync(new(session.Project, session.Id, session.Started, session.Finished!.Value,
                    snapshot, session.Windows(), result.DataComplete, (ulong)result.PlannedTotal, (ulong)result.ScheduledDue,
                    (ulong)result.NotScheduledAfterStop, result.Manifest, result.Completion, result.Reason, result.Failures, result.Quality), deadline.Token);
                session.Persisted(true);
            }
            catch (Exception) { session.Persisted(false); }
        }
        catch (Exception) { session.Failed(); }
        finally
        {
            lock (gate)
            {
                active = null;
                completed.Enqueue(session);
                while (completed.Count > 20) completed.Dequeue().Cancel.Dispose();
            }
        }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? work;
        lock (gate) { stopping = true; active?.Cancel.Cancel(); work = active?.Work; }
        if (work is not null)
            try { await work.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            catch (Exception) { /* Shutdown remains bounded; no sensitive exception output. */ }
    }
    private sealed class Session : ILocalMetricOutput
    {
        private readonly object gate = new();
        private readonly List<MetricWindow> windows = new(133);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PlanView plan;
        private readonly ulong planned;
        private MetricWindowTotals totals = MetricWindowTotals.Empty;
        private LocalRunResult? result;
        private string persistence = "pending";
        private bool failed;
        internal Session(Guid project, LoadTestPlan p)
        {
            Project = project; Id = p.ExecutionId; planned = (ulong)p.PlannedTotal;
            plan = new(p.RequestsPerSecond, p.DurationSeconds, p.MaxConcurrency, p.Method, p.RateLimitMode.ToString());
        }
        internal Guid Project { get; }
        internal Guid Id { get; }
        internal DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        internal DateTimeOffset? Finished { get; private set; }
        internal CancellationTokenSource Cancel { get; } = new();
        internal Task? Work { get; set; }
        internal MetricWindow[] Windows() { lock (gate) return windows.ToArray(); }
        public ValueTask WriteAsync(MetricWindow window, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (windows.Count == 133) throw new InvalidOperationException("Window capacity reached.");
                totals = totals.Append(window); windows.Add(window);
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask FlushAsync(MetricWindowManifest? manifest, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        internal void Finish(LocalRunResult value) { lock (gate) { result = value; totals = value.Totals; Finished = DateTimeOffset.UtcNow; clock.Stop(); } }
        internal void Persisted(bool saved) { lock (gate) persistence = saved ? "saved" : "failed"; }
        internal void Failed() { lock (gate) { failed = true; persistence = "failed"; Finished = DateTimeOffset.UtcNow; clock.Stop(); } }
        internal ExecutionView View()
        {
            lock (gate) return new(Id, Project, Started, Finished, failed ? "Failed" : result?.Completion.ToString() ?? "Running",
                persistence, result?.DataComplete ?? false, Views.Quality(result?.Quality ?? totals.Quality),
                failed ? "InternalFailure" : result?.Reason.ToString(), plan, Views.Metrics(planned, totals),
                windows.Select(Views.Window).ToArray(), result?.TotalDuration.TotalSeconds ?? clock.Elapsed.TotalSeconds);
        }
    }
}
