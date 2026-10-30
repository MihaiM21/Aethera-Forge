using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Infrastructure.Placement;

namespace Aethera.Api.Features.Placement;

/// <summary>Checks on the server an application builds on (ADR 0012), shared by create and update.</summary>
public static class BuildServerChecks
{
    /// <summary>
    /// 422 <c>not_active</c> unless the build server is active. Returns warnings: it has roles but not <see cref="ServerRole.Build"/> (accepted, the choice
    /// is yours), or it differs from the run server and has no build registry yet (building would fail until one is chosen).
    /// </summary>
    /// <exception cref="ApiProblemException">The build server is not active.</exception>
    public static IReadOnlyList<string> Check(Server buildServer, Guid runServerId, string pointer)
    {
        if (buildServer.Lifecycle != ServerLifecycle.Active)
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer(pointer, "not_active",
                $"{buildServer.Name} is {buildServer.Lifecycle.ToString().ToLowerInvariant()}; only an active server can build.")]));

        var warnings = new List<string>();
        if (PlacementRules.RoleWarning(buildServer.Name, buildServer.Roles, PlacementKind.Build) is { } roleWarning) warnings.Add(roleWarning);
        if (buildServer.Id != runServerId && buildServer.BuildRegistryId is null)
            warnings.Add($"{buildServer.Name} has no build registry, so the image cannot travel to the server that runs the application: deployments fail with build.registry_missing until you choose one in the server's settings.");
        return warnings;
    }
}
