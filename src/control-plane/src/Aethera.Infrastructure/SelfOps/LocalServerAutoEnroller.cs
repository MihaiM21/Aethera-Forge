using Aethera.Domain;
using Aethera.Infrastructure.Agents;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>
/// Out of the box the machine the control plane runs on is a deployment server, like Coolify's localhost: the agent container of the
/// compose project waits unenrolled, and as soon as the first organization exists (the owner has registered) this makes the
/// <c>local</c> server and hands the agent its join token. It acts once per installation: a request already in the audit log, or any
/// server in the organization, means somebody has taken the decision (deleting the server stays deleted).
/// Off with <c>Aethera:Agents:AutoLocalServer=false</c>; "Use this machine" on the Servers page remains.
/// </summary>
public sealed class LocalServerAutoEnroller(IServiceScopeFactory scopes, IOptions<AgentGatewayOptions> options, ILogger<LocalServerAutoEnroller> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoLocalServer || !options.Value.Enabled || AetheraHost.IsOpenApiGeneration) return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (await RunOnceAsync(stoppingToken)) return; // done for this installation
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Enrolling this machine's agent failed; trying again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass. True when there is nothing left to do (a request was made, or the decision was taken before).</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(cancellationToken);
        if (org is null) return false; // nobody has registered yet
        if (await db.AuditEvents.AnyAsync(e => e.OrganizationId == org && e.Action == LocalServerEnabler.RequestedAction, cancellationToken)) return true;
        if (await db.Servers.AnyAsync(s => s.OrganizationId == org, cancellationToken)) return true;

        var result = await scope.ServiceProvider.GetRequiredService<LocalServerEnabler>().EnableAsync(org.Value, userId: null, automatic: true, cancellationToken);
        switch (result)
        {
            case LocalServerEnableResult.Requested:
                logger.LogInformation("This machine is being connected as the server '{Name}'", LocalServerEnabler.ServerName);
                return true;
            case LocalServerEnableResult.NotWritable:
                logger.LogWarning("Could not hand the join token to the agent container: the shared folder is not writable");
                return false;
            default:
                return false; // no agent container waiting yet (or a request pending): look again
        }
    }
}
