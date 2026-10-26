using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Lifecycle;
using Npgsql;
using NpgsqlTypes;

namespace Dinamico.Persistence;

/// <summary>One sealed local stream per execution. All writes and retention are atomic.</summary>
public sealed class PostgresRunStore(string connectionString)
{
    private const int MaxWindows = 133;
    private const string Migration = """
        CREATE SCHEMA IF NOT EXISTS dinamico_m2;
        CREATE TABLE dinamico_m2.executions (
          project_id uuid NOT NULL, id uuid NOT NULL, created_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
          started_utc timestamptz NOT NULL, finished_utc timestamptz NOT NULL,
          content_hash bytea NOT NULL, snapshot_hash bytea NOT NULL, schema_version integer NOT NULL CHECK(schema_version=1),
          snapshot jsonb, data_complete boolean NOT NULL, planned_total bigint NOT NULL CHECK(planned_total>=0),
          scheduled_due bigint NOT NULL CHECK(scheduled_due>=0), not_scheduled bigint NOT NULL CHECK(not_scheduled>=0),
          sealed boolean NOT NULL DEFAULT false, detail_retained boolean NOT NULL DEFAULT true,
          completion integer NOT NULL, reason integer NOT NULL, failures integer NOT NULL, quality integer NOT NULL,
          summary bytea, summary_retained boolean NOT NULL DEFAULT true,
          PRIMARY KEY(project_id,id), CHECK(finished_utc>=started_utc));
        CREATE INDEX executions_project_cursor ON dinamico_m2.executions(project_id,created_utc DESC,id DESC);
        CREATE INDEX executions_retention ON dinamico_m2.executions(finished_utc) WHERE sealed;
        CREATE TABLE dinamico_m2.windows (
          project_id uuid NOT NULL, execution_id uuid NOT NULL, window_index bigint NOT NULL CHECK(window_index>=0),
          payload bytea NOT NULL, PRIMARY KEY(project_id,execution_id,window_index),
          FOREIGN KEY(project_id,execution_id) REFERENCES dinamico_m2.executions(project_id,id));
        """;

