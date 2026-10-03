using System.Net;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.DomainNames;

public sealed record CreateDomainRequest
{
    /// <summary>The application or service the domain routes to (implied by the URL on <c>/applications/{id}/domains</c>).</summary>
    public Guid? WorkloadId { get; init; }

    /// <summary>A hostname; IDNs are converted to punycode and the name is lower-cased. A wildcard is only allowed as the leftmost label.</summary>
    public string? Hostname { get; init; }

    /// <summary>Starts with <c>/</c>; default <c>/</c>.</summary>
    public string? PathPrefix { get; init; }

    public bool? HttpsEnabled { get; init; }

    /// <summary>Container port to route to; the workload's first HTTP port when absent.</summary>
    public int? TargetPort { get; init; }

    public bool? IsPrimary { get; init; }
}

public sealed record UpdateDomainRequest
{
    [NotClearable] public string? Hostname { get; init; }
    [NotClearable] public string? PathPrefix { get; init; }
    [NotClearable] public bool? HttpsEnabled { get; init; }
    public int? TargetPort { get; init; }
    [NotClearable] public bool? IsPrimary { get; init; }
}

public sealed record DomainCertificateResponse(CertificateStatus Status, DateTimeOffset? ExpiresAt, string? Error);

public sealed record DomainDnsResponse(DnsStatus Status, DateTimeOffset? CheckedAt, IReadOnlyList<string> ResolvedIps);

public sealed record DomainResponse(
    Guid Id, Guid WorkloadId, Guid ServerId, string Hostname, string PathPrefix, bool HttpsEnabled, int? TargetPort, bool IsPrimary,
    DomainCertificateResponse Certificate, DomainDnsResponse Dns, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Result of <c>POST /domains/{id}/verify-dns</c>: what the name resolves to versus what the server's address is.</summary>
public sealed record DnsCheckResponse(
    Guid DomainId, string Hostname, DnsStatus Status, IReadOnlyList<string> ExpectedIps, IReadOnlyList<string> ResolvedIps, DateTimeOffset CheckedAt,
    string Message);

public static class DomainRules
{
    public static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = "/";
        if (path is null) return true;
        var text = path.Trim();
        if (text.Length == 0 || text[0] != '/' || text.Length > 500 || text.Any(c => char.IsWhiteSpace(c) || c is '?' or '#' or '\\' or '"')) return false;
        text = System.Text.RegularExpressions.Regex.Replace(text, "/{2,}", "/");
        if (text.Split('/').Contains("..")) return false;
        normalized = text.Length > 1 ? text.TrimEnd('/') : text;
        if (normalized.Length == 0) normalized = "/";
        return true;
    }
}

public static class HostnameValidatorExtensions
{
    /// <summary>A valid domain hostname (see <see cref="HostnameRules"/>); code <c>domain.invalid_host</c>.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeHostname<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(h => HostnameRules.TryNormalize(h, allowWildcard: true, out _, out _)).WithErrorCode(ResourceProblemCodes.DomainInvalidHost)
            .WithMessage((request, h) => HostnameRules.TryNormalize(h, allowWildcard: true, out _, out var error) ? "" : error);
}

public sealed class CreateDomainValidator : AbstractValidator<CreateDomainRequest>
{
    public CreateDomainValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty();
        RuleFor(x => x.Hostname).MustBeHostname().When(x => !string.IsNullOrWhiteSpace(x.Hostname));
        RuleFor(x => x.PathPrefix).Must(p => DomainRules.TryNormalizePath(p, out _)).WithErrorCode("pattern")
            .WithMessage("Must start with '/' and contain no spaces, '?', '#' or '..' segments.");
        RuleFor(x => x.TargetPort).InclusiveBetween(1, 65535).When(x => x.TargetPort is not null);
    }
}

public sealed class UpdateDomainValidator : AbstractValidator<UpdateDomainRequest>
{
    public UpdateDomainValidator()
    {
        RuleFor(x => x.Hostname).MustBeHostname().When(x => x.Hostname is not null);
        RuleFor(x => x.PathPrefix).Must(p => DomainRules.TryNormalizePath(p, out _)).WithErrorCode("pattern")
            .WithMessage("Must start with '/' and contain no spaces, '?', '#' or '..' segments.").When(x => x.PathPrefix is not null);
        RuleFor(x => x.TargetPort).InclusiveBetween(1, 65535).When(x => x.TargetPort is not null);
    }
}

internal static class DomainEndpoints
{
    private static readonly SortDefinition<WorkloadDomain> Sorts = new SortDefinition<WorkloadDomain>("hostname")
        .Add("hostname", d => d.Hostname).Add("createdAt", d => d.CreatedAt).Add("updatedAt", d => d.UpdatedAt);

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/domains").WithTags("Domains");

        group.MapGet("/", List).WithName("listDomains").RequireRead();
        group.MapPost("/", (CreateDomainRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct) =>
                Create(request, request.WorkloadId, http, db, actor, audit, ct))
            .WithName("createDomain").Validate<CreateDomainRequest>().RequireWrite();
        group.MapGet("/{id:guid}", Get).WithName("getDomain").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateDomain")
            .Accepts<UpdateDomainRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateDomainRequest>().RequireWrite();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteDomain").RequireWrite();
        group.MapPost("/{id:guid}/verify-dns", VerifyDns).WithName("verifyDomainDns").RequireWrite();

