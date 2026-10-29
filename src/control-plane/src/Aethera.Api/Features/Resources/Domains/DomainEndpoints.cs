using System.Text.RegularExpressions;
using Aethera.Api.Features.DnsProviders;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Features.Resources.Workloads;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Domain;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

    /// <summary>Compose applications: the service of the compose file that serves the domain (required there, refused elsewhere).</summary>
    public string? ComposeService { get; init; }

    /// <summary>Answer with a permanent redirect to this hostname (path and query kept) instead of serving the application.</summary>
    public string? RedirectTo { get; init; }

    /// <summary>Remove the path prefix before the request reaches the container.</summary>
    public bool? StripPathPrefix { get; init; }

    /// <summary>HTTP basic auth users. Passwords are hashed (bcrypt) on arrival and never returned.</summary>
    public List<BasicAuthUserRequest>? BasicAuth { get; init; }

    /// <summary>Issue the certificate through this DNS provider (DNS-01). Required for wildcard hosts.</summary>
    public Guid? DnsProviderId { get; init; }
}

public sealed record BasicAuthUserRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed record UpdateDomainRequest
{
    [NotClearable] public string? Hostname { get; init; }
    [NotClearable] public string? PathPrefix { get; init; }
    [NotClearable] public bool? HttpsEnabled { get; init; }
    public int? TargetPort { get; init; }
    [NotClearable] public bool? IsPrimary { get; init; }
    public string? ComposeService { get; init; }
    public string? RedirectTo { get; init; }
    [NotClearable] public bool? StripPathPrefix { get; init; }

    /// <summary>Replaces the users; an empty list (or null) removes basic auth.</summary>
    public List<BasicAuthUserRequest>? BasicAuth { get; init; }

    public Guid? DnsProviderId { get; init; }
}

public sealed record DomainCertificateResponse(CertificateStatus Status, DateTimeOffset? ExpiresAt, string? Error);

public sealed record DomainDnsResponse(DnsStatus Status, DateTimeOffset? CheckedAt, IReadOnlyList<string> ResolvedIps);

/// <param name="BasicAuthUsers">User names only; the hashes stay in a managed secret.</param>
/// <param name="Warnings">Things that will not work as configured, e.g. HTTPS without an ACME email.</param>
public sealed record DomainResponse(
    Guid Id, Guid WorkloadId, Guid ServerId, string Hostname, string PathPrefix, bool HttpsEnabled, int? TargetPort, bool IsPrimary,
    string? ComposeService, string? RedirectTo, bool StripPathPrefix, IReadOnlyList<string> BasicAuthUsers, Guid? DnsProviderId,
    DomainCertificateResponse Certificate, DomainDnsResponse Dns, IReadOnlyList<string> Warnings, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool IsAutomatic = false);

/// <summary>
/// Result of <c>POST /domains/{id}/verify-dns</c>: what the name resolves to versus what the server's address is. <c>method</c> says how it
/// was found: <c>resolver</c> (a DNS lookup) or <c>cloudflareApi</c> (the record read through a DNS provider, ADR 0010).
/// </summary>
public sealed record DnsCheckResponse(
    Guid DomainId, string Hostname, DnsStatus Status, IReadOnlyList<string> ExpectedIps, IReadOnlyList<string> ResolvedIps, DateTimeOffset CheckedAt,
    string Message, string Method);

public static partial class DomainRules
{
    public const int MaxPathPrefixLength = 256;

    public const string PathPrefixMessage =
        "Must start with '/' and contain only letters, digits and . _ ~ / - (at most 256 characters, no '//', '.' or '..' segments).";

    // Only characters that are safe inside a generated router rule (Traefik PathPrefix(`...`), ADR 0006); no query, quote, backtick or space.
    [GeneratedRegex(@"\A/[A-Za-z0-9._~/-]*\z")]
    private static partial Regex PathPrefixPattern();

    /// <summary>
    /// A path prefix: <c>/</c> followed by letters, digits and <c>. _ ~ / -</c>, at most 256 characters, no <c>//</c>, no <c>.</c> or <c>..</c>
    /// segment. A trailing slash is dropped. <c>null</c> means the default <c>/</c>.
    /// </summary>
    public static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = "/";
        if (path is null) return true;
        if (path.Length == 0 || path.Length > MaxPathPrefixLength || !PathPrefixPattern().IsMatch(path) || path.Contains("//", StringComparison.Ordinal))
            return false;
        if (path.Split('/').Any(segment => segment is "." or "..")) return false;
        normalized = path.Length > 1 ? path.TrimEnd('/') : path;
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

/// <summary>Grammars of the domain options (ADR 0010). Everything that ends up in a Traefik rule, label or htpasswd line is checked against these.</summary>
public static partial class DomainOptionRules
{
    public const int MaxBasicAuthUsers = 20;
    public const int MinPasswordLength = 8;