    private const string DiagnosticsMigration = """
        ALTER TABLE dinamico_m2.executions ADD COLUMN diagnostics jsonb;
        ALTER TABLE dinamico_m2.executions ADD CONSTRAINT executions_diagnostics_size
          CHECK(diagnostics IS NULL OR octet_length(diagnostics::text) <= 2097152);
        """;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Open(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Execute(connection, "SELECT pg_advisory_xact_lock(729418203125)", cancellationToken);
        await Execute(connection, "CREATE TABLE IF NOT EXISTS public.dinamico_migrations(version integer PRIMARY KEY, checksum text NOT NULL)", cancellationToken);
        await using (var future = new NpgsqlCommand("SELECT count(*) FROM public.dinamico_migrations WHERE version NOT IN (1,2)", connection))
            if ((long)(await future.ExecuteScalarAsync(cancellationToken))! != 0)
                throw new InvalidDataException("Unknown migration version. Upgrade the application before migrating.");
        foreach (var (version, sql) in new[] { (1, Migration), (2, DiagnosticsMigration) })
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            await using var query = new NpgsqlCommand("SELECT checksum FROM public.dinamico_migrations WHERE version=$1", connection);
            query.Parameters.AddWithValue(version);
            var previous = await query.ExecuteScalarAsync(cancellationToken);
            if (previous is string checksum)
            {
                if (checksum != hash) throw new InvalidDataException($"Migration {version} checksum mismatch.");
            }
            else
            {
                await Execute(connection, sql, cancellationToken);
                await using var insert = new NpgsqlCommand("INSERT INTO public.dinamico_migrations VALUES($1,$2)", connection);
                Add(insert, version, hash);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ImportResult> ImportAsync(ExecutionImport input, CancellationToken cancellationToken = default)
    {
        var validated = Validate(input);
        await using var connection = await Open(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO dinamico_m2.executions(project_id,id,started_utc,finished_utc,content_hash,snapshot_hash,schema_version,
              snapshot,data_complete,planned_total,scheduled_due,not_scheduled,completion,reason,failures,quality,diagnostics)
            VALUES($1,$2,$3,$4,$5,$6,1,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16) ON CONFLICT DO NOTHING
            """, connection);
        Add(insert, input.ProjectId, input.ExecutionId, input.StartedUtc.ToUniversalTime(), input.FinishedUtc.ToUniversalTime(),
            validated.Hash, SHA256.HashData(Encoding.UTF8.GetBytes(validated.Snapshot)));
        insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, validated.Snapshot);
        Add(insert, input.DataComplete, checked((long)input.PlannedTotal), checked((long)input.ScheduledDue), checked((long)input.NotScheduledAfterStop), (int)input.Completion, (int)input.Reason, (int)input.Failures, (int)input.Quality);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)validated.Diagnostics ?? DBNull.Value });
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var existing = Scoped(connection, "SELECT content_hash FROM dinamico_m2.executions WHERE project_id=$1 AND id=$2", input.ProjectId, input.ExecutionId);
            var hash = (byte[])(await existing.ExecuteScalarAsync(cancellationToken))!;
            if (!CryptographicOperations.FixedTimeEquals(hash, validated.Hash)) throw new ImportConflictException();
            await transaction.CommitAsync(cancellationToken);
            return ImportResult.Replayed;
        }
        foreach (var (window, index) in validated.Windows.Select((value, index) => (value, index)))
        {
            await using var write = Scoped(connection, "INSERT INTO dinamico_m2.windows VALUES($1,$2,$3,$4)", input.ProjectId, input.ExecutionId);
            Add(write, (long)index, window);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var seal = Scoped(connection, "UPDATE dinamico_m2.executions SET summary=$3,sealed=true WHERE project_id=$1 AND id=$2", input.ProjectId, input.ExecutionId);
        seal.Parameters.AddWithValue(SummaryCodec.Encode(validated.Summary));
        await seal.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ImportResult.Inserted;
    }

    public async Task<StoredExecution?> GetAsync(Guid projectId, Guid executionId, CancellationToken cancellationToken = default)
    {
        Scope(projectId, executionId);
        await using var connection = await Open(cancellationToken);
        await using var command = Scoped(connection, "SELECT " + Columns + " FROM dinamico_m2.executions WHERE project_id=$1 AND id=$2 AND sealed AND summary_retained", projectId, executionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<ExecutionPage> ListAsync(Guid projectId, int limit = 50, ExecutionCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        Scope(projectId);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await Open(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT " + Columns + " FROM dinamico_m2.executions WHERE project_id=$1 AND sealed AND summary_retained"
            + (cursor is null ? "" : " AND (created_utc,id)<($3,$4)") + " ORDER BY created_utc DESC,id DESC LIMIT $2", connection);
        Add(command, projectId, limit + 1);
        if (cursor is not null) Add(command, cursor.CreatedUtc.ToUniversalTime(), cursor.ExecutionId);
        var items = new List<StoredExecution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        bool more = items.Count > limit;
        if (more) items.RemoveAt(limit);
        return new(items, more ? new(items[^1].CreatedUtc, items[^1].ExecutionId) : null);
    }

    public async Task<IReadOnlyList<MetricWindow>> WindowsAsync(Guid projectId, Guid executionId, ulong from = 0,
        ulong? to = null, int limit = MaxWindows, CancellationToken cancellationToken = default)
    {
        Scope(projectId, executionId);
        if (from > long.MaxValue || to > long.MaxValue || to < from || limit is < 1 or > MaxWindows) throw new ArgumentOutOfRangeException(nameof(from));
        await using var connection = await Open(cancellationToken);
        await using var command = Scoped(connection, "SELECT payload FROM dinamico_m2.windows w WHERE project_id=$1 AND execution_id=$2 AND window_index >= $3 AND window_index <= $4 AND EXISTS (SELECT 1 FROM dinamico_m2.executions e WHERE e.project_id=w.project_id AND e.id=w.execution_id AND e.sealed AND e.summary_retained) ORDER BY window_index LIMIT $5", projectId, executionId);
        Add(command, (long)from, (long)(to ?? long.MaxValue), limit);
        var windows = new List<MetricWindow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) windows.Add(MetricWindowCodec.Decode(reader.GetFieldValue<byte[]>(0)));
        return windows;
    }

    public async Task<PruneResult> PruneAsync(DateTimeOffset now, int batchSize = 50, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = await Open(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock a bounded set of sealed runs; concurrent cleaners skip runs already being cleaned.
        await using var select = new NpgsqlCommand("SELECT project_id,id,finished_utc FROM dinamico_m2.executions WHERE sealed AND ((detail_retained AND finished_utc <= $1) OR (summary_retained AND finished_utc <= $2) OR (NOT summary_retained AND NOT detail_retained AND finished_utc <= $3)) ORDER BY finished_utc LIMIT $4 FOR UPDATE SKIP LOCKED", connection);
        Add(select, now.ToUniversalTime().AddDays(-7), now.ToUniversalTime().AddDays(-30), now.ToUniversalTime().AddDays(-90), batchSize);
        var runs = new List<(Guid Project, Guid Id, DateTime End)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) runs.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetDateTime(2)));
        int details = 0, summaries = 0, identities = 0;
        foreach (var run in runs)
        {
            await using var delete = Scoped(connection, "DELETE FROM dinamico_m2.windows WHERE project_id=$1 AND execution_id=$2", run.Project, run.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await using var update = Scoped(connection, "UPDATE dinamico_m2.executions SET detail_retained=false,diagnostics=jsonb_set(diagnostics, '{Requests}', '[]'::jsonb) WHERE project_id=$1 AND id=$2 AND detail_retained", run.Project, run.Id);
            details += await update.ExecuteNonQueryAsync(cancellationToken);
            if (run.End <= now.UtcDateTime.AddDays(-30))
            {
                await using var summary = Scoped(connection, "UPDATE dinamico_m2.executions SET summary=NULL,snapshot=NULL,diagnostics=NULL,summary_retained=false WHERE project_id=$1 AND id=$2 AND sealed AND summary_retained", run.Project, run.Id);
                summaries += await summary.ExecuteNonQueryAsync(cancellationToken);
            }
            if (run.End <= now.UtcDateTime.AddDays(-90))
            {
                await using var identity = Scoped(connection, "DELETE FROM dinamico_m2.executions WHERE project_id=$1 AND id=$2 AND sealed AND NOT detail_retained AND NOT summary_retained", run.Project, run.Id);
                identities += await identity.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(details, summaries, identities);
    }

    public async Task<StorageMeasurement> MeasureAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await Open(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_total_relation_size('dinamico_m2.executions') + pg_total_relation_size('dinamico_m2.windows'), pg_wal_lsn_diff(pg_current_wal_insert_lsn(),'0/0')", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1));
    }

    private const string Columns = "project_id,id,created_utc,started_utc,finished_utc,snapshot,data_complete,planned_total,scheduled_due,not_scheduled,detail_retained,summary,completion,reason,failures,quality,schema_version,snapshot_hash,content_hash,diagnostics";
    private static StoredExecution Read(NpgsqlDataReader reader) => new StoredExecution(reader.GetGuid(0), reader.GetGuid(1), reader.GetDateTime(2),
        reader.GetDateTime(3), reader.GetDateTime(4), reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<LocalRunSnapshot>(reader.GetString(5)),
        reader.GetBoolean(6), (ulong)reader.GetInt64(7), (ulong)reader.GetInt64(8), (ulong)reader.GetInt64(9), reader.GetBoolean(10),
        reader.IsDBNull(11) ? null : SummaryCodec.Decode(reader.GetFieldValue<byte[]>(11)),
        (LocalRunCompletion)reader.GetInt32(12), (LocalRunReason)reader.GetInt32(13), (LocalRunFailure)reader.GetInt32(14), (MeasurementQuality)reader.GetInt32(15), reader.GetInt32(16),
        Convert.ToHexString(reader.GetFieldValue<byte[]>(17)), Convert.ToHexString(reader.GetFieldValue<byte[]>(18)))
        { Diagnostics = reader.IsDBNull(19) ? null : DiagnosticsCodec.Decode(reader.GetString(19), !reader.GetBoolean(10)) };
    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static void Scope(Guid project, Guid? execution = null)
    {
        if (project == Guid.Empty || execution == Guid.Empty) throw new ArgumentException("Nonempty project and execution UUIDs are required.");
    }
    private static NpgsqlCommand Scoped(NpgsqlConnection connection, string sql, Guid project, Guid execution)
    {
        var command = new NpgsqlCommand(sql, connection); Add(command, project, execution); return command;
    }
    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
    }
    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(ct);
    }

    private static (byte[] Hash, string Snapshot, List<byte[]> Windows, RunSummary Summary, string? Diagnostics) Validate(ExecutionImport input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Scope(input.ProjectId, input.ExecutionId);
        ArgumentNullException.ThrowIfNull(input.Snapshot);
        ArgumentNullException.ThrowIfNull(input.Windows);
        var snapshot = input.Snapshot;
        if (snapshot.RequestsPerSecond is < 1 or > LocalEngineLimits.MaxRequestsPerSecond
            || snapshot.DurationSeconds is < 1 or > LocalEngineLimits.MaxDurationSeconds
            || snapshot.MaxConcurrency is < 1 or > LocalEngineLimits.MaxConcurrency
            || !Enum.IsDefined(snapshot.Mode) || !Enum.IsDefined(snapshot.RetryPolicy) || !Enum.IsDefined(snapshot.SuccessRule)
            || snapshot.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS")
            || snapshot.RequestTimeoutTicks <= 0 || snapshot.RequestTimeoutTicks > LocalEngineLimits.MaxRequestTimeout.Ticks
            || snapshot.MaxResponseBytes is < 1 or > LocalEngineLimits.MaxResponseBytes
            || snapshot.TargetScheme is not ("http" or "https") || snapshot.TargetPort is < 1 or > 65535
            || ((uint)input.Quality & ~31U) != 0
            || !Enum.IsDefined(input.Completion) || !Enum.IsDefined(input.Reason) || ((int)input.Failures & ~31) != 0)
            throw new InvalidDataException("Invalid local run snapshot or outcome.");
        if (input.FinishedUtc < input.StartedUtc || input.Windows.Count > MaxWindows)
            throw new InvalidDataException("Invalid run timestamps or window limit.");
        Check(input.PlannedTotal, input.ScheduledDue, input.NotScheduledAfterStop);
        if (input.PlannedTotal != checked((ulong)snapshot.RequestsPerSecond * (ulong)snapshot.DurationSeconds)
            || input.ScheduledDue > input.PlannedTotal || input.NotScheduledAfterStop != input.PlannedTotal - input.ScheduledDue)
            throw new InvalidDataException("Invalid scheduling totals.");
        var totals = MetricWindowTotals.Empty;
        var windows = new List<byte[]>();
        foreach (var window in input.Windows)
        {
            byte[] encoded = MetricWindowCodec.Encode(window);
            // Reapply all wire invariants, even for windows created by a collector.
            MetricWindow decoded = MetricWindowCodec.Decode(encoded);
            ValidateWindow(decoded);
            totals = totals.Append(decoded);
            windows.Add(encoded);
        }
        var summary = RunSummary.From(totals) with { Manifest = input.Manifest };
        ValidateSummary(summary);
        if (totals.End > TimeSpan.FromSeconds(snapshot.DurationSeconds + 65)
            || totals[MetricCounter.ScheduledDue] > input.ScheduledDue)
            throw new InvalidDataException("Metrics exceed local run bounds.");
        if (input.Manifest is { } manifest)
        {
            Check(manifest.LastWindowIndex);
            if (manifest.LastWindowIndex >= MaxWindows || manifest.End < totals.End
                || manifest.End > TimeSpan.FromSeconds(snapshot.DurationSeconds + 65)
                || ((uint)manifest.Quality & ~31U) != 0)
                throw new InvalidDataException("Invalid metric manifest.");
        }
        if (input.DataComplete && (input.Manifest is not { } complete || !totals.HasCompleteCoverage(complete)
            || input.Windows.Count == 0 || totals.LastInFlight != 0
            || totals[MetricCounter.ScheduledDue] != input.ScheduledDue || complete.Quality != totals.Quality))
            throw new InvalidDataException("Complete import requires matching final manifest and drained metrics.");
        string? diagnosticsJson = input.Diagnostics is null ? null : DiagnosticsCodec.Encode(input.Diagnostics);
        if (input.Diagnostics is { } diagnostics && (
            diagnostics.CaptureMode == "Complete" && input.PlannedTotal > Dinamico.LoadEngine.Observability.RunDiagnostics.MaximumRequests
            || diagnostics.Requests.Any(r => (ulong)r.ArrivalIndex >= input.PlannedTotal)
            || input.DataComplete && (ulong)diagnostics.Observed != totals.TerminalDuration.Count))
            throw new InvalidDataException("Diagnostic coverage does not match the canonical run.");
        string snapshotJson = JsonSerializer.Serialize(snapshot);
        using var canonical = new MemoryStream();
        using (var writer = new BinaryWriter(canonical, Encoding.UTF8, true))
        {
            writer.Write(1); writer.Write(input.ProjectId.ToByteArray()); writer.Write(input.ExecutionId.ToByteArray());
            writer.Write(input.StartedUtc.UtcTicks); writer.Write(input.FinishedUtc.UtcTicks); writer.Write(snapshotJson);
            writer.Write(input.DataComplete); writer.Write(input.PlannedTotal); writer.Write(input.ScheduledDue); writer.Write(input.NotScheduledAfterStop);
            writer.Write((int)input.Completion); writer.Write((int)input.Reason); writer.Write((int)input.Failures); writer.Write((uint)input.Quality);
            writer.Write(input.Manifest is not null);
            if (input.Manifest is { } writtenManifest)
            {
                writer.Write(writtenManifest.LastWindowIndex); writer.Write(writtenManifest.End.Ticks); writer.Write(writtenManifest.Complete); writer.Write((uint)writtenManifest.Quality);
            }
            writer.Write(windows.Count);
            foreach (var encoded in windows) writer.Write(encoded);
            if (diagnosticsJson is not null) writer.Write(diagnosticsJson);
        }
        return (SHA256.HashData(canonical.ToArray()), snapshotJson, windows, summary, diagnosticsJson);
    }
    private static void ValidateWindow(MetricWindow value)
    {
        Check(value.Sequence, value.WindowIndex, value.InFlight, value.Registrations, value.RegistrationDelaySumNanoseconds,
            value.RegistrationDelayMaxNanoseconds, value.RetryAfterClamped, value.RetryAfterClockUncertain);
        Check(value.Counters); Check(value.StatusReceived); Check(value.RetryAfterKinds);
        ValidateHistogram(HistogramSummary.From(value.TerminalDuration)); ValidateHistogram(HistogramSummary.From(value.HttpCompletedDuration));
    }
    private static void ValidateSummary(RunSummary value)
    {
        Check(value.WindowCount, value.LastInFlight, value.Registrations, value.RegistrationDelaySumNanoseconds,
            value.RegistrationDelayMaxNanoseconds, value.RetryAfterClamped, value.RetryAfterClockUncertain);
        Check(value.Counters); Check(value.StatusReceived); Check(value.RetryAfterKinds);
        ValidateHistogram(value.TerminalDuration); ValidateHistogram(value.HttpCompletedDuration);
        Check(value.StatusReceived.Aggregate(0UL, (a,b) => checked(a+b)), value.RetryAfterKinds.Aggregate(0UL, (a,b) => checked(a+b)));
    }
    private static void ValidateHistogram(HistogramSummary value)
    {
        Check(value.Count, value.SumNanoseconds, value.OverflowCount);
        if (value.MinimumNanoseconds is { } min) Check(min);
        if (value.MaximumNanoseconds is { } max) Check(max);
        Check(value.Counts);
    }
    private static void Check(params ulong[] values) => Check((IEnumerable<ulong>)values);
    private static void Check(IEnumerable<ulong> values)
    {
        if (values.Any(value => value > long.MaxValue)) throw new InvalidDataException("Metric exceeds PostgreSQL Int64 bound.");
    }
}
