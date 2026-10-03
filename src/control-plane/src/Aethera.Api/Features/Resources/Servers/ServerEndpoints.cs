using System.Net;
using System.Text.RegularExpressions;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Servers;

/// <summary>Capacity facts of the machine. Recorded by hand now; discovery (agent) overwrites them later.</summary>
public sealed record ServerResourcesRequest
{
    public int? CpuCores { get; init; }
    public long? MemoryBytes { get; init; }
    public long? DiskBytes { get; init; }
    public string? Os { get; init; }
    public string? Architecture { get; init; }
}

public sealed record CreateServerRequest
{
    public string? Name { get; init; }

    /// <summary>DNS name or IP address used to reach the server.</summary>
    public string? Host { get; init; }

    public int? SshPort { get; init; }
    public string? SshUser { get; init; }

    /// <summary><c>agent</c> (default) or <c>ssh</c>.</summary>
    public string? Transport { get; init; }

    /// <summary>Any of <c>master</c>, <c>build</c>, <c>storage</c>, <c>ci</c>, <c>worker</c>.</summary>
    public List<string>? Roles { get; init; }

    /// <summary>The address the world reaches the server at; domain DNS checks compare against it.</summary>
    public string? PublicIp { get; init; }

    public int? MaxConcurrentBuilds { get; init; }

    /// <summary>A secret holding the SSH private key or password.</summary>
    public Guid? SshCredentialSecretId { get; init; }

    public ServerResourcesRequest? Resources { get; init; }
}

public sealed record UpdateServerRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Host { get; init; }
    [NotClearable] public int? SshPort { get; init; }
    public string? SshUser { get; init; }
    [NotClearable] public string? Transport { get; init; }
    [NotClearable] public List<string>? Roles { get; init; }
    public string? PublicIp { get; init; }
    [NotClearable] public int? MaxConcurrentBuilds { get; init; }
    public Guid? SshCredentialSecretId { get; init; }

    /// <summary><c>pending</c>, <c>active</c>, <c>maintenance</c> or <c>disabled</c>.</summary>
    [NotClearable] public string? Lifecycle { get; init; }

    [NotClearable] public ServerResourcesRequest? Resources { get; init; }
}

public sealed record ReachabilityStatusResponse(ReachabilityStatus Status, DateTimeOffset? CheckedAt, DateTimeOffset? ChangedAt, int? ProbePort);

public sealed record AgentStatusResponse(AgentStatus Status, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? ChangedAt);

public sealed record DockerStatusResponse(DockerStatus Status, DateTimeOffset? ChangedAt);

/// <summary>The separate status axes of a server (spec section 44, ADR 0002); each is unknown until the first observation.</summary>
public sealed record ServerStatusResponse(ReachabilityStatusResponse Reachability, AgentStatusResponse Agent, DockerStatusResponse Docker);

public sealed record ServerResourcesResponse(int? CpuCores, long? MemoryBytes, long? DiskBytes, string? Os, string? Architecture);

public sealed record ServerResponse(
    Guid Id, string Name, string Host, int SshPort, string? SshUser, ServerTransport Transport, IReadOnlyList<ServerRole> Roles,
    ServerLifecycle Lifecycle, string? PublicIp, int MaxConcurrentBuilds, Guid? SshCredentialSecretId, ServerResourcesResponse Resources,
    ServerStatusResponse Status, string? AgentVersion, int WorkloadCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public static partial class ServerRules
{
    [GeneratedRegex(@"\A[a-z_][a-z0-9_-]{0,31}\$?\z")]
    private static partial Regex SshUserPattern();

    public static bool IsValidSshUser(string? user) => user is not null && SshUserPattern().IsMatch(user);

    /// <summary>A host is a hostname or an IP address, nothing else (no scheme, port or path).</summary>
    public static bool IsValidHost(string? host) =>
        host is not null && host.Length <= 253 && (IPAddress.TryParse(host, out _) || HostnameRules.TryNormalize(host, allowWildcard: false, out _, out _));

    public static bool IsValidIp(string? ip) => ip is not null && IPAddress.TryParse(ip, out _);
}

public sealed class CreateServerValidator : AbstractValidator<CreateServerRequest>
{
    public CreateServerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Host).NotEmpty();
        RuleFor(x => x.Host).Must(ServerRules.IsValidHost).WithErrorCode("pattern")
            .WithMessage("Must be a DNS name or an IP address, without scheme or port.").When(x => !string.IsNullOrEmpty(x.Host));
        RuleFor(x => x.SshPort).InclusiveBetween(1, 65535).When(x => x.SshPort is not null);
        RuleFor(x => x.SshUser).Must(ServerRules.IsValidSshUser).WithErrorCode("pattern").WithMessage("Must be a valid Unix user name.")
            .When(x => x.SshUser is not null);
        RuleFor(x => x.Transport).MustBeEnum<CreateServerRequest, ServerTransport>();
        RuleForEach(x => x.Roles).Must(r => EnumText.IsDefined<ServerRole>(r)).WithErrorCode("invalid_enum")
            .WithMessage($"Must be one of: {EnumText.Names<ServerRole>()}.");
        RuleFor(x => x.Roles).Must(r => r is null || r.Select(v => EnumText.Parse<ServerRole>(v)).Distinct().Count() == r.Count)
            .WithErrorCode("not_unique").WithMessage("Each role may appear once.");
        RuleFor(x => x.PublicIp).Must(ServerRules.IsValidIp).WithErrorCode("pattern").WithMessage("Must be an IPv4 or IPv6 address.")
            .When(x => x.PublicIp is not null);
        RuleFor(x => x.MaxConcurrentBuilds).InclusiveBetween(1, 64).When(x => x.MaxConcurrentBuilds is not null);
        RuleFor(x => x.Resources).SetValidator(new ServerResourcesValidator()!);
    }
}

