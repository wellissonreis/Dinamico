using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.LoadEngine.Observability;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Local;

public static class LocalCli
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine("Usage: run --config FILE --output FILE|- [--overwrite]");
            return 0;
        }
        if (!TryParse(args, out var configPath, out var outputPath, out bool overwrite))
            return Error(2, "configuration.arguments_invalid");

        try
        {
            if (outputPath != "-" && (string.Equals(Path.GetFullPath(configPath!), Path.GetFullPath(outputPath!),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || (!overwrite && File.Exists(outputPath))))
                return Error(2, "configuration.output_conflict");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return Error(2, "configuration.path_invalid");
        }

        LoadTestPlan plan;
        try
        {
            await using var input = File.OpenRead(configPath!);
            // Bound the untrusted document before deserializing headers or payload.
            if (input.Length > 2 * 1024 * 1024) return Error(2, "configuration.too_large");
            using var document = await JsonDocument.ParseAsync(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Error(2, "configuration.invalid_json");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowed = typeof(LoadTestConfiguration).GetProperties().Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            allowed.Add("schemaVersion");
            int? version = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name)) return Error(2, "configuration.unknown_field");
                if (!names.Add(property.Name)) return Error(2, "configuration.duplicate_field");
                if (property.Name.Equals("headers", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var header in property.Value.EnumerateArray())
                    {
                        if (header.ValueKind != JsonValueKind.Object) return Error(2, "configuration.invalid_header");
                        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var field in header.EnumerateObject())
                            if ((!field.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
                                && !field.Name.Equals("value", StringComparison.OrdinalIgnoreCase)) || !headerNames.Add(field.Name))
                                return Error(2, "configuration.invalid_header_field");
                    }
                }
                if (property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
                    version = property.Value.GetInt32();
            }
            if (version != 2) return Error(2, "configuration.unsupported_schema");
            var configuration = document.RootElement.Deserialize<LoadTestConfiguration>(JsonOptions);
            var validation = LocalPlanValidator.Validate(configuration, Guid.NewGuid());
            if (!validation.IsValid)
            {
                return Error(2, "configuration.rejected", new
                {
                    errorCount = validation.Errors.Length,
                    errors = validation.Errors.Take(5).ToArray()
                });
            }
            plan = validation.Plan!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException or NotSupportedException)
        {
            return Error(2, "configuration.read_failed");
        }

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        var observability = new BoundedRunObservability(Console.Error, plan.ExecutionId);
        observability.Start(plan.RateLimitMode);
        JsonReportOutput? output = null;
        try
        {
            var creating = Task.Run(() => JsonReportOutput.CreateAsync(outputPath!, overwrite, plan, observability));
            try { output = await creating.WaitAsync(LocalRunController.FlushTimeout); }
            catch (TimeoutException)
            {
                ObserveFault(creating);
                return Error(3, "report.create_timed_out");
            }
            var result = await new LocalRunController().RunAsync(plan, output, stop.Token);
            observability.Finish(result);
            await observability.CompleteAsync(TimeSpan.FromSeconds(1));
            if (!result.OutputStopped)
            {
                output = null; // Output still owns I/O; do not race its writer during shutdown.
                return Error(3, "report.output_not_stopped");
            }
            using var reportDeadline = new CancellationTokenSource(LocalRunController.FlushTimeout);
            var publishingOutput = output;
            var publish = Task.Run(() => publishingOutput.CompleteAsync(result, reportDeadline.Token));
            try { await publish.WaitAsync(LocalRunController.FlushTimeout + TimeSpan.FromSeconds(1)); }
            catch (TimeoutException)
            {
                output = null; // A blocked stream can outlive cancellation; never dispose it concurrently.
                ObserveFault(publish);
                return Error(3, "report.publish_timed_out");
            }
            if (result.Completion == LocalRunCompletion.Cancelled) return 130;
            return result.Completion == LocalRunCompletion.Completed && result.DataComplete ? 0 : 3;
        }
        catch (Exception)
        {
            return Error(3, "run.or_report.failed");
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (output is not null)
            {
                try { await output.DisposeAsync(); } catch (Exception) { }
            }
        }
    }

    private static bool TryParse(string[] args, out string? config, out string? output, out bool overwrite)
    {
        config = output = null;
        overwrite = false;
        if (args.Length == 0 || args[0] != "run") return false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when config is null && i + 1 < args.Length: config = args[++i]; break;
                case "--output" when output is null && i + 1 < args.Length: output = args[++i]; break;
                case "--overwrite" when !overwrite: overwrite = true; break;
                default: return false;
            }
        }
        return !string.IsNullOrWhiteSpace(config) && !string.IsNullOrWhiteSpace(output);
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(static done => { _ = done.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static int Error(int code, string name, object? data = null)
    {
        try
        {
            string line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow, level = "error", @event = name,
                execution_id = (Guid?)null, schema_version = 1, reason_code = name,
                truncated = false, data
            }, JsonOptions);
            var logging = Task.Run(() => Console.Error.WriteLine(line));
            ObserveFault(logging);
            logging.Wait(TimeSpan.FromMilliseconds(100));
        }
        catch (Exception) { }
        return code;
    }
}
