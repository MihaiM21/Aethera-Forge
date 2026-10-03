var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
   .WithName("GetHealth");

app.Run();

internal sealed record HealthResponse(string Status);

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