    /// <summary>A compose service name: what Compose accepts, without anything a label or an override could misread.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z")]
    private static partial Regex ServicePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9._@-]{1,64}\z")]
    private static partial Regex UserPattern();

    public static bool IsServiceName(string? value) => value is not null && ServicePattern().IsMatch(value);

    public static bool IsUserName(string? value) => value is not null && UserPattern().IsMatch(value);

    /// <summary>bcrypt reads at most 72 bytes: a longer password would be silently cut, so it is refused.</summary>
    public static bool IsPassword(string? value) =>
        value is { Length: >= MinPasswordLength } && System.Text.Encoding.UTF8.GetByteCount(value) <= 72 && !value.Any(char.IsControl);

    public static IRuleBuilderOptions<T, string?> MustBeServiceName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(IsServiceName).WithErrorCode("pattern")
            .WithMessage("A compose service name: letters, digits, '.', '_' and '-' (at most 64 characters), starting with a letter or digit.");

    /// <summary>A plain (non-wildcard) hostname, normalized by <see cref="HostnameRules"/>.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeRedirectTarget<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(h => HostnameRules.TryNormalize(h, allowWildcard: false, out _, out _)).WithErrorCode(ResourceProblemCodes.DomainInvalidHost)
            .WithMessage((request, h) => HostnameRules.TryNormalize(h, allowWildcard: false, out _, out var error) ? "" : error);
}

public sealed class BasicAuthUserValidator : AbstractValidator<BasicAuthUserRequest>
{
    public BasicAuthUserValidator()
    {
        RuleFor(x => x.Username).NotEmpty();
        RuleFor(x => x.Username).Must(DomainOptionRules.IsUserName).WithErrorCode("pattern")
            .WithMessage("1-64 letters, digits and . _ @ - (no ':' and no spaces).").When(x => !string.IsNullOrEmpty(x.Username));
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Password).Must(DomainOptionRules.IsPassword).WithErrorCode("pattern")
            .WithMessage($"{DomainOptionRules.MinPasswordLength} to 72 bytes, no control characters.").When(x => !string.IsNullOrEmpty(x.Password));
    }
}

/// <summary>The users of one domain: each valid, at most <see cref="DomainOptionRules.MaxBasicAuthUsers"/>, no name twice.</summary>
public sealed class BasicAuthListValidator : AbstractValidator<List<BasicAuthUserRequest>>
{
    public BasicAuthListValidator()
    {
        RuleForEach(x => x).SetValidator(new BasicAuthUserValidator());
        RuleFor(x => x).Must(list => list.Count <= DomainOptionRules.MaxBasicAuthUsers).WithErrorCode("too_long")
            .WithMessage($"At most {DomainOptionRules.MaxBasicAuthUsers} users.");
        RuleFor(x => x).Must(list => list.Select(u => u.Username).Distinct(StringComparer.Ordinal).Count() == list.Count)
            .WithErrorCode("duplicate").WithMessage("A user name can only appear once.");
    }
}

public sealed class CreateDomainValidator : AbstractValidator<CreateDomainRequest>
{
    public CreateDomainValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty();
        RuleFor(x => x.Hostname).MustBeHostname().When(x => !string.IsNullOrWhiteSpace(x.Hostname));
        RuleFor(x => x.PathPrefix).Must(p => DomainRules.TryNormalizePath(p, out _)).WithErrorCode("pattern")
            .WithMessage(DomainRules.PathPrefixMessage);
        RuleFor(x => x.TargetPort).InclusiveBetween(1, 65535).When(x => x.TargetPort is not null);
        RuleFor(x => x.ComposeService).MustBeServiceName().When(x => x.ComposeService is not null);
        RuleFor(x => x.RedirectTo).MustBeRedirectTarget().When(x => x.RedirectTo is not null);
        RuleFor(x => x.BasicAuth!).SetValidator(new BasicAuthListValidator()).When(x => x.BasicAuth is not null);
    }
}

