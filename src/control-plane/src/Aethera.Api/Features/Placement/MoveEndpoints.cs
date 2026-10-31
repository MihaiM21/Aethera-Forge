using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Engine.Deployments;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Placement;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Placement;

/// <summary>
/// <c>POST /applications/{id}/move</c> and <c>POST /services/{id}/move</c> (ADR 0012): the workload runs on another server afterwards. A deployment of its
/// current configuration is queued on the target; the old server keeps serving until it runs, then its containers are removed. Volumes do not move.
/// </summary>
internal static class MoveEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        MapWorkload(api, "applications", "Application", isService: false);
        MapWorkload(api, "services", "Service", isService: true);
    }

    private static void MapWorkload(IEndpointRouteBuilder api, string prefix, string noun, bool isService)
    {
        api.MapPost($"/{prefix}/{{id:guid}}/move", (Guid id, MoveWorkloadRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, ServerCapacityReader capacities,
                DeploymentService deployments, CancellationToken ct) =>
                Move(id, isService, request, db, actor, audit, capacities, deployments, ct))
            .WithName($"move{noun}").WithTags(isService ? "Services" : "Applications")
            .Validate<MoveWorkloadRequest>().RequireRole(AetheraPolicies.Developer).RequireScope(Scopes.Deploy)
            .WithSummary($"Move the {noun.ToLowerInvariant()} to another server")
            .WithDescription(
                "Queues a deployment of the current configuration on the target server (202 with the deployment and its job). The workload belongs to the target " +
                "from the moment the deployment starts, the old server keeps serving until the new deployment runs, and then its containers and images are removed. " +
                "If the deployment fails, the workload stays on (or goes back to) the old server. Named volumes and host paths stay behind: a workload with " +
                "volumes needs confirm: true, and the response lists them (back them up and restore them on the new server). A workload that never ran just changes server (200).")
            .Produces<MoveWorkloadResponse>(StatusCodes.Status200OK).Produces<MoveWorkloadResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<Results<Ok<MoveWorkloadResponse>, Accepted<MoveWorkloadResponse>>> Move(
        Guid id, bool isService, MoveWorkloadRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, ServerCapacityReader capacities,
        DeploymentService deployments, CancellationToken ct)
    {
        var org = actor.Org();
        var kind = isService ? "service" : "application";
        var workload = await db.WorkloadsOf(org).Include(w => w.Volumes).Include(w => w.Domains)
                           .Where(w => isService ? w is Service : w is Application).FirstOrDefaultAsync(w => w.Id == id, ct)
                       ?? throw new ApiProblemException(ApiProblems.NotFound(kind, id));
        var target = await db.RequireServerAsync(org, request.ServerId, "/serverId", ct);
        var from = await db.GetServerAsync(org, workload.ServerId, tracking: false, ct);

        if (target.Id == workload.ServerId)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/serverId", "same_server", $"The {kind} is on {target.Name} already.")]));
        if (target.Lifecycle != ServerLifecycle.Active)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/serverId", "not_active",
                $"{target.Name} is {target.Lifecycle.ToString().ToLowerInvariant()}; only an active server can take a workload.")]));
        if (!ServerCapacityReader.IsConnected(target))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/serverId", "not_connected",
                target.Transport == ServerTransport.Ssh ? $"{target.Name} cannot be reached over SSH right now." : $"The agent on {target.Name} is not connected.")]));

        // One change at a time: a deployment in flight would race the switch of servers.
        if (await db.Deployments.AnyAsync(d => d.WorkloadId == id && (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.InProgress), ct))
            throw new ApiProblemException(ApiProblems.Conflict(PlacementProblemCodes.MoveInProgress, $"A deployment of this {kind} is still running; wait for it to finish before moving it."));

        var volumes = workload.Volumes.OrderBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => v.HostPath is { Length: > 0 } ? v.HostPath : VolumeNames.DockerName(workload.Slug, workload.Id, v.Name)).ToList();
        if (volumes.Count > 0 && request.Confirm != true)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status428PreconditionRequired, PlacementProblemCodes.MoveConfirmationRequired,
                $"This {kind} has {volumes.Count} volume{(volumes.Count == 1 ? "" : "s")} ({string.Join(", ", volumes)}). Volumes do not move: the data stays on {from.Name} and the {kind} starts without it on {target.Name}. " +
                "Back it up first (Backups) and restore it on the new server, then repeat the request with confirm: true.")
                .WithExtension("volumes", volumes));

        var warnings = new List<string>();
        if (PlacementRules.RoleWarning(target.Name, target.Roles, PlacementRules.KindOf(workload)) is { } roleWarning) warnings.Add(roleWarning);
        if ((await capacities.ReadAsync(org, null, target.Id, ct)).FirstOrDefault() is { } capacity)
        {
            if (PlacementRules.MemoryOf(workload.Runtime) is { } memory && capacity.MemoryFree is { } free && free < memory)
                warnings.Add($"{target.Name} has {PlacementRules.Bytes(Math.Max(0, free))} of memory unreserved and this {kind} asks for {PlacementRules.Bytes(memory)}: the server will be over-committed.");
            if (PlacementRules.CpuOf(workload.Runtime) is { } cpu && capacity.CpuFree is { } cpuFree && cpuFree < cpu)
                warnings.Add($"{target.Name} has {Math.Max(0, cpuFree):0.##} CPU cores unreserved and this {kind} asks for {cpu:0.##}: the server will be over-committed.");
        }

        var dataNote = volumes.Count > 0 ? $" Its volumes ({string.Join(", ", volumes)}) stay on {from.Name}; restore a backup on {target.Name} to bring the data over." : "";

        // Never deployed: nothing runs anywhere, so there is nothing to redeploy or clean up.
        if (workload.CurrentDeploymentId is null && !await deployments.HasDeploymentsAsync(id, ct))
        {
            WorkloadMoves.RehomeDomains(workload.Domains, from, target);
            workload.ServerId = target.Id;
            await audit.RecordAsync($"{kind}.moved", kind, id, new { slug = workload.Slug, fromServerId = from.Id, toServerId = target.Id, volumes, deployed = false }, ct);
            return TypedResults.Ok(new MoveWorkloadResponse(id, from.Id, target.Id, null, null, volumes.Count > 0, volumes, warnings,
                $"The {kind} was never deployed, so it now belongs to {target.Name}; the next deployment runs there.{dataNote}"));
        }

        QueuedDeployment queued;
        try
        {
            queued = await deployments.MoveAsync(id, target.Id, ct);
        }
        catch (DeploymentConflictException ex)
        {
            throw new ApiProblemException(ApiProblems.Conflict(ex.Code, ex.Message));
        }

        await audit.RecordAsync($"{kind}.move_requested", kind, id,
            new { slug = workload.Slug, fromServerId = from.Id, toServerId = target.Id, deploymentId = queued.Deployment.Id, jobId = queued.JobId, volumes, warnings = warnings.Count }, ct);
        var message = $"Deploying on {target.Name}. {from.Name} keeps serving until the new deployment runs; then its containers are removed.{dataNote}";
        return TypedResults.Accepted($"/api/v1/deployments/{queued.Deployment.Id}",
            new MoveWorkloadResponse(id, from.Id, target.Id, queued.Deployment.Id, queued.JobId, volumes.Count > 0, volumes, warnings, message));
    }
}
