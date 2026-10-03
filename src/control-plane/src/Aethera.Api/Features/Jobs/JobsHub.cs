using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Jobs;

/// <summary>Server-to-client messages of <c>/hubs/jobs</c> (ADR 0003 section 6).</summary>
public interface IJobsClient
{
    /// <summary>The job changed. The payload is exactly what <c>GET /jobs/{id}</c> returns.</summary>
    Task JobUpdated(JobDto job);

    /// <summary>Transient progress reported by the handler (not persisted).</summary>
    Task JobProgress(Guid jobId, int percent, string? message);
}

/// <summary>Group names of <c>/hubs/jobs</c>. Resource and organization groups embed the organization, so a subscriber only ever joins groups of its own.</summary>
internal static class JobGroups
{
    public static string Job(Guid jobId) => $"job:{jobId:D}";

    public static string Resource(string organization, string type, Guid id) => $"resource:{organization}:{type.ToLowerInvariant()}:{id:D}";

    public static string Organization(string organization) => $"jobs:org:{organization}";

    public static string Org(Guid organizationId) => organizationId.ToString("D");
}

internal static class HubIdentity
{
    /// <summary>The organization of the connected principal; a hub error when the credential carries none.</summary>
    public static Guid OrganizationOf(ClaimsPrincipal? user) =>
        user is not null && AetheraPrincipal.ReadGuid(user, AetheraClaimTypes.OrganizationId) is { } id
            ? id
            : throw new HubException("auth.forbidden");
}

/// <summary>
/// Job state changes. Clients choose what to follow: one job (<c>Subscribe</c>), everything that happens to a resource
/// (<c>SubscribeResource</c>) or all jobs of their organization (<c>SubscribeAll</c>). Every subscription is authorized when it is made.
/// </summary>
public sealed class JobsHub(IServiceScopeFactory scopes) : Hub<IJobsClient>
{
    public async Task Subscribe(Guid jobId)
    {
        var organization = HubIdentity.OrganizationOf(Context.User);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var job = await db.Jobs.AsNoTracking().VisibleTo(organization).FirstOrDefaultAsync(j => j.Id == jobId, Context.ConnectionAborted);
        if (job is null) throw new HubException("job.not_found");

        await Groups.AddToGroupAsync(Context.ConnectionId, JobGroups.Job(jobId), Context.ConnectionAborted);
        // A snapshot, so the client does not have to race a REST call against the first event.
        await Clients.Caller.JobUpdated(await JobMapper.ToDtoWithPositionAsync(db, job, Context.ConnectionAborted));
    }

    public Task Unsubscribe(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroups.Job(jobId));

    public async Task SubscribeResource(string resourceType, Guid resourceId)
    {
        var organization = JobGroups.Org(HubIdentity.OrganizationOf(Context.User));
        ValidateResourceType(resourceType);
        await Groups.AddToGroupAsync(Context.ConnectionId, JobGroups.Resource(organization, resourceType, resourceId), Context.ConnectionAborted);
    }

    public async Task UnsubscribeResource(string resourceType, Guid resourceId)
    {
        var organization = JobGroups.Org(HubIdentity.OrganizationOf(Context.User));
        ValidateResourceType(resourceType);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroups.Resource(organization, resourceType, resourceId));
    }

    public async Task SubscribeAll()
    {
        var organization = JobGroups.Org(HubIdentity.OrganizationOf(Context.User));
        await Groups.AddToGroupAsync(Context.ConnectionId, JobGroups.Organization(organization), Context.ConnectionAborted);
    }

    public async Task UnsubscribeAll()
    {
        var organization = JobGroups.Org(HubIdentity.OrganizationOf(Context.User));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroups.Organization(organization));
    }

    private static void ValidateResourceType(string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType) || resourceType.Length > 64) throw new HubException("validation.invalid_parameter");
    }
}

/// <summary>
/// Listens for job events on the <see cref="ILiveBus"/> (Redis when configured, so events from any instance arrive) and pushes them to
/// the matching <c>/hubs/jobs</c> groups. A status change is re-read from Postgres and sent as the same <see cref="JobDto"/> REST returns.
/// </summary>
public sealed class JobEventRelay(
    ILiveBus bus,
    IHubContext<JobsHub, IJobsClient> hub,
    IServiceScopeFactory scopes,
    ILogger<JobEventRelay> logger) : IHostedService
{
    private readonly Channel<JobBusEvent> _events = Channel.CreateBounded<JobBusEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private IAsyncDisposable? _subscription;
    private Task _pump = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
        // Do not hold up start-up on Redis: connect and subscribe in the background, and keep trying while it is unreachable.
        _ = Task.Run(async () =>
        {
            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    _subscription = await bus.SubscribeAsync(LiveChannels.JobEvents, json =>
                    {
                        if (JobBusEvent.Parse(json) is { } message) _events.Writer.TryWrite(message);
                    }).WaitAsync(TimeSpan.FromSeconds(5), _stopping.Token);
                    return;
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Subscribing to job events failed; /hubs/jobs has no live updates until it succeeds");
                    try { await Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        _events.Writer.TryComplete();
        if (_subscription is not null) await _subscription.DisposeAsync();
        try { await _pump.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private async Task PumpAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var message in _events.Reader.ReadAllAsync(stopping))
            {
                try { await DeliverAsync(message, stopping); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Delivering the event of job {JobId} failed", message.JobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task DeliverAsync(JobBusEvent message, CancellationToken cancellationToken)
    {
        // Every job belongs to exactly one organization, so its events only ever reach groups of that organization.
        var organization = JobGroups.Org(message.OrganizationId);
        var groups = new List<string> { JobGroups.Job(message.JobId), JobGroups.Organization(organization) };
        if (message.ResourceType is not null && message.ResourceId is { } resourceId)
            groups.Add(JobGroups.Resource(organization, message.ResourceType, resourceId));

        if (message.Kind == "progress")
        {
            await hub.Clients.Groups(groups).JobProgress(message.JobId, message.Percent ?? 0, message.Message);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == message.JobId, cancellationToken);
        if (job is null) return;
        await hub.Clients.Groups(groups).JobUpdated(await JobMapper.ToDtoWithPositionAsync(db, job, cancellationToken));
    }
}
