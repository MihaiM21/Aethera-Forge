using Aethera.Api.Features.Agents;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Workloads;

public sealed record ComposeServiceResponse(string Name, string Image, bool HasBuild, IReadOnlyList<int> Ports);

/// <param name="CommitSha">The commit the file was read from (empty for an inline file).</param>
/// <param name="Violations">What the policy forbids in the file (privileged, host network, Docker socket, ...); the deployment would fail on these.</param>
public sealed record ComposeInspectResponse(IReadOnlyList<ComposeServiceResponse> Services, string CommitSha, IReadOnlyList<string> Violations);

/// <summary><c>POST /applications/{id}/compose/inspect</c> (ADR 0010): reads the application's compose file on its server, so the UI can offer a service picker for domains.</summary>
internal static class ComposeEndpoints
{
    public static void Map(RouteGroupBuilder applications)
    {
        applications.MapPost("/{id:guid}/compose/inspect", Inspect).WithName("inspectApplicationCompose").RequireWrite()
            .Produces<ComposeInspectResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
    }

    private static async Task<Ok<ComposeInspectResponse>> Inspect(
        Guid id, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IServerTransportResolver resolver, CancellationToken ct)
    {
        var org = actor.Org();
        var app = await db.ApplicationsOf(org).AsNoTracking().Include(a => a.GitSource).Include(a => a.ComposeSource).Include(a => a.EnvironmentVariables)
                      .AsSplitQuery().FirstOrDefaultAsync(a => a.Id == id, ct)
                  ?? throw new ApiProblemException(ApiProblems.NotFound("application", id));
        if (app.SourceKind != ApplicationSourceKind.Compose || app.ComposeSource is not { } source)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status422UnprocessableEntity, "application.not_compose", "This application is not a compose application."));

        // Variables the file interpolates: plain values as they are; a secret's value never leaves the control plane here (a placeholder keeps
        // `${VAR:?}` checks from failing).
        var env = app.EnvironmentVariables.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => EnvVarSpec.OfPlain(e.Key, e.SecretId is null ? e.Value ?? "" : "aethera-inspect-placeholder")).ToList();
        var name = $"{app.Slug}-{app.Id.ToString("N")[..8]}";

        ComposeProjectSpec project;
        if (source.FilePath is { Length: > 0 } filePath)
        {
            var git = app.GitSource ?? throw new ApiProblemException(new ApiProblem(StatusCodes.Status422UnprocessableEntity, "source.not_configured",
                "A compose file in the repository needs the application's git source."));
            GitCredentialSpec? credential = null;
            if (git.GitCredentialId is { } cid)
            {
                var c = await db.GitCredentials.AsNoTracking().Include(x => x.Secret).FirstOrDefaultAsync(x => x.Id == cid && x.OrganizationId == org, ct)
                        ?? throw new ApiProblemException(ApiProblems.NotFound("git_credential", cid));
                var (_, value) = await vault.RevealAsync(c.Secret, null, ct);
                credential = c.Kind == GitCredentialKind.DeployKey
                    ? new GitCredentialSpec(c.Id.ToString(), SshPrivateKey: new SecretValue(value))
                    : new GitCredentialSpec(c.Id.ToString(), c.Username, new SecretValue(value));
            }

            project = new ComposeProjectSpec(name, "")
            {
                Git = new GitSourceSpec(git.RepositoryUrl, git.Branch, git.CommitPin, Depth: git.CommitPin is null ? 1 : 0, Credentials: credential),
                ComposePath = filePath, Env = env,
            };
        }
        else
        {
            project = new ComposeProjectSpec(name, source.InlineContent ?? "") { Env = env };
        }

        ResolvedOutcome<ComposeInspection> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<ComposeInspectCommand, ComposeInspection>(
                app.ServerId, new ComposeInspectCommand(project), CommandOptions.For($"compose-inspect:{Guid.NewGuid():N}"), ct);
        }
        catch (ServerTransportException ex)
        {
            throw new ApiProblemException(ex.Code == TransportErrors.Unsupported
                ? new ApiProblem(StatusCodes.Status422UnprocessableEntity, TransportErrors.Unsupported,
                    "The agent on this server is too old to read a compose file: update the agent (Servers > the server > Update agent).")
                : AgentProblems.From(ex));
        }

        if (!resolved.Outcome.Succeeded)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status502BadGateway, "compose.inspect_failed",
                resolved.Outcome.ErrorMessage ?? "The compose file could not be read."));
        var result = resolved.Outcome.EnsureSucceeded();
        return TypedResults.Ok(new ComposeInspectResponse(
            result.Services.Select(s => new ComposeServiceResponse(s.Name, s.Image, s.HasBuild, s.Ports)).ToList(), result.CommitSha, result.Violations));
    }
}