public sealed class UpdateServerValidator : AbstractValidator<UpdateServerRequest>
{
    public UpdateServerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Host).NotEmpty().Must(ServerRules.IsValidHost).WithErrorCode("pattern")
            .WithMessage("Must be a DNS name or an IP address, without scheme or port.").When(x => x.Host is not null);
        RuleFor(x => x.SshPort).InclusiveBetween(1, 65535).When(x => x.SshPort is not null);
        RuleFor(x => x.SshUser).Must(ServerRules.IsValidSshUser).WithErrorCode("pattern").WithMessage("Must be a valid Unix user name.")
            .When(x => x.SshUser is not null);
        RuleFor(x => x.Transport).MustBeEnum<UpdateServerRequest, ServerTransport>();
        RuleFor(x => x.Lifecycle).MustBeEnum<UpdateServerRequest, ServerLifecycle>();
        RuleForEach(x => x.Roles).Must(r => EnumText.IsDefined<ServerRole>(r)).WithErrorCode("invalid_enum")
            .WithMessage($"Must be one of: {EnumText.Names<ServerRole>()}.");
        RuleFor(x => x.Roles).Must(r => r is null || r.Select(v => EnumText.Parse<ServerRole>(v)).Distinct().Count() == r.Count)
            .WithErrorCode("not_unique").WithMessage("Each role may appear once.");
        RuleFor(x => x.PublicIp).Must(ServerRules.IsValidIp).WithErrorCode("pattern").WithMessage("Must be an IPv4 or IPv6 address.")
            .When(x => x.PublicIp is not null);
        RuleFor(x => x.MaxConcurrentBuilds).InclusiveBetween(1, 64).When(x => x.MaxConcurrentBuilds is not null);
        RuleFor(x => x.Resources).SetValidator(new ServerResourcesValidator()!);
    }
}

public sealed class ServerResourcesValidator : AbstractValidator<ServerResourcesRequest>
{
    public ServerResourcesValidator()
    {
        RuleFor(x => x.CpuCores).InclusiveBetween(1, 4096).When(x => x.CpuCores is not null);
        RuleFor(x => x.MemoryBytes).GreaterThan(0).When(x => x.MemoryBytes is not null);
        RuleFor(x => x.DiskBytes).GreaterThan(0).When(x => x.DiskBytes is not null);
        RuleFor(x => x.Os).MaximumLength(100);
        RuleFor(x => x.Architecture).MaximumLength(32);
    }
}

internal static class ServerEndpoints
{
    private static readonly SortDefinition<Server> Sorts = new SortDefinition<Server>("name")
        .Add("name", s => s.Name).Add("host", s => s.Host).Add("createdAt", s => s.CreatedAt).Add("updatedAt", s => s.UpdatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/servers").WithTags("Servers");

        group.MapGet("/", List).WithName("listServers").RequireRead();
        group.MapPost("/", Create).WithName("createServer").Validate<CreateServerRequest>().RequireAdmin(Scopes.ServersWrite);
        group.MapGet("/{id:guid}", Get).WithName("getServer").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateServer")
            .Accepts<UpdateServerRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateServerRequest>().RequireAdmin(Scopes.ServersWrite);
        group.MapDelete("/{id:guid}", Delete).WithName("deleteServer").RequireAdmin(Scopes.ServersWrite);
    }

