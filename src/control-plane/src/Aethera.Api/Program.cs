using Aethera.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAetheraPersistence(builder.Configuration);

var app = builder.Build();

// Opt-in: containers/dev set Aethera:Database:AutoMigrate=true; production upgrades are explicit (WP5.2).
await app.Services.MigrateAetheraDatabaseIfEnabledAsync(app.Configuration);

// Liveness: the process is up.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
   .WithName("GetHealth");

// Readiness: dependencies (PostgreSQL) are reachable.
app.MapGet("/ready", async (HttpContext http, ILogger<Program> logger, CancellationToken cancellationToken) =>
    {
        try
        {
            // Resolved inside the try block so a missing/invalid connection string reports "unavailable" instead of a 500.
            var db = http.RequestServices.GetRequiredService<AetheraDbContext>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            if (await db.Database.CanConnectAsync(timeout.Token))
                return Results.Ok(new ReadyResponse("ready", new ReadyChecks("ok")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Never echo the exception (it can contain connection details); log it instead.
            logger.LogWarning(ex, "Readiness check failed: database unavailable");
        }

        return Results.Json(
            new ReadyResponse("unavailable", new ReadyChecks("unavailable")),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    })
   .WithName("GetReady");

app.Run();

internal sealed record HealthResponse(string Status);

internal sealed record ReadyChecks(string Database);

internal sealed record ReadyResponse(string Status, ReadyChecks Checks);

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
