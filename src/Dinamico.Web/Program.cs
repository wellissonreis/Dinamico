using System.Net;
using System.Text;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Validation;
using Dinamico.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

namespace Dinamico.Web;

public static class Program
{
    public static async Task Main(string[] args) => await LocalWebApplication.Build(args).RunAsync();
}

public static class LocalWebApplication
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static WebApplication Build(string[]? args = null, int port = 5080, string? connectionString = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args ?? [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = 2 * 1024 * 1024; });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
        builder.Services.AddSingleton(new DatabaseState(connectionString ?? Environment.GetEnvironmentVariable("DINAMICO_DATABASE_CONNECTION")));
        builder.Services.AddHostedService(p => p.GetRequiredService<DatabaseState>());
        builder.Services.AddSingleton<ExecutionService>();
        builder.Services.AddHostedService(p => p.GetRequiredService<ExecutionService>());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || !(host == "localhost" || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
            { await Error(403, "local_only", "Acesso permitido apenas na máquina local.").ExecuteAsync(context); return; }
            if (context.Request.Method is not ("GET" or "HEAD"))
            {
                var origin = context.Request.Headers.Origin.ToString();
                string[] allowed = ["http://127.0.0.1:5080", "http://localhost:5080", "http://127.0.0.1:5173", "http://localhost:5173"];
                if (context.Request.Headers["X-Dinamico-Client"] != "local-ui" || origin.Length != 0 && !allowed.Contains(origin, StringComparer.Ordinal))
                { await Error(403, "invalid_origin", "Cliente local ou origem inválidos.").ExecuteAsync(context); return; }
            }
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!context.Response.HasStarted) { context.Response.Clear(); await Error(503, "unavailable", "Serviço temporariamente indisponível.").ExecuteAsync(context); }
            }
        });
        app.MapGet("/api/health", async (DatabaseState db, CancellationToken token) =>
        {
            bool available = await db.ProbeAsync(token);
            return Results.Ok(new { status = available ? "ok" : "degraded", databaseAvailable = available });
        });
        app.MapGet("/api/projects/{projectId}/active", (string projectId, ExecutionService runs) =>
            Id(projectId, out var project) ? Results.Ok(new { execution = runs.Active(project) }) : InvalidId());
        app.MapPost("/api/projects/{projectId}/executions", async (string projectId, HttpContext context, DatabaseState db, ExecutionService runs) =>
        {
            if (!Id(projectId, out var project)) return InvalidId();
            StartRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<StartRequest>(context.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException) { return Error(400, "invalid_request", "JSON inválido ou corpo excede 2 MiB."); }
            if (request is null || !double.IsFinite(request.RequestTimeoutSeconds) || request.RequestTimeoutSeconds is <= 0 or > 60
                || request.RateLimitMode is not ("Normal" or "Diagnostic" or "Authorized"))
                return Error(400, "invalid_request", "Configuração inválida.");
            var mode = Enum.Parse<RateLimitMode>(request.RateLimitMode);
            if (request.CaptureMode is not ("Off" or "Sampled" or "Complete"))
                return Error(400, "invalid_capture", "Modo de captura inválido.");
            var validation = LocalPlanValidator.Validate(new()
            {
                Url = request.Url, Method = request.Method, RequestsPerSecond = request.RequestsPerSecond,
                DurationSeconds = request.DurationSeconds, MaxConcurrency = request.MaxConcurrency,
                RequestTimeout = TimeSpan.FromSeconds(request.RequestTimeoutSeconds), RateLimitMode = mode,
                Headers = request.Headers, Payload = Encoding.UTF8.GetBytes(request.Payload ?? ""),
                TestCredentialHeaderIndex = request.TestCredentialHeaderIndex, TestAuthorizationDeclared = request.TestAuthorizationDeclared,
                CaptureMode = Enum.Parse<RequestCaptureMode>(request.CaptureMode), CorrelateRequests = request.CorrelateRequests
            }, Guid.NewGuid());
            if (!validation.IsValid) return Results.Json(new { code = "invalid_plan", message = "Plano inválido.", errors = validation.Errors.Select(e => new { code = e.Code.ToString(), field = e.Field.ToString(), e.HeaderIndex }) }, statusCode: 400);
            if (!db.Available) return Error(503, "database_unavailable", "Banco de dados indisponível.");
            var view = runs.Start(project, validation.Plan!);
            return view is null ? Error(409, "execution_busy", "Já existe uma execução ativa.") : Results.Json(view, statusCode: 202);
        });
        app.MapGet("/api/projects/{projectId}/executions", async (string projectId, HttpContext context, DatabaseState db) =>
        {
            if (!Id(projectId, out var project)) return InvalidId();
            int limit = 20;
            if (context.Request.Query.TryGetValue("limit", out var raw) && (!int.TryParse(raw, out limit) || limit is < 1 or > 100)) return Error(400, "invalid_page", "Limite inválido.");
            ExecutionCursor? cursor = null;
            if (context.Request.Query.TryGetValue("cursor", out var encoded))
            {
                try { cursor = JsonSerializer.Deserialize<ExecutionCursor>(Convert.FromBase64String(encoded.ToString()), Json); }
                catch (Exception ex) when (ex is JsonException or FormatException) { return Error(400, "invalid_cursor", "Cursor inválido."); }
                if (cursor is null || cursor.ExecutionId == Guid.Empty || cursor.CreatedUtc == default) return Error(400, "invalid_cursor", "Cursor inválido.");
            }
            if (db.Store is null) return Error(503, "database_unavailable", "Banco de dados indisponível.");
            var page = await db.Store.ListAsync(project, limit, cursor, context.RequestAborted);
            var items = new List<ExecutionView>(page.Items.Count);
            foreach (var stored in page.Items)
                items.Add(Views.Stored(stored, await db.Store.WindowsAsync(project, stored.ExecutionId, limit: 133, cancellationToken: context.RequestAborted)));
            return Results.Ok(new { items, nextCursor = page.NextCursor is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(page.NextCursor, Json)) });
        });
        app.MapGet("/api/projects/{projectId}/executions/{executionId}", async (string projectId, string executionId, ExecutionService runs, DatabaseState db, CancellationToken token) =>
        {
            if (!Id(projectId, out var project) || !Id(executionId, out var id)) return InvalidId();
            var view = await Find(project, id, runs, db, token);
            return view is null ? Error(404, "not_found", "Execução não encontrada.") : Results.Ok(view);
        });
        app.MapPost("/api/projects/{projectId}/executions/{executionId}/cancel", async (string projectId, string executionId, ExecutionService runs, DatabaseState db, CancellationToken token) =>
        {
            if (!Id(projectId, out var project) || !Id(executionId, out var id)) return InvalidId();
            var view = runs.Cancel(project, id) ?? await Find(project, id, runs, db, token);
            return view is null ? Error(404, "not_found", "Execução não encontrada.") : Results.Json(view, statusCode: 202);
        });
        app.MapGet("/api/projects/{projectId}/executions/{executionId}/events", async (string projectId, string executionId, HttpContext context, ExecutionService runs, DatabaseState db) =>
        {
            if (!Id(projectId, out var project) || !Id(executionId, out var id)) { await InvalidId().ExecuteAsync(context); return; }
            var view = await Find(project, id, runs, db, context.RequestAborted);
            if (view is null) { await Error(404, "not_found", "Execução não encontrada.").ExecuteAsync(context); return; }
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            while (view is not null)
            {
                await context.Response.WriteAsync("event: snapshot\ndata: " + JsonSerializer.Serialize(view, Json) + "\n\n: keepalive\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (view.State != "Running" && view.Persistence != "pending") break;
                await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
                view = runs.Find(project, id);
            }
        });
        app.Map("/api/{**path}", () => Error(404, "not_found", "Recurso não encontrado."));
        var dist = FindFrontend();
        if (dist is not null)
        {
            var provider = new PhysicalFileProvider(dist);
            app.Lifetime.ApplicationStopped.Register(provider.Dispose);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapFallback(async context => { context.Response.ContentType = "text/html"; await context.Response.SendFileAsync(Path.Combine(dist, "index.html")); });
        }
        return app;
    }
    private static string? FindFrontend()
    {
        // dotnet run can use the project directory; direct DLL startup can use the repo root.
        foreach (string startingPoint in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(startingPoint); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "FrontEnd", "dist");
                if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
            }
        return null;
    }
    private static bool Id(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;
    private static IResult InvalidId() => Error(400, "invalid_id", "Identificador inválido.");
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);
    private static async Task<ExecutionView?> Find(Guid project, Guid id, ExecutionService runs, DatabaseState db, CancellationToken token)
    {
        var view = runs.Find(project, id);
        if (view is not null) return view;
        if (db.Store is null) throw new InvalidOperationException("Database unavailable.");
        var stored = await db.Store.GetAsync(project, id, token);
        return stored is null ? null : Views.Stored(stored, await db.Store.WindowsAsync(project, id, limit: 133, cancellationToken: token));
    }
}