        foreach (var (collection, singular) in new[] { ("applications", "Application"), ("services", "Service") })
        {
            var nested = api.MapGroup($"/{collection}/{{workloadId:guid}}/domains").WithTags("Domains");
            var isApplication = singular == "Application";
            nested.MapGet("/", (Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors,
                    [AsParameters] PageRequest page, string? sort, string? q, CancellationToken ct) =>
                    ListForWorkload(isApplication, workloadId, http, db, actor, cursors, page, sort, q, ct))
                .WithName($"list{singular}Domains").RequireRead();
            nested.MapPost("/", async (Guid workloadId, CreateDomainRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor,
                    IAuditLog audit, CancellationToken ct) =>
                {
                    await WorkloadSupport.RequireWorkloadRouteAsync(db, actor.Org(), isApplication, workloadId, ct);
                    if (request.WorkloadId is { } given && given != workloadId)
                        throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/workloadId", "mismatch",
                            "Does not match the application or service in the URL.")]));
                    return await Create(request, workloadId, http, db, actor, audit, ct);
                })
                .WithName($"create{singular}Domain").Validate<CreateDomainRequest>().RequireWrite();
        }
    }

    // ---- handlers -------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<DomainResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        Guid? workloadId, Guid? serverId, string? dnsStatus, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("workloadId", "serverId", "dnsStatus", "q");
        var statuses = ResourceHttp.ParseEnumFilter<DnsStatus>(dnsStatus, "dnsStatus");
        var query = db.DomainsOf(actor.Org()).AsNoTracking();
        if (workloadId is { } w) query = query.Where(d => d.WorkloadId == w);
        if (serverId is { } s) query = query.Where(d => d.ServerId == s);
        if (statuses is not null) query = query.Where(d => statuses.Contains(d.DnsStatus));
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(d => EF.Functions.ILike(d.Hostname, like, "\\"));

        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&serverId={serverId}&dnsStatus={dnsStatus}&q={q}", ct);
        return TypedResults.Ok(new Page<DomainResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Ok<Page<DomainResponse>>> ListForWorkload(
        bool isApplication, Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, PageRequest page,
        string? sort, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var org = actor.Org();
        await WorkloadSupport.RequireWorkloadRouteAsync(db, org, isApplication, workloadId, ct);
        var query = db.DomainsOf(org).AsNoTracking().Where(d => d.WorkloadId == workloadId);
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(d => EF.Functions.ILike(d.Hostname, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&q={q}", ct);
        return TypedResults.Ok(new Page<DomainResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<DomainResponse>> Create(
        CreateDomainRequest request, Guid? workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var workload = await db.RequireWorkloadAsync(org, workloadId, "/workloadId", ct);
        HostnameRules.TryNormalize(request.Hostname, allowWildcard: true, out var hostname, out _);
        DomainRules.TryNormalizePath(request.PathPrefix, out var path);
        await EnsureFreeAsync(db, hostname, path, null, ct);

        var domain = new WorkloadDomain
        {
            WorkloadId = workload.Id, ServerId = workload.ServerId, PathPrefix = path, HttpsEnabled = request.HttpsEnabled ?? true,
            TargetPort = request.TargetPort,
        };
        domain.SetHostname(hostname);

        var hasPrimary = await db.Domains.AnyAsync(d => d.WorkloadId == workload.Id && d.IsPrimary, ct);
        domain.IsPrimary = request.IsPrimary ?? !hasPrimary;
        if (domain.IsPrimary) await UnsetPrimaryAsync(db, workload.Id, domain.Id, ct);

        db.Domains.Add(domain);
        await audit.RecordAsync("domain.created", "domain", domain.Id, new { hostname, pathPrefix = path, workloadId = workload.Id }, ct);
        http.SetETag(domain.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("domains", domain.Id), ToResponse(domain));
    }

    private static async Task<Ok<DomainResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(domain.RowVersion);
        return TypedResults.Ok(ToResponse(domain));
    }

    private static async Task<Ok<DomainResponse>> Update(
        Guid id, PatchRequest<UpdateDomainRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(domain.RowVersion);
        var body = patch.Body;

        var hostname = domain.Hostname;
        var path = domain.PathPrefix;
        var changed = new List<string>();
        if (patch.Has("hostname") && body.Hostname is { } rawHost)
        {
            HostnameRules.TryNormalize(rawHost, allowWildcard: true, out hostname, out _);
            changed.Add("hostname");
        }

        if (patch.Has("pathPrefix") && body.PathPrefix is { } rawPath)
        {
            DomainRules.TryNormalizePath(rawPath, out path);
            changed.Add("pathPrefix");
        }

        if (hostname != domain.Hostname || path != domain.PathPrefix)
        {
            await EnsureFreeAsync(db, hostname, path, id, ct);
            if (hostname != domain.Hostname)
            {
                // A new name has never been checked and has no certificate yet.
                domain.SetHostname(hostname);
                domain.DnsStatus = DnsStatus.Unknown;
                domain.DnsCheckedAt = null;
                domain.DnsResolvedIps = [];
                domain.CertificateStatus = CertificateStatus.None;
                domain.CertificateExpiresAt = null;
                domain.CertificateError = null;
            }

            domain.PathPrefix = path;
        }

        if (patch.Has("httpsEnabled") && body.HttpsEnabled is { } https) { domain.HttpsEnabled = https; changed.Add("httpsEnabled"); }
        if (patch.Has("targetPort")) { domain.TargetPort = body.TargetPort; changed.Add("targetPort"); }
        if (patch.Has("isPrimary") && body.IsPrimary is { } primary)
        {
            domain.IsPrimary = primary;
            if (primary) await UnsetPrimaryAsync(db, domain.WorkloadId, id, ct);
            changed.Add("isPrimary");
        }

        await audit.RecordAsync("domain.updated", "domain", id, new { hostname = domain.Hostname, changed }, ct);
        http.SetETag(domain.RowVersion);
        return TypedResults.Ok(ToResponse(domain));
    }

    private static async Task<NoContent> Delete(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(domain.RowVersion);
        domain.MarkDeleted(clock.UtcNow);
        await audit.RecordAsync("domain.deleted", "domain", id, new { hostname = domain.Hostname, pathPrefix = domain.PathPrefix }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<DnsCheckResponse>> VerifyDns(
        Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IDnsResolver resolver, IClock clock, ILogger<IDnsResolver> logger,
        CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == domain.ServerId, ct);

        var expected = new SortedSet<string>(StringComparer.Ordinal);
        if (server?.PublicIp is { } publicIp && IPAddress.TryParse(publicIp, out var parsedPublic)) expected.Add(parsedPublic.ToString());
        if (server is not null && IPAddress.TryParse(server.Host, out var parsedHost)) expected.Add(parsedHost.ToString());

        // A wildcard cannot be looked up itself: probe a name the wildcard covers.
        var probe = domain.Hostname.StartsWith("*.", StringComparison.Ordinal) ? "aethera-dns-check." + domain.Hostname[2..] : domain.Hostname;
        var resolved = new SortedSet<string>(StringComparer.Ordinal);
        DnsStatus status;
        string message;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(LookupTimeout);
            try
            {
                foreach (var address in await resolver.ResolveAsync(probe, timeout.Token))
                    resolved.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString());

                (status, message) = Evaluate(expected, resolved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("DNS lookup for domain {DomainId} failed: {ExceptionType}", id, ex.GetType().Name);
                status = DnsStatus.Error;
                message = "The DNS lookup failed or timed out. Try again later.";
            }
        }

        var now = clock.UtcNow;
        domain.DnsStatus = status;
        domain.DnsCheckedAt = now;
        domain.DnsResolvedIps = resolved.ToList();
        await audit.RecordAsync("domain.dns_verified", "domain", id, new { hostname = domain.Hostname, status }, ct);
        return TypedResults.Ok(new DnsCheckResponse(id, domain.Hostname, status, expected.ToList(), resolved.ToList(), now, message));
    }

    private static (DnsStatus Status, string Message) Evaluate(IReadOnlySet<string> expected, IReadOnlySet<string> resolved)
    {
        if (resolved.Count == 0) return (DnsStatus.Missing, "The name does not resolve to any address (no A/AAAA records).");
        if (expected.Count == 0) return (DnsStatus.Unknown, "The server has no public IP recorded, so the result cannot be compared. Set publicIp on the server.");
        return resolved.Overlaps(expected)
            ? (DnsStatus.Ok, "The name resolves to the server.")
            : (DnsStatus.Mismatch, "The name resolves, but not to the server's address.");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------

    private static async Task<WorkloadDomain> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.DomainsOf(org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("domain", id));
    }

    private static async Task EnsureFreeAsync(AetheraDbContext db, string hostname, string path, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.DeletedAt == null && d.Hostname == hostname && d.PathPrefix == path && d.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("domain", $"The domain '{hostname}{(path == "/" ? "" : path)}' is already in use."));
    }

    private static async Task UnsetPrimaryAsync(AetheraDbContext db, Guid workloadId, Guid exceptId, CancellationToken ct)
    {
        foreach (var other in await db.Domains.Where(d => d.WorkloadId == workloadId && d.IsPrimary && d.Id != exceptId).ToListAsync(ct))
            other.IsPrimary = false;
    }

    internal static DomainResponse ToResponse(WorkloadDomain d) => new(
        d.Id, d.WorkloadId, d.ServerId, d.Hostname, d.PathPrefix, d.HttpsEnabled, d.TargetPort, d.IsPrimary,
        new DomainCertificateResponse(d.CertificateStatus, d.CertificateExpiresAt, d.CertificateError),
        new DomainDnsResponse(d.DnsStatus, d.DnsCheckedAt, d.DnsResolvedIps), d.CreatedAt, d.UpdatedAt);
}
