using Aethera.Api.Features.Audit;
using Aethera.Api;
using Aethera.Api.Features.Agents;
using Aethera.Api.Features.Auth;
using Aethera.Api.Features.Deployments;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.OpenApi;
using Aethera.Api.Web;
using Aethera.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAetheraPersistence(builder.Configuration);
builder.Services.AddAetheraApi(builder.Configuration); // WP1.0: JSON, errors, security, CORS, OpenAPI, validation, clock, audit
builder.Services.AddAuth(builder.Configuration);       // WP1.1
builder.Services.AddResources(builder.Configuration);  // WP1.2
builder.Services.AddJobs(builder.Configuration);       // WP1.3
builder.Services.AddAgentGateway(builder.Configuration); // WP2.2: internal CA, mTLS gRPC gateway, agent transport
builder.Services.AddDeployments(builder.Configuration);  // WP3.2: deployment engine, strategies, deploy/lifecycle jobs

var app = builder.Build();

// Opt-in: containers/dev set Aethera:Database:AutoMigrate=true; production upgrades are explicit (WP5.2).
// Not reached by the build-time OpenAPI generator, which stops the host after Build().
await app.Services.MigrateAetheraDatabaseIfEnabledAsync(app.Configuration);

app.UseAgentGateway();    // WP2.2: keeps the gRPC port and the API ports apart
app.UseAetheraApi();
app.MapOperationalEndpoints(); // /health, /ready
app.MapAetheraDocs();          // /api/openapi/{documentName}.json, /api/docs

var api = app.MapApiGroup();   // /api/v1, authenticated by default
api.MapAuth();                 // WP1.1
api.MapResources();            // WP1.2
api.MapJobs();                 // WP1.3
api.MapAgents();               // WP2.2
api.MapDeployments();          // WP3.2-3.4: deployments, rollback, build detection, proxy, webhook management
api.MapAuditLog();             // WP4.4: audit log viewer
api.MapContributors();         // test/extension seam
app.MapGitWebhooks();          // WP3.4: POST /webhooks/git/{endpointId} (anonymous, signature-verified)
app.MapJobsHubs();             // WP1.3: /hubs/* (SignalR) at the root
app.MapAgentGateway();         // WP2.2: AgentService + EnrollmentService (gRPC, mTLS listener only)
app.UseAetheraStaticWeb();     // ADR 0005: the Next.js export, for every path no endpoint claimed

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
