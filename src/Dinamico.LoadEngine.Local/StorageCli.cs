using System.Text.Json;
using System.Text.Json.Nodes;
using Dinamico.LoadEngine.Metrics;
using Dinamico.Persistence;

namespace Dinamico.LoadEngine.Local;

internal static class StorageCli
{
    public const string Usage = "storage migrate|import|list|show|windows|prune|measure [options]; connection: DINAMICO_DATABASE_CONNECTION";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["-h"]) { Console.Error.WriteLine(Usage); return 0; }
            if (args.Length == 0) return Error(2, "storage.arguments_invalid");
            var options = Parse(args[1..]);
            string[] allowed = args[0] switch
            {
                "migrate" or "measure" => [],
                "import" => ["project", "report"],
                "list" => ["project", "limit", "cursor"],
                "show" => ["project", "execution"],
                "windows" => ["project", "execution", "from", "to", "limit"],
                "prune" => ["batch"],
                _ => throw new ArgumentException()
            };
            if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException();
            Guid project = allowed.Contains("project") ? Id(options, "project") : default;
            Guid execution = allowed.Contains("execution") ? Id(options, "execution") : default;
            // Validate and read the report before opening a connection or mutating the database.
            ExecutionImport? import = null;
            if (args[0] == "import")
            {
                await using var file = File.OpenRead(Required(options, "report"));
                import = await LocalReportReader.ReadAsync(file, project);
            }
            int limit = Integer(options, "limit", args[0] == "windows" ? 133 : 50, 1, args[0] == "windows" ? 133 : 100);
            int batch = Integer(options, "batch", 50, 1, 100);
            ulong from = Unsigned(options, "from", 0);
            ulong? to = options.ContainsKey("to") ? Unsigned(options, "to", 0) : null;
            if (to < from) throw new ArgumentException();
            ExecutionCursor? cursor = options.TryGetValue("cursor", out string? token) ? DecodeCursor(token) : null;
            string? connection = Environment.GetEnvironmentVariable("DINAMICO_DATABASE_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection)) return Error(2, "storage.connection_missing");
            var store = new PostgresRunStore(connection);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = deadline.Token;
            switch (args[0])
            {
                case "migrate":
                    await store.MigrateAsync(ct);
                    await Print(new { migrated = true });
                    break;
                case "import":
                    var outcome = await store.ImportAsync(import!, ct);
                    await Print(new { import!.ProjectId, import.ExecutionId, outcome });
                    break;
                case "list":
                    var page = await store.ListAsync(project, limit, cursor, ct);
                    await Print(new { items = page.Items.Select(ExecutionView), nextCursor = page.NextCursor is { } next ? EncodeCursor(next) : null });
                    break;
                case "show":
                    var result = await store.GetAsync(project, execution, ct);
                    if (result is null) return Error(5, "storage.not_found");
                    await Print(ExecutionView(result));
                    break;
                case "windows":
                    if (await store.GetAsync(project, execution, ct) is null) return Error(5, "storage.not_found");
                    var windows = await store.WindowsAsync(project, execution, from, to, limit, ct);
                    await Print(windows.Select(w => new { w.WindowIndex, w.Start, w.End, w.IsFinal, w.Quality,
                        w.Counters, w.StatusReceived, w.RateLimitedPercent, encodedWindow = Convert.ToBase64String(MetricWindowCodec.Encode(w)) }));
                    break;
                case "prune":
                    await Print(await store.PruneAsync(DateTimeOffset.UtcNow, batch, ct));
                    break;
                case "measure":
                    await Print(await store.MeasureAsync(ct));
                    break;
            }
            return 0;
        }
        catch (ImportConflictException) { return Error(4, "storage.content_conflict"); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException
            or InvalidDataException or OverflowException or FileNotFoundException or DirectoryNotFoundException
            or UnauthorizedAccessException)
        {
            return Error(2, "storage.input_invalid");
        }
        catch (Exception) { return Error(3, "storage.operation_failed"); }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 == args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(args[i + 1]) || !result.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException();
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException();
    private static Guid Id(Dictionary<string, string> options, string name) =>
        Guid.TryParse(Required(options, name), out var id) && id != Guid.Empty ? id : throw new ArgumentException();
    private static int Integer(Dictionary<string, string> options, string name, int fallback, int min, int max) =>
        !options.TryGetValue(name, out var value) ? fallback : int.TryParse(value, out int number) && number >= min && number <= max
            ? number : throw new ArgumentException();
    private static ulong Unsigned(Dictionary<string, string> options, string name, ulong fallback) =>
        !options.TryGetValue(name, out var value) ? fallback : ulong.TryParse(value, out ulong number)
            ? number : throw new ArgumentException();
    private static string EncodeCursor(ExecutionCursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, LocalCli.JsonOptions));
    private static ExecutionCursor DecodeCursor(string token)
    {
        if (token.Length > 512) throw new ArgumentException();
        var cursor = JsonSerializer.Deserialize<ExecutionCursor>(Convert.FromBase64String(token), LocalCli.JsonOptions)
            ?? throw new ArgumentException();
        if (cursor.ExecutionId == Guid.Empty || cursor.CreatedUtc.Offset != TimeSpan.Zero) throw new ArgumentException();
        return cursor;
    }
    private static async Task Print<T>(T value) =>
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, LocalCli.JsonOptions));
    private static JsonNode ExecutionView(StoredExecution execution)
    {
        var node = JsonSerializer.SerializeToNode(execution, LocalCli.JsonOptions)!;
        if (execution.Summary is { } summary)
        {
            foreach (var (name, histogram) in new[] { ("terminalDuration", summary.TerminalDuration),
                ("httpCompletedDuration", summary.HttpCompletedDuration) })
            {
                var view = node["summary"]![name]!;
                view["schema"] = LogHistogramSchema.Name;
                view["unit"] = "nanoseconds";
                view["p50"] = JsonSerializer.SerializeToNode(histogram.Quantile(50), LocalCli.JsonOptions);
                view["p95"] = JsonSerializer.SerializeToNode(histogram.Quantile(95), LocalCli.JsonOptions);
                view["p99"] = JsonSerializer.SerializeToNode(histogram.Quantile(99), LocalCli.JsonOptions);
            }
            ulong statuses = summary.StatusReceived.Aggregate(0UL, (sum, count) => checked(sum + count));
            node["summary"]!["finalStatuses"] = statuses;
            node["summary"]!["rateLimitedPercent"] = statuses == 0 ? null : JsonValue.Create(100.0 * summary.StatusReceived[329] / statuses);
        }
        return node;
    }
    private static int Error(int code, string name)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { @event = name, schemaVersion = 1 }, LocalCli.JsonOptions));
        return code;
    }
}
