using Aethera.Api;
using Aethera.Api.Features.Auth;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.OpenApi;
using Aethera.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAetheraPersistence(builder.Configuration);
builder.Services.AddAetheraApi(builder.Configuration); // WP1.0: JSON, errors, security, CORS, OpenAPI, validation, clock, audit
builder.Services.AddAuth(builder.Configuration);       // WP1.1
builder.Services.AddResources(builder.Configuration);  // WP1.2
builder.Services.AddJobs(builder.Configuration);       // WP1.3

var app = builder.Build();

// Opt-in: containers/dev set Aethera:Database:AutoMigrate=true; production upgrades are explicit (WP5.2).
// Not reached by the build-time OpenAPI generator, which stops the host after Build().
await app.Services.MigrateAetheraDatabaseIfEnabledAsync(app.Configuration);

app.UseAetheraApi();
app.MapOperationalEndpoints(); // /health, /ready
app.MapAetheraDocs();          // /api/openapi/{documentName}.json, /api/docs

var api = app.MapApiGroup();   // /api/v1, authenticated by default
api.MapAuth();                 // WP1.1
api.MapResources();            // WP1.2
api.MapJobs();                 // WP1.3
api.MapContributors();         // test/extension seam
app.MapJobsHubs();             // WP1.3: /hubs/* (SignalR) at the root

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
