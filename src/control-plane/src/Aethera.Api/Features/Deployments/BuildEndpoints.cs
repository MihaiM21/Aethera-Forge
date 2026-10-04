using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Engine.Proxy;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Deployments;

public sealed record BuildDetectRequest(string RepositoryUrl, string? Branch = null, string? ContextPath = null, Guid? GitCredentialId = null);

public sealed record BuildCandidateDto(
    string Engine, double Confidence, string Reason, string? DockerfilePath, string? InstallCommand, string? BuildCommand, string? StartCommand,
    string? OutputDirectory, string Language, IReadOnlyList<int> SuggestedPorts);

public sealed record BuildDetectResponse(IReadOnlyList<BuildCandidateDto> Candidates, string CommitSha);

public sealed record ProxyStatusDto(string Provider, string Version, bool Running, bool Changed);

internal static class BuildEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var servers = api.MapGroup("/servers/{id:guid}").WithTags("Builds");
        servers.MapPost("/build-detect", Detect).WithName("detectBuildMethod").RequireWrite()
            .Produces<BuildDetectResponse>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status502BadGateway);
        servers.MapPost("/proxy/ensure", EnsureProxy).WithName("ensureServerProxy").RequireAdmin(Scopes.ServersWrite).Produces<ProxyStatusDto>();
    }

    private static async Task<Ok<BuildDetectResponse>> Detect(
        Guid id, BuildDetectRequest request, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IServerTransportResolver resolver, CancellationToken ct)
    {
        var org = actor.Org();
        await db.GetServerAsync(org, id, tracking: false, ct);
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl) || request.RepositoryUrl.StartsWith('-') || request.RepositoryUrl.Length > 2000)
            throw new ApiProblemException(ApiProblems.InvalidParameter("repositoryUrl", "A repository URL is required."));

        GitCredentialSpec? credential = null;
        if (request.GitCredentialId is { } cid)
        {
            var c = await db.GitCredentials.AsNoTracking().Include(x => x.Secret).FirstOrDefaultAsync(x => x.Id == cid && x.OrganizationId == org, ct)
                    ?? throw new ApiProblemException(ApiProblems.NotFound("git_credential", cid));
            var (_, value) = await vault.RevealAsync(c.Secret, null, ct);
            credential = c.Kind == GitCredentialKind.DeployKey
                ? new GitCredentialSpec(c.Id.ToString(), SshPrivateKey: new SecretValue(value))
                : new GitCredentialSpec(c.Id.ToString(), c.Username, new SecretValue(value));
        }

        var command = new BuildDetectCommand(new GitSourceSpec(request.RepositoryUrl, request.Branch, Depth: 1, Credentials: credential), request.ContextPath);
        ResolvedOutcome<BuildDetection> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<BuildDetectCommand, BuildDetection>(
                id, command, CommandOptions.For($"detect:{Guid.NewGuid():N}"), ct);
        }
        catch (ServerTransportException ex)
        {
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status502BadGateway, ex.Code, ex.Message));
        }
        if (!resolved.Outcome.Succeeded)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status502BadGateway, "build.detect_failed", resolved.Outcome.ErrorMessage ?? "The repository could not be inspected."));

        var result = resolved.Outcome.EnsureSucceeded();
        return TypedResults.Ok(new BuildDetectResponse(
            result.Candidates.Select(c => new BuildCandidateDto(
                c.EngineName is { Length: > 0 } ? c.EngineName : c.Engine.ToString().ToLowerInvariant(), c.Confidence, c.Reason, Blank(c.DockerfilePath),
                Blank(c.InstallCommand), Blank(c.BuildCommand), Blank(c.StartCommand), Blank(c.OutputDir), c.Language, c.SuggestedPorts)).ToList(),
            result.CommitSha));
    }

    private static async Task<Ok<ProxyStatusDto>> EnsureProxy(
        Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IProxyProvider proxy, IOptions<ProxyOptions> options,
        IServerTransportResolver resolver, CancellationToken ct)
    {
        await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        var o = options.Value;
        var command = proxy.BuildEnsureCommand(new ProxySettings(o.AcmeEmail, o.Network, o.Version, o.Staging));
        ResolvedOutcome<ProxyEnsured> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<ProxyEnsureCommand, ProxyEnsured>(id, command, CommandOptions.For($"proxy:{Guid.NewGuid():N}"), ct);
        }
        catch (ServerTransportException ex)
        {
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status502BadGateway, ex.Code, ex.Message));
        }
        if (!resolved.Outcome.Succeeded)
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status502BadGateway, "proxy.ensure_failed", resolved.Outcome.ErrorMessage ?? "The proxy could not be started."));
        var result = resolved.Outcome.EnsureSucceeded();
        await audit.RecordAsync("server.proxy_ensured", "server", id, new { result.Provider, result.Version, result.Changed }, ct);
        return TypedResults.Ok(new ProxyStatusDto(result.Provider, result.Version, result.Running, result.Changed));
    }

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