    private static async Task<Ok<Page<ServerResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        string? lifecycle, string? transport, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("lifecycle", "transport", "q");
        var lifecycles = ResourceHttp.ParseEnumFilter<ServerLifecycle>(lifecycle, "lifecycle");
        var transports = ResourceHttp.ParseEnumFilter<ServerTransport>(transport, "transport");
        var like = ResourceHttp.LikePattern(q);

        var query = db.ServersOf(actor.Org()).AsNoTracking();
        if (lifecycles is not null) query = query.Where(s => lifecycles.Contains(s.Lifecycle));
        if (transports is not null) query = query.Where(s => transports.Contains(s.Transport));
        if (like is not null) query = query.Where(s => EF.Functions.ILike(s.Name, like, "\\") || EF.Functions.ILike(s.Host, like, "\\"));

        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"lifecycle={lifecycle}&transport={transport}&q={q}", ct);
        var ids = items.Select(s => s.Id).ToList();
        var counts = await db.Workloads.Where(w => ids.Contains(w.ServerId)).GroupBy(w => w.ServerId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return TypedResults.Ok(new Page<ServerResponse>(items.Select(s => ToResponse(s, counts.GetValueOrDefault(s.Id))).ToList(), next));
    }

    private static async Task<Created<ServerResponse>> Create(
        CreateServerRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, org, name, null, ct);
        if (request.SshCredentialSecretId is { } secretId) await ManagedSecrets.ClaimForServerAsync(db, org, secretId, "/sshCredentialSecretId", ct);

        var server = new Server
        {
            OrganizationId = org, Name = name, Host = request.Host!.Trim().ToLowerInvariant(), SshPort = request.SshPort ?? 22, SshUser = request.SshUser,
            Transport = EnumText.Parse<ServerTransport>(request.Transport) ?? ServerTransport.Agent,
            Roles = (request.Roles ?? []).Select(r => EnumText.Parse<ServerRole>(r)!.Value).Distinct().ToList(),
            PublicIp = NormalizeIp(request.PublicIp), MaxConcurrentBuilds = request.MaxConcurrentBuilds ?? 1,
            SshCredentialSecretId = request.SshCredentialSecretId,
        };
        if (server.Transport == ServerTransport.Ssh) server.SetAgentStatus(AgentStatus.NotInstalled, clock.UtcNow);
        ApplyResources(server, request.Resources, _ => true);

        db.Servers.Add(server);
        await audit.RecordAsync("server.created", "server", server.Id,
            new { name, host = server.Host, transport = server.Transport, roles = server.Roles }, ct);
        http.SetETag(server.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("servers", server.Id), ToResponse(server, 0));
    }

    private static async Task<Ok<ServerResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: false, ct);
        http.SetETag(server.RowVersion);
        return TypedResults.Ok(ToResponse(server, await db.Workloads.CountAsync(w => w.ServerId == id, ct)));
    }

    private static async Task<Ok<ServerResponse>> Update(
        Guid id, PatchRequest<UpdateServerRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var server = await db.GetServerAsync(org, id, tracking: true, ct);
        http.CheckIfMatch(server.RowVersion);
        var body = patch.Body;

        var changed = new List<string>();
        if (patch.Has("name") && body.Name is { } name && name.Trim() != server.Name)
        {
            await EnsureNameFreeAsync(db, org, name.Trim(), id, ct);
            server.Name = name.Trim();
            changed.Add("name");
        }

        if (patch.Has("host") && body.Host is { } host) { server.Host = host.Trim().ToLowerInvariant(); changed.Add("host"); }
        if (patch.Has("sshPort") && body.SshPort is { } port) { server.SshPort = port; changed.Add("sshPort"); }
        if (patch.Has("sshUser")) { server.SshUser = body.SshUser; changed.Add("sshUser"); }
        if (patch.Has("transport") && EnumText.Parse<ServerTransport>(body.Transport) is { } transport)
        {
            server.Transport = transport;
            if (transport == ServerTransport.Ssh && server.AgentStatus is AgentStatus.Unknown) server.SetAgentStatus(AgentStatus.NotInstalled, clock.UtcNow);
            changed.Add("transport");
        }

        if (patch.Has("roles") && body.Roles is { } roles)
        {
            server.Roles = roles.Select(r => EnumText.Parse<ServerRole>(r)!.Value).Distinct().ToList();
            changed.Add("roles");
        }

        if (patch.Has("publicIp")) { server.PublicIp = NormalizeIp(body.PublicIp); changed.Add("publicIp"); }
        if (patch.Has("maxConcurrentBuilds") && body.MaxConcurrentBuilds is { } builds) { server.MaxConcurrentBuilds = builds; changed.Add("maxConcurrentBuilds"); }
        if (patch.Has("lifecycle") && EnumText.Parse<ServerLifecycle>(body.Lifecycle) is { } lifecycle) { server.Lifecycle = lifecycle; changed.Add("lifecycle"); }
        if (patch.Has("sshCredentialSecretId"))
        {
            var previous = server.SshCredentialSecretId;
            if (body.SshCredentialSecretId is { } secretId) await ManagedSecrets.ClaimForServerAsync(db, org, secretId, "/sshCredentialSecretId", ct);
            server.SshCredentialSecretId = body.SshCredentialSecretId;
            if (previous is { } old && old != body.SshCredentialSecretId) await ManagedSecrets.ReleaseFromServerAsync(db, old, id, ct);
            changed.Add("sshCredentialSecretId");
        }

        if (patch.Has("resources") && body.Resources is { } resources)
        {
            ApplyResources(server, resources, path => patch.Has("resources." + path));
            changed.Add("resources");
        }

        await audit.RecordAsync("server.updated", "server", id, new { name = server.Name, changed }, ct);
        http.SetETag(server.RowVersion);
        return TypedResults.Ok(ToResponse(server, await db.Workloads.CountAsync(w => w.ServerId == id, ct)));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var server = await db.GetServerAsync(actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(server.RowVersion);
        Confirmation.Require(confirm, server.Name);

        var assigned = await db.Workloads.CountAsync(w => w.ServerId == id, ct);
        if (assigned > 0)
            throw new ApiProblemException(ApiProblems.Conflict(ResourceProblemCodes.ServerInUse,
                $"{assigned} application(s) or service(s) are assigned to this server. Move or delete them first."));

        server.MarkDeleted(clock.UtcNow);
        if (server.SshCredentialSecretId is { } credential) await ManagedSecrets.ReleaseFromServerAsync(db, credential, id, ct);
        await audit.RecordAsync("server.deleted", "server", id, new { name = server.Name, host = server.Host }, ct);
        return TypedResults.NoContent();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static async Task EnsureNameFreeAsync(AetheraDbContext db, Guid org, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Servers.AnyAsync(s => s.OrganizationId == org && s.Name == name && s.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("server", $"A server named '{name}' already exists."));
    }

    private static string? NormalizeIp(string? ip) => ip is not null && IPAddress.TryParse(ip, out var parsed) ? parsed.ToString() : null;

    private static void ApplyResources(Server server, ServerResourcesRequest? r, Func<string, bool> has)
    {
        if (r is null) return;
        if (has("cpuCores")) server.Facts.CpuCores = r.CpuCores;
        if (has("memoryBytes")) server.Facts.MemoryBytes = r.MemoryBytes;
        if (has("diskBytes")) server.Facts.DiskBytes = r.DiskBytes;
        if (has("os")) server.Facts.Os = r.Os;
        if (has("architecture")) server.Facts.Architecture = r.Architecture;
    }

    internal static ServerResponse ToResponse(Server s, int workloadCount) => new(
        s.Id, s.Name, s.Host, s.SshPort, s.SshUser, s.Transport, s.Roles.OrderBy(r => r).ToList(), s.Lifecycle, s.PublicIp, s.MaxConcurrentBuilds,
        s.SshCredentialSecretId, new ServerResourcesResponse(s.Facts.CpuCores, s.Facts.MemoryBytes, s.Facts.DiskBytes, s.Facts.Os, s.Facts.Architecture),
        new ServerStatusResponse(
            new ReachabilityStatusResponse(s.ReachabilityStatus, s.ReachabilityCheckedAt, s.ReachabilityChangedAt, s.ReachabilityProbePort),
            new AgentStatusResponse(s.AgentStatus, s.LastHeartbeatAt, s.AgentStatusChangedAt),
            new DockerStatusResponse(s.DockerStatus, s.DockerStatusChangedAt)),
        s.AgentVersion, workloadCount, s.CreatedAt, s.UpdatedAt);
}
