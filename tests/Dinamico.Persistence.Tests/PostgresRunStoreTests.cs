using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Observability;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.Persistence;
using Npgsql;

namespace Dinamico.Persistence.Tests;

// Integration category deliberately fails with an actionable error when no real database is configured.
[Trait("Category", "PostgreSQL")]
public sealed class PostgresRunStoreTests
{
    private static string Connection => Environment.GetEnvironmentVariable("DINAMICO_TEST_DATABASE")
        ?? throw new InvalidOperationException("Set DINAMICO_TEST_DATABASE to an exclusive real PostgreSQL test database.");
    private static PostgresRunStore Store() => new(Connection);

    [Fact]
    public async Task ConcurrentReplayConflictScopeAndReopen()
    {
        var store = Store();
        await Task.WhenAll(store.MigrateAsync(), Store().MigrateAsync());
        var input = Input();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store().ImportAsync(input)));
        Assert.Single(results, value => value == ImportResult.Inserted);
        Assert.Equal(7, results.Count(value => value == ImportResult.Replayed));
        var summary = (await Store().GetAsync(input.ProjectId, input.ExecutionId))!.Summary!;
        Assert.Equal(1UL, summary.WindowCount);
        Assert.Equal(1UL, summary.TerminalDuration.Count);
        Assert.Equal(1000000UL, summary.TerminalDuration.SumNanoseconds);
        Assert.Equal(1UL, summary.HttpCompletedDuration.Count);
        Assert.NotNull(summary.TerminalDuration.Quantile(99));
        Assert.Equal(LocalRunReason.DurationElapsed, (await Store().GetAsync(input.ProjectId, input.ExecutionId))!.Reason);
        Assert.Null(await store.GetAsync(Guid.NewGuid(), input.ExecutionId));
        Assert.Empty(await store.WindowsAsync(Guid.NewGuid(), input.ExecutionId));
        Assert.Empty((await store.ListAsync(Guid.NewGuid())).Items);
        Assert.Single(await store.WindowsAsync(input.ProjectId, input.ExecutionId));
        Assert.Empty(await store.WindowsAsync(input.ProjectId, input.ExecutionId, 1));
        await Assert.ThrowsAsync<ImportConflictException>(() => store.ImportAsync(input with { FinishedUtc = input.FinishedUtc.AddSeconds(1) }));
        await Assert.ThrowsAsync<ImportConflictException>(() => store.ImportAsync(input with { Snapshot = input.Snapshot with { Method = "POST" } }));
        var stored = (await store.GetAsync(input.ProjectId, input.ExecutionId))!;
        Assert.Equal("GET", stored.Snapshot!.Method);
        Assert.Equal(1, stored.SnapshotSchemaVersion);
        Assert.Equal(64, stored.SnapshotHash.Length);
        Assert.Equal(input.Manifest, stored.Summary!.Manifest);
        // Same execution UUID in a separate logical project is independently scoped.
        await store.ImportAsync(input with { ProjectId = Guid.NewGuid() });
        Assert.True((await store.MeasureAsync()).RelationBytes > 0);
    }

    [Fact]
    public async Task InsertAndMergeAreRolledBackTogether()
    {
        var store = Store(); await store.MigrateAsync();
        var input = Input() with { Diagnostics = Diagnostics() };
        string function = "fail_" + Guid.NewGuid().ToString("N");
        await Sql($"""
            CREATE FUNCTION dinamico_m2.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN IF NEW.id = '{input.ExecutionId}'::uuid THEN RAISE EXCEPTION 'injected merge failure'; END IF; RETURN NEW; END $body$;
            CREATE TRIGGER {function} BEFORE UPDATE ON dinamico_m2.executions FOR EACH ROW EXECUTE FUNCTION dinamico_m2.{function}();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => store.ImportAsync(input));
            Assert.Null(await store.GetAsync(input.ProjectId, input.ExecutionId));
            Assert.Empty(await store.WindowsAsync(input.ProjectId, input.ExecutionId));
        }
        finally { await Sql($"DROP TRIGGER {function} ON dinamico_m2.executions; DROP FUNCTION dinamico_m2.{function}()"); }
        Assert.Equal(ImportResult.Inserted, await store.ImportAsync(input));
    }

    [Fact]
    public async Task RejectsGapsManifestMismatchAndOverflowButPersistsExplicitPartial()
    {
        var store = Store(); await store.MigrateAsync();
        var input = Input(twoWindows: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input with { Windows = input.Windows.Skip(1).ToArray(), DataComplete = false }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input with { Manifest = null }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input with { Manifest = input.Manifest!.Value with { LastWindowIndex = 100 } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input with { PlannedTotal = (ulong)long.MaxValue + 1 }));
        var clock = new Clock(); var sink = new MetricWindowSink(); var collector = new MetricWindowCollector(clock, 0, sink);
        collector.RecordNotStarted(NotStartedReason.Policy, (ulong)long.MaxValue + 1, 0);
        clock.Ticks = TimeSpan.FromSeconds(1).Ticks; collector.Close(); sink.TryRead(out var huge);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input with { Windows = [huge!], DataComplete = false, Manifest = null }));
        var partial = input with { ExecutionId = Guid.NewGuid(), Windows = input.Windows.Take(1).ToArray(), DataComplete = false,
            Completion = LocalRunCompletion.Failed, Reason = LocalRunReason.OutputFailure, Failures = LocalRunFailure.Output };
        await store.ImportAsync(partial);
        var loaded = await store.GetAsync(partial.ProjectId, partial.ExecutionId);
        Assert.False(loaded!.DataComplete); Assert.Equal(LocalRunFailure.Output, loaded.Failures);
        Assert.Equal(1UL, loaded.Summary!.WindowCount);
    }

    [Fact]
    public async Task RetentionUsesFinishedTimeAndSealingAndKeepsReplayIdentity()
    {
        var store = Store(); await store.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var input = Input() with { StartedUtc = now.AddDays(-10), FinishedUtc = now.AddDays(-8) };
        await store.ImportAsync(input);
        var active = Input() with { StartedUtc = now.AddDays(-100), FinishedUtc = now.AddDays(-99) };
        await store.ImportAsync(active);
        await Sql($"UPDATE dinamico_m2.executions SET sealed=false WHERE project_id='{active.ProjectId}' AND id='{active.ExecutionId}'");
        try
        {
            await store.PruneAsync(now, 200);
            Assert.Empty(await store.WindowsAsync(input.ProjectId, input.ExecutionId));
            Assert.NotNull((await store.GetAsync(input.ProjectId, input.ExecutionId))!.Summary);
            Assert.Equal(ImportResult.Replayed, await store.ImportAsync(input));
            await Assert.ThrowsAsync<ImportConflictException>(() => store.ImportAsync(input with { DataComplete = false }));
            Assert.Null(await store.GetAsync(active.ProjectId, active.ExecutionId));
            Assert.DoesNotContain((await store.ListAsync(active.ProjectId)).Items, item => item.ExecutionId == active.ExecutionId);
            Assert.Empty(await store.WindowsAsync(active.ProjectId, active.ExecutionId));
            await store.PruneAsync(now.AddDays(23), 200);
            Assert.Null(await store.GetAsync(input.ProjectId, input.ExecutionId));
            Assert.Equal(ImportResult.Replayed, await store.ImportAsync(input));
            // Only this project's old identity is advanced to test dedup expiry without a global future clock.
            var expired = Input() with { StartedUtc = now.AddDays(-92), FinishedUtc = now.AddDays(-91) };
            await store.ImportAsync(expired);
            Assert.True((await store.PruneAsync(now, 200)).IdentitiesRemoved > 0);
            Assert.Equal(ImportResult.Inserted, await store.ImportAsync(expired));
        }
        finally { await Sql($"UPDATE dinamico_m2.executions SET sealed=true WHERE project_id='{active.ProjectId}' AND id='{active.ExecutionId}'"); }
        Assert.Single(await store.WindowsAsync(active.ProjectId, active.ExecutionId));
    }

    [Fact]
    public async Task CursorPagesHaveNoOverlapAndRangeIsBounded()
    {
        var store = Store(); await store.MigrateAsync();
        var project = Guid.NewGuid();
        for (int i = 0; i < 3; i++) await store.ImportAsync(Input(twoWindows: true) with { ProjectId = project });
        var first = await store.ListAsync(project, 2);
        Assert.Equal(2, first.Items.Count); Assert.NotNull(first.NextCursor);
        var last = await store.ListAsync(project, 2, first.NextCursor);
        Assert.Single(last.Items); Assert.Null(last.NextCursor);
        Assert.DoesNotContain(last.Items[0].ExecutionId, first.Items.Select(item => item.ExecutionId));
        var window = await store.WindowsAsync(project, first.Items[0].ExecutionId, 1, 1, 1);
        Assert.Equal(1UL, Assert.Single(window).WindowIndex);
    }

    [Fact]
    public async Task CompleteManifestMayCloseAnAlreadyPublishedBoundaryWindow()
    {
        var store = Store(); await store.MigrateAsync();
        var clock = new Clock(); var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(clock, 0, sink);
        clock.Ticks = TimeSpan.FromSeconds(5).Ticks; collector.Advance();
        var manifest = collector.Close(); sink.TryRead(out var window);
        Assert.False(window!.IsFinal);
        var input = Input() with { Windows = [window], Manifest = manifest, ScheduledDue = 0, NotScheduledAfterStop = 1 };
        Assert.Equal(ImportResult.Inserted, await store.ImportAsync(input));
    }

    [Fact]
    public async Task MigrationChecksumIsEnforced()
    {
        var store = Store(); await store.MigrateAsync();
        foreach (int version in new[] { 1, 2 })
        {
            string checksum;
            await using (var connection = new NpgsqlConnection(Connection))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT checksum FROM public.dinamico_migrations WHERE version=$1", connection);
                command.Parameters.AddWithValue(version);
                checksum = (string)(await command.ExecuteScalarAsync())!;
            }
            await Sql($"UPDATE public.dinamico_migrations SET checksum='invalid' WHERE version={version}");
            try { await Assert.ThrowsAsync<InvalidDataException>(() => store.MigrateAsync()); }
            finally { await Sql($"UPDATE public.dinamico_migrations SET checksum='{checksum}' WHERE version={version}"); }
        }
        await Sql("INSERT INTO public.dinamico_migrations VALUES(3,'future')");
        try { await Assert.ThrowsAsync<InvalidDataException>(() => store.MigrateAsync()); }
        finally { await Sql("DELETE FROM public.dinamico_migrations WHERE version=3"); }
    }

    [Fact]
    public async Task DiagnosticsRoundTripReplayConflictAndRetention()
    {
        var store = Store(); await store.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var input = Input() with { Diagnostics = Diagnostics() };
        Assert.Equal(ImportResult.Inserted, await store.ImportAsync(input));
        var loaded = (await Store().GetAsync(input.ProjectId, input.ExecutionId))!.Diagnostics!;
        Assert.Equal(input.Diagnostics.Requests, loaded.Requests);
        Assert.Equal(input.Diagnostics.Resources, loaded.Resources);
        Assert.Equal(input.Diagnostics.Scheduler, loaded.Scheduler);
        Assert.NotNull(Assert.Single((await store.ListAsync(input.ProjectId)).Items).Diagnostics);
        Assert.Equal(ImportResult.Replayed, await store.ImportAsync(input));
        await Assert.ThrowsAsync<ImportConflictException>(() => store.ImportAsync(input with
            { Diagnostics = input.Diagnostics with { ExportState = "failed" } }));
        var legacy = Input(); await store.ImportAsync(legacy);
        Assert.Null((await store.GetAsync(legacy.ProjectId, legacy.ExecutionId))!.Diagnostics);
        await Assert.ThrowsAsync<ImportConflictException>(() => store.ImportAsync(legacy with { Diagnostics = Diagnostics() }));
        await store.PruneAsync(now.AddDays(8), 200);
        loaded = (await Store().GetAsync(input.ProjectId, input.ExecutionId))!.Diagnostics!;
        Assert.Empty(loaded.Requests); Assert.Single(loaded.Resources);
        Assert.Equal(1, loaded.Retained); Assert.NotNull(loaded.Scheduler);
        Assert.Equal(ImportResult.Replayed, await store.ImportAsync(input));
        await store.PruneAsync(now.AddDays(31), 200);
        Assert.Null(await store.GetAsync(input.ProjectId, input.ExecutionId));
        await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT diagnostics IS NULL FROM dinamico_m2.executions WHERE project_id=$1 AND id=$2", connection);
        command.Parameters.AddWithValue(input.ProjectId); command.Parameters.AddWithValue(input.ExecutionId);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task StoredDiagnosticsAreValidatedAndDatabaseEnforcesByteLimit()
    {
        var store = Store(); await store.MigrateAsync();
        var input = Input() with { Diagnostics = Diagnostics() };
        await store.ImportAsync(input);
        await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
        await using var corrupt = new NpgsqlCommand("UPDATE dinamico_m2.executions SET diagnostics=jsonb_set(diagnostics,'{SchemaVersion}','2') WHERE project_id=$1 AND id=$2", connection);
        corrupt.Parameters.AddWithValue(input.ProjectId); corrupt.Parameters.AddWithValue(input.ExecutionId);
        await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().GetAsync(input.ProjectId, input.ExecutionId));
        await using var oversized = new NpgsqlCommand("UPDATE dinamico_m2.executions SET diagnostics=jsonb_build_object('unsafe',repeat('x',2097152)) WHERE project_id=$1 AND id=$2", connection);
        oversized.Parameters.AddWithValue(input.ProjectId); oversized.Parameters.AddWithValue(input.ExecutionId);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => oversized.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
    }

    [Fact]
    public async Task RejectsUnsafeAndMalformedDiagnosticsBeforeWriting()
    {
        var store = new PostgresRunStore("Host=unused");
        var safe = Diagnostics();
        RunDiagnostics[] invalid = [
            safe with { SchemaVersion = 2 }, safe with { CaptureMode = "secret-canary" },
            safe with { ExportState = "secret-canary" }, safe with { Observed = 0 },
            safe with { Observed = 2, Discarded = 1, CaptureComplete = false },
            safe with { Requests = [safe.Requests[0] with { ArrivalIndex = 1 }] },
            safe with { Resources = new ResourceSample[134] }, safe with { Requests = new RequestSample[2001] },
            safe with { TraceUrlTemplate = "https://user:secret@host/trace/{traceId}" },
            safe with { TraceUrlTemplate = "https://host/trace/{traceId}?token=secret" },
            safe with { TraceUrlTemplate = "javascript:{traceId}" },
            safe with { Requests = [safe.Requests[0] with { ErrorKind = "secret-canary" }] },
            safe with { Requests = [safe.Requests[0] with { TraceId = "secret-canary" }] },
            safe with { Requests = [safe.Requests[0] with { DurationMilliseconds = double.NaN }] },
            safe with { Resources = [safe.Resources[0] with { WorkingSetBytes = -1 }] }
        ];
        foreach (var diagnostics in invalid)
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(Input() with { Diagnostics = diagnostics }));
    }

    private static RunDiagnostics Diagnostics() => new(1, "Complete", true, 1, 1, 0, true,
        [new(0, 1, 100, 50, 2.5, 200, 0, 0, 0, 0, 0, 1, null, null)],
        [new(0, 0, 1, .5, 200, "HttpCompleted", "None", new string('a', 32), new string('b', 16))],
        new(1, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)),
        "not-configured", "https://traces.example/trace/{traceId}");

    private static ExecutionImport Input(bool twoWindows = false)
    {
        var clock = new Clock(); var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(clock, 0, sink);
        collector.RecordStarted(0);
        collector.RecordHeaders(new(0, 200, default, 0));
        collector.RecordTerminal(new(0, RequestOutcome.HttpCompleted, TimeSpan.FromMilliseconds(1), 200, 10), 0);
        clock.Ticks = TimeSpan.FromSeconds(twoWindows ? 6 : 1).Ticks;
        var manifest = collector.Close();
        var windows = new List<MetricWindow>();
        while (sink.TryRead(out var window)) windows.Add(window!);
        return new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-6), DateTimeOffset.UtcNow,
            new(1, 1, 1), windows, true, 1, 1, 0, manifest);
    }
    private static async Task Sql(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    private sealed class Clock : IMonotonicClock
    {
        public long Ticks;
        public long GetTimestamp() => Ticks;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.FromTicks(endingTimestamp - startingTimestamp);
    }
}
