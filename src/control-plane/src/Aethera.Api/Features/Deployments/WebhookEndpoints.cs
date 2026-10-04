using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Engine.Git;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Deployments;

public sealed record WebhookSetupRequest(GitProvider Provider = GitProvider.GitHub, string? BranchFilter = null);

/// <summary>The webhook of an application. <see cref="Secret"/> is only present when it was just created or rotated.</summary>
public sealed record WebhookDto(Guid Id, GitProvider Provider, string Path, bool Enabled, string? BranchFilter, DateTimeOffset? LastDeliveryAt, string? Secret);

public sealed record WebhookDeliveryDto(
    string DeliveryId, DateTimeOffset ReceivedAt, string? EventType, string? Ref, string? CommitSha, WebhookOutcome Outcome, string? Detail, Guid? DeploymentId);

public sealed record WebhookResultDto(WebhookOutcome Outcome, string? Detail, Guid? DeploymentId);

internal static class WebhookEndpoints
{
    /// <summary>Largest webhook body read (providers send a few KiB; this bounds memory for unauthenticated requests).</summary>
    private const int MaxBodyBytes = 1 << 20;

    public static void MapManagement(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/applications/{id:guid}/webhook").WithTags("Webhooks");
        group.MapGet("/", Get).WithName("getWebhook").RequireRead();
        group.MapPut("/", Put).WithName("setupWebhook").RequireWrite();
        group.MapDelete("/", Delete).WithName("deleteWebhook").RequireWrite();
        group.MapGet("/deliveries", Deliveries).WithName("listWebhookDeliveries").RequireRead();
    }

    /// <summary>The public receiver. Anonymous by design: every request is authenticated by the provider's signature or token.</summary>
    public static void MapReceiver(IEndpointRouteBuilder app) =>
        app.MapPost("/webhooks/git/{endpointId:guid}", Receive).WithName("receiveGitWebhook").WithTags("Webhooks").AllowAnonymous()
            .ExcludeFromDescription().DisableAntiforgery();