public sealed class UpdateDomainValidator : AbstractValidator<UpdateDomainRequest>
{
    public UpdateDomainValidator()
    {
        RuleFor(x => x.Hostname).MustBeHostname().When(x => x.Hostname is not null);
        RuleFor(x => x.PathPrefix).Must(p => DomainRules.TryNormalizePath(p, out _)).WithErrorCode("pattern")
            .WithMessage(DomainRules.PathPrefixMessage).When(x => x.PathPrefix is not null);
        RuleFor(x => x.TargetPort).InclusiveBetween(1, 65535).When(x => x.TargetPort is not null);
        RuleFor(x => x.ComposeService).MustBeServiceName().When(x => x.ComposeService is not null);
        RuleFor(x => x.RedirectTo).MustBeRedirectTarget().When(x => x.RedirectTo is not null);
        RuleFor(x => x.BasicAuth!).SetValidator(new BasicAuthListValidator()).When(x => x.BasicAuth is not null);
    }
}

internal static class DomainEndpoints
{
    private static readonly SortDefinition<WorkloadDomain> Sorts = new SortDefinition<WorkloadDomain>("hostname")
        .Add("hostname", d => d.Hostname).Add("createdAt", d => d.CreatedAt).Add("updatedAt", d => d.UpdatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/domains").WithTags("Domains");

        group.MapGet("/", List).WithName("listDomains").RequireRead();
        group.MapPost("/", (CreateDomainRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, IOptions<ProxyOptions> proxy, CancellationToken ct) =>
                Create(request, request.WorkloadId, http, db, actor, audit, vault, proxy.Value, ct))
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
            nested.MapGet("/", (Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IOptions<ProxyOptions> proxy, KeysetCursor cursors,
                    [AsParameters] PageRequest page, string? sort, string? q, CancellationToken ct) =>
                    ListForWorkload(isApplication, workloadId, http, db, actor, vault, proxy.Value, cursors, page, sort, q, ct))
                .WithName($"list{singular}Domains").RequireRead();
            nested.MapPost("/", async (Guid workloadId, CreateDomainRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor,
                    IAuditLog audit, SecretVault vault, IOptions<ProxyOptions> proxy, CancellationToken ct) =>
                {
                    await WorkloadSupport.RequireWorkloadRouteAsync(db, actor.Org(), isApplication, workloadId, ct);
                    if (request.WorkloadId is { } given && given != workloadId)
                        throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/workloadId", "mismatch",
                            "Does not match the application or service in the URL.")]));
                    return await Create(request, workloadId, http, db, actor, audit, vault, proxy.Value, ct);
                })
                .WithName($"create{singular}Domain").Validate<CreateDomainRequest>().RequireWrite();
        }
    }

    // ---- handlers -------------------------------------------------------------------------------------------------------------------

    private static async Task<Ok<Page<DomainResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IOptions<ProxyOptions> proxy, KeysetCursor cursors,
        [AsParameters] PageRequest page, string? sort, Guid? workloadId, Guid? serverId, string? dnsStatus, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("workloadId", "serverId", "dnsStatus", "q");
        var statuses = ResourceHttp.ParseEnumFilter<DnsStatus>(dnsStatus, "dnsStatus");
        var query = db.DomainsOf(actor.Org()).AsNoTracking();
        if (workloadId is { } w) query = query.Where(d => d.WorkloadId == w);
        if (serverId is { } s) query = query.Where(d => d.ServerId == s);
        if (statuses is not null) query = query.Where(d => statuses.Contains(d.DnsStatus));
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(d => EF.Functions.ILike(d.Hostname, like, "\\"));

        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&serverId={serverId}&dnsStatus={dnsStatus}&q={q}", ct);
        return TypedResults.Ok(new Page<DomainResponse>(await ToResponsesAsync(items, db, vault, proxy.Value, ct), next));
    }

    private static async Task<Ok<Page<DomainResponse>>> ListForWorkload(
        bool isApplication, Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, SecretVault vault, ProxyOptions proxy,
        KeysetCursor cursors, PageRequest page, string? sort, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var org = actor.Org();
        await WorkloadSupport.RequireWorkloadRouteAsync(db, org, isApplication, workloadId, ct);
        var query = db.DomainsOf(org).AsNoTracking().Where(d => d.WorkloadId == workloadId);
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(d => EF.Functions.ILike(d.Hostname, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&q={q}", ct);
        return TypedResults.Ok(new Page<DomainResponse>(await ToResponsesAsync(items, db, vault, proxy, ct), next));
    }

    private static async Task<Created<DomainResponse>> Create(
        CreateDomainRequest request, Guid? workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        ProxyOptions proxy, CancellationToken ct)
    {
        var org = actor.Org();
        var workload = await db.RequireWorkloadAsync(org, workloadId, "/workloadId", ct);
        HostnameRules.TryNormalize(request.Hostname, allowWildcard: true, out var hostname, out _);
        DomainRules.TryNormalizePath(request.PathPrefix, out var path);
        string? redirectTo = null;
        if (request.RedirectTo is not null) HostnameRules.TryNormalize(request.RedirectTo, allowWildcard: false, out redirectTo, out _);
        var basicAuth = request.BasicAuth is { Count: > 0 } ? request.BasicAuth : null;
        await ValidateOptionsAsync(db, org, workload, hostname, request.ComposeService, redirectTo, request.StripPathPrefix ?? false, basicAuth is not null,
            request.DnsProviderId, ct);
        await EnsureFreeAsync(db, hostname, path, null, ct);

        var domain = new WorkloadDomain
        {
            WorkloadId = workload.Id, ServerId = workload.ServerId, PathPrefix = path, HttpsEnabled = request.HttpsEnabled ?? true,
            TargetPort = request.TargetPort, ComposeService = request.ComposeService, RedirectTo = redirectTo,
            StripPathPrefix = request.StripPathPrefix ?? false, DnsProviderId = request.DnsProviderId,
        };
        domain.SetHostname(hostname);
        if (basicAuth is not null) domain.BasicAuthSecretId = StoreBasicAuth(vault, actor, org, workload.Id, domain, basicAuth).Id;

        var hasPrimary = await db.Domains.AnyAsync(d => d.WorkloadId == workload.Id && d.IsPrimary, ct);
        domain.IsPrimary = request.IsPrimary ?? !hasPrimary;
        if (domain.IsPrimary) await UnsetPrimaryAsync(db, workload.Id, domain.Id, ct);

        db.Domains.Add(domain);
        await audit.RecordAsync("domain.created", "domain", domain.Id,
            new { hostname, pathPrefix = path, workloadId = workload.Id, composeService = domain.ComposeService, redirectTo, basicAuth = basicAuth is not null }, ct);
        http.SetETag(domain.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("domains", domain.Id), await ToResponseAsync(domain, db, vault, proxy, ct, basicAuth?.Select(u => u.Username!).ToList() ?? []));
    }

    private static async Task<Ok<DomainResponse>> Get(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IOptions<ProxyOptions> proxy, CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(domain.RowVersion);
        return TypedResults.Ok(await ToResponseAsync(domain, db, vault, proxy.Value, ct));
    }

    private static async Task<Ok<DomainResponse>> Update(
        Guid id, PatchRequest<UpdateDomainRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        IOptions<ProxyOptions> proxy, IClock clock, CancellationToken ct)
    {
        var org = actor.Org();
        var domain = await FindAsync(db, org, id, tracking: true, ct);
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

        // The options as they will be, checked together (a wildcard needs a provider, a compose domain a service, ...).
        var composeService = patch.Has("composeService") ? body.ComposeService : domain.ComposeService;
        var redirectTo = domain.RedirectTo;
        if (patch.Has("redirectTo")) HostnameRules.TryNormalize(body.RedirectTo, allowWildcard: false, out redirectTo, out _);
        if (patch.Has("redirectTo") && body.RedirectTo is null) redirectTo = null;
        var strip = patch.Has("stripPathPrefix") && body.StripPathPrefix is { } s ? s : domain.StripPathPrefix;
        var dnsProviderId = patch.Has("dnsProviderId") ? body.DnsProviderId : domain.DnsProviderId;
        var newAuth = patch.Has("basicAuth") && body.BasicAuth is { Count: > 0 } ? body.BasicAuth : null;
        var hasAuth = patch.Has("basicAuth") ? newAuth is not null : domain.BasicAuthSecretId is not null;
        var workload = await db.RequireWorkloadAsync(org, domain.WorkloadId, "/workloadId", ct);
        await ValidateOptionsAsync(db, org, workload, hostname, composeService, redirectTo, strip, hasAuth, dnsProviderId, ct);

        if (hostname != domain.Hostname || path != domain.PathPrefix)
        {
            await EnsureFreeAsync(db, hostname, path, id, ct);
            if (hostname != domain.Hostname)
            {
                // A new name has never been checked and has no certificate yet; once renamed it is the user's own, not an automatic one.
                domain.SetHostname(hostname);
                domain.IsAutomatic = false;
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
        if (patch.Has("composeService")) { domain.ComposeService = composeService; changed.Add("composeService"); }
        if (patch.Has("redirectTo")) { domain.RedirectTo = redirectTo; changed.Add("redirectTo"); }
        if (patch.Has("stripPathPrefix")) { domain.StripPathPrefix = strip; changed.Add("stripPathPrefix"); }
        if (patch.Has("dnsProviderId")) { domain.DnsProviderId = dnsProviderId; changed.Add("dnsProviderId"); }
        if (patch.Has("basicAuth"))
        {
            if (newAuth is null)
            {
                if (domain.BasicAuthSecretId is { } old)
                {
                    (await db.Secrets.FirstAsync(x => x.Id == old, ct)).MarkDeleted(clock.UtcNow);
                    domain.BasicAuthSecretId = null;
                }
            }
            else if (domain.BasicAuthSecretId is { } existing)
            {
                ManagedSecrets.RequireTokenScope(actor);
                vault.AddVersion(await db.Secrets.FirstAsync(x => x.Id == existing, ct), HtpasswdOf(newAuth));
            }
            else
            {
                domain.BasicAuthSecretId = StoreBasicAuth(vault, actor, org, domain.WorkloadId, domain, newAuth).Id;
            }

            changed.Add("basicAuth");
        }

        if (patch.Has("isPrimary") && body.IsPrimary is { } primary)
        {
            domain.IsPrimary = primary;
            if (primary) await UnsetPrimaryAsync(db, domain.WorkloadId, id, ct);
            changed.Add("isPrimary");
        }

        await audit.RecordAsync("domain.updated", "domain", id, new { hostname = domain.Hostname, changed }, ct);
        http.SetETag(domain.RowVersion);
        return TypedResults.Ok(await ToResponseAsync(domain, db, vault, proxy.Value, ct, patch.Has("basicAuth") ? newAuth?.Select(u => u.Username!).ToList() ?? [] : null));
    }

    private static async Task<NoContent> Delete(
        Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(domain.RowVersion);
        var now = clock.UtcNow;
        domain.MarkDeleted(now);
        if (domain.BasicAuthSecretId is { } secretId) (await db.Secrets.FirstAsync(s => s.Id == secretId, ct)).MarkDeleted(now);
        await audit.RecordAsync("domain.deleted", "domain", id, new { hostname = domain.Hostname, pathPrefix = domain.PathPrefix }, ct);
        return TypedResults.NoContent();
    }

    // ---- options (ADR 0010) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks the options of a domain together with its workload: a compose application's domain names its service (and only those do), a
    /// wildcard host needs a DNS provider of the organization, a redirect serves nothing so it has no auth or prefix stripping.
    /// </summary>
    private static async Task ValidateOptionsAsync(
        AetheraDbContext db, Guid org, Workload workload, string hostname, string? composeService, string? redirectTo, bool strip, bool hasBasicAuth,
        Guid? dnsProviderId, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        var isCompose = workload is Application { SourceKind: ApplicationSourceKind.Compose };
        if (isCompose && string.IsNullOrEmpty(composeService))
            errors.Add(FieldError.AtPointer("/composeService", "required", "A domain of a compose application names the compose service that serves it."));
        if (!isCompose && composeService is not null)
            errors.Add(FieldError.AtPointer("/composeService", "not_applicable", "Only domains of compose applications name a compose service."));
        if (hostname.StartsWith("*.", StringComparison.Ordinal) && dnsProviderId is null)
            errors.Add(FieldError.AtPointer("/dnsProviderId", "domain.wildcard_needs_dns_provider",
                "A wildcard host needs a DNS provider: wildcard certificates are issued with a DNS-01 challenge."));
        if (redirectTo is not null)
        {
            if (redirectTo == hostname) errors.Add(FieldError.AtPointer("/redirectTo", "invalid", "A domain cannot redirect to itself."));
            if (hasBasicAuth) errors.Add(FieldError.AtPointer("/basicAuth", "not_applicable", "A redirect serves nothing, so it cannot have basic auth."));
            if (strip) errors.Add(FieldError.AtPointer("/stripPathPrefix", "not_applicable", "A redirect has no container to strip the prefix for."));
        }

        if (dnsProviderId is { } providerId
            && !await db.DnsProviders.AnyAsync(p => p.Id == providerId && p.OrganizationId == org && p.DeletedAt == null, ct))
            errors.Add(FieldError.AtPointer("/dnsProviderId", "not_found", "The DNS provider does not exist."));
        if (errors.Count > 0) throw new ApiProblemException(ApiProblems.Validation(errors));
    }

    private static string HtpasswdOf(IEnumerable<BasicAuthUserRequest> users) =>
        string.Join("\n", users.Select(u => $"{u.Username}:{Bcrypt.Hash(u.Password!)}"));

    /// <summary>The htpasswd lines go into a managed secret (<see cref="SecretPurpose.BasicAuth"/>); only the user names are ever returned.</summary>
    private static Secret StoreBasicAuth(SecretVault vault, ICurrentActor actor, Guid org, Guid workloadId, WorkloadDomain domain, IEnumerable<BasicAuthUserRequest> users)
    {
        ManagedSecrets.RequireTokenScope(actor);
        return vault.Create(org, $"basic-auth/{domain.Id.ToString("N")[^12..]}", "Basic auth users of a domain (bcrypt htpasswd lines).",
            HtpasswdOf(users), workloadId: workloadId, purpose: SecretPurpose.BasicAuth);
    }

    private static async Task<IReadOnlyList<string>> BasicAuthUsersAsync(WorkloadDomain d, AetheraDbContext db, SecretVault vault, CancellationToken ct)
    {
        if (d.BasicAuthSecretId is not { } secretId) return [];
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secretId, ct);
        if (secret is null) return [];
        var (_, value) = await vault.RevealAsync(secret, null, ct);
        return value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[..l.IndexOf(':')]).ToList();
    }

    private static async Task<Ok<DnsCheckResponse>> VerifyDns(
        Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, DomainDnsChecker checker, IClock clock, CancellationToken ct)
    {
        var domain = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        var outcome = await checker.CheckAsync(actor.Org(), domain, ct);

        var now = clock.UtcNow;
        domain.DnsStatus = outcome.Status;
        domain.DnsCheckedAt = now;
        domain.DnsResolvedIps = outcome.Resolved.ToList();
        await audit.RecordAsync("domain.dns_verified", "domain", id, new { hostname = domain.Hostname, status = outcome.Status, method = outcome.Method }, ct);
        return TypedResults.Ok(new DnsCheckResponse(id, domain.Hostname, outcome.Status, outcome.Expected, outcome.Resolved, now, outcome.Message, outcome.Method));
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

    private static async Task<IReadOnlyList<DomainResponse>> ToResponsesAsync(
        IEnumerable<WorkloadDomain> domains, AetheraDbContext db, SecretVault vault, ProxyOptions proxy, CancellationToken ct)
    {
        var result = new List<DomainResponse>();
        foreach (var d in domains) result.Add(await ToResponseAsync(d, db, vault, proxy, ct));
        return result;
    }

    private static async Task<DomainResponse> ToResponseAsync(
        WorkloadDomain d, AetheraDbContext db, SecretVault vault, ProxyOptions proxy, CancellationToken ct, IReadOnlyList<string>? basicAuthUsers = null)
    {
        var warnings = new List<string>();
        if (d.HttpsEnabled && !await ProxyConfigurator.AcmeAvailableAsync(db, proxy, ct)) warnings.Add(ProxyConfigurator.NoAcmeEmailWarning);
        return ToResponse(d, basicAuthUsers ?? await BasicAuthUsersAsync(d, db, vault, ct), warnings);
    }

    internal static DomainResponse ToResponse(WorkloadDomain d, IReadOnlyList<string> basicAuthUsers, IReadOnlyList<string> warnings) => new(
        d.Id, d.WorkloadId, d.ServerId, d.Hostname, d.PathPrefix, d.HttpsEnabled, d.TargetPort, d.IsPrimary,
        d.ComposeService, d.RedirectTo, d.StripPathPrefix, basicAuthUsers, d.DnsProviderId,
        new DomainCertificateResponse(d.CertificateStatus, d.CertificateExpiresAt, d.CertificateError),
        new DomainDnsResponse(d.DnsStatus, d.DnsCheckedAt, d.DnsResolvedIps), warnings, d.CreatedAt, d.UpdatedAt, d.IsAutomatic);
}