    // ---- management -----------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<WebhookDto>> Get(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        await RequireAppAsync(db, actor, id, ct);
        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.WorkloadId == id, ct)
                       ?? throw new ApiProblemException(ApiProblems.NotFound("webhook", id));
        return TypedResults.Ok(ToDto(endpoint, null));
    }

    private static async Task<Ok<WebhookDto>> Put(
        Guid id, WebhookSetupRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, CancellationToken ct)
    {
        var org = actor.Org();
        var app = await db.ApplicationsOf(org).Include(a => a.GitSource).FirstOrDefaultAsync(a => a.Id == id, ct)
                  ?? throw new ApiProblemException(ApiProblems.NotFound("application", id));
        if (app.GitSource is null)
            throw new ApiProblemException(ApiProblems.Conflict("webhook.no_git_source", "Only applications built from a git repository can have a webhook."));
        if (request.BranchFilter is { Length: > 200 })
            throw new ApiProblemException(ApiProblems.InvalidParameter("branchFilter", "The branch filter is too long."));

        // Setting up again rotates the secret: the old one stops working at once.
        var secretValue = SecretVault.GeneratePassword(40);
        var endpoint = await db.WebhookEndpoints.Include(e => e.Secret).FirstOrDefaultAsync(e => e.WorkloadId == id, ct);
        if (endpoint is null)
        {
            var secret = vault.Create(org, $"webhook:{app.Slug}", "Git webhook secret", secretValue, workloadId: app.Id, purpose: SecretPurpose.GitCredential);
            endpoint = new WebhookEndpoint { WorkloadId = id, Provider = request.Provider, SecretId = secret.Id, BranchFilter = request.BranchFilter };
            db.WebhookEndpoints.Add(endpoint);
        }
        else
        {
            vault.AddVersion(endpoint.Secret, secretValue);
            endpoint.Provider = request.Provider;
            endpoint.BranchFilter = request.BranchFilter;
            endpoint.Enabled = true;
        }
        await audit.RecordAsync("application.webhook_configured", "application", id, new { provider = request.Provider, request.BranchFilter }, ct);
        return TypedResults.Ok(ToDto(endpoint, secretValue));
    }

    private static async Task<NoContent> Delete(Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        await RequireAppAsync(db, actor, id, ct);
        var endpoint = await db.WebhookEndpoints.Include(e => e.Secret).FirstOrDefaultAsync(e => e.WorkloadId == id, ct)
                       ?? throw new ApiProblemException(ApiProblems.NotFound("webhook", id));
        db.WebhookEndpoints.Remove(endpoint);
        endpoint.Secret.MarkDeleted(clock.UtcNow);
        await audit.RecordAsync("application.webhook_deleted", "application", id, new { }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<WebhookDeliveryDto>>> Deliveries(Guid id, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        await RequireAppAsync(db, actor, id, ct);
        var rows = await db.WebhookDeliveries.AsNoTracking().Where(d => d.Endpoint.WorkloadId == id)
            .OrderByDescending(d => d.ReceivedAt).Take(50).ToListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<WebhookDeliveryDto>>(rows.Select(d =>
            new WebhookDeliveryDto(d.DeliveryId, d.ReceivedAt, d.EventType, d.Ref, d.CommitSha, d.Outcome, d.Detail, d.DeploymentId)).ToList());
    }

    private static async Task RequireAppAsync(AetheraDbContext db, ICurrentActor actor, Guid id, CancellationToken ct)
    {
        if (!await db.ApplicationsOf(actor.Org()).AnyAsync(a => a.Id == id, ct))
            throw new ApiProblemException(ApiProblems.NotFound("application", id));
    }

    private static WebhookDto ToDto(WebhookEndpoint e, string? secret) =>
        new(e.Id, e.Provider, $"/webhooks/git/{e.Id}", e.Enabled, e.BranchFilter, e.LastDeliveryAt, secret);

    // ---- receiver -------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> Receive(
        Guid endpointId, HttpRequest request, AetheraDbContext db, SecretVault vault, DeploymentService deployments, IClock clock,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("Aethera.Webhooks");
        if (request.ContentLength is > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }
        var body = buffer.ToArray();

        // The same answer for "no such endpoint", "disabled" and "bad signature": the endpoint id is not a secret, the signature is.
        var endpoint = await db.WebhookEndpoints.Include(e => e.Secret).FirstOrDefaultAsync(e => e.Id == endpointId && e.Enabled, ct);
        if (endpoint is null) return Results.Unauthorized();

        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var provider = GitProviders.For(endpoint.Provider);
        var (_, secret) = await vault.RevealAsync(endpoint.Secret, null, ct);
        if (!provider.VerifySignature(headers, body, secret))
        {
            log.LogWarning("Webhook {EndpointId}: signature verification failed.", endpointId);
            return Results.Unauthorized();
        }

        var evt = provider.Parse(headers, body);
        if (evt is null) return Results.BadRequest(new WebhookResultDto(WebhookOutcome.Rejected, "The payload was not understood.", null));

        endpoint.LastDeliveryAt = clock.UtcNow;
        var delivery = new WebhookDelivery
        {
            EndpointId = endpoint.Id, DeliveryId = evt.DeliveryId.Length > 255 ? evt.DeliveryId[..255] : evt.DeliveryId, EventType = Trim(evt.EventType, 64),
            Ref = Trim(evt.Ref, 255), CommitSha = Trim(evt.CommitSha, 64), SignatureValid = true, ReceivedAt = clock.UtcNow, Outcome = WebhookOutcome.Accepted,
        };
        if (await db.WebhookDeliveries.AnyAsync(d => d.EndpointId == endpoint.Id && d.DeliveryId == delivery.DeliveryId, ct))
            return Results.Ok(new WebhookResultDto(WebhookOutcome.Duplicate, "This delivery was already processed.", null));

        var app = await db.Applications.Include(a => a.GitSource).FirstOrDefaultAsync(a => a.Id == endpoint.WorkloadId, ct);
        var (outcome, detail) = Decide(evt, endpoint, app);
        Deployment? deployment = null;
        Guid? jobId = null;
        if (outcome == WebhookOutcome.Accepted)
        {
            // The deployment row and job are created first; the delivery row then records which one the delivery caused.
            var queued = await deployments.DeployAsync(app!.Id, DeploymentTrigger.Webhook,
                new DeploymentRequestInfo(evt.Ref, evt.CommitSha, Trim(evt.CommitMessage, 500), Trim(evt.CommitAuthor, 200)), ct);
            deployment = queued.Deployment;
            jobId = queued.JobId;
        }

        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            EndpointId = delivery.EndpointId, DeliveryId = delivery.DeliveryId, EventType = delivery.EventType, Ref = delivery.Ref, CommitSha = delivery.CommitSha,
            SignatureValid = true, ReceivedAt = delivery.ReceivedAt, Outcome = outcome, Detail = detail, JobId = jobId, DeploymentId = deployment?.Id,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent redelivery of the same id won the unique index; treat this one as the duplicate it is.
            return Results.Ok(new WebhookResultDto(WebhookOutcome.Duplicate, "This delivery was already processed.", null));
        }
        return outcome == WebhookOutcome.Accepted
            ? Results.Accepted(null, new WebhookResultDto(outcome, detail, deployment?.Id))
            : Results.Ok(new WebhookResultDto(outcome, detail, null));
    }

    private static (WebhookOutcome, string?) Decide(WebhookEvent evt, WebhookEndpoint endpoint, Application? app)
    {
        if (!evt.IsPush) return (WebhookOutcome.Ignored, $"'{evt.EventType}' events do not trigger deployments.");
        if (app?.GitSource is not { } git) return (WebhookOutcome.Ignored, "The application has no git source.");
        if (evt.Branch is null) return (WebhookOutcome.Ignored, "Only branch pushes trigger deployments.");
        if (evt.BranchDeleted) return (WebhookOutcome.Ignored, "The branch was deleted.");
        if (!BranchMatcher.Matches(endpoint.BranchFilter, git.Branch, evt.Branch)) return (WebhookOutcome.Ignored, $"Branch '{evt.Branch}' does not match the filter.");
        if (!git.AutoDeploy) return (WebhookOutcome.Ignored, "Auto-deploy is turned off for this application.");
        if (git.CommitPin is not null) return (WebhookOutcome.Ignored, "The application is pinned to a commit.");
        return (WebhookOutcome.Accepted, null);
    }

    private static string? Trim(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
