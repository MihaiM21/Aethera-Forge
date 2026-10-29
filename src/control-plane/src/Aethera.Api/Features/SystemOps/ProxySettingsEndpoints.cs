using Aethera.Api.Features.DnsProviders;
using Aethera.Api.Features.Resources;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Deployments;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.SystemOps;

/// <summary>
/// The proxy and domain settings of the instance (ADR 0011). <c>source</c> is <c>settings</c> once they were saved here, <c>environment</c> while
/// they still come from the installer (<c>AETHERA_ACME_EMAIL</c>, <c>AETHERA_DOMAIN</c>). <c>suggestedDomain</c> is an automatic name for the UI
/// (<c>aethera.&lt;ip&gt;.sslip.io</c>) when the dashboard server's address is known.
/// </summary>
public sealed record ProxySettingsDto(
    string? AcmeEmail, bool AcmeStaging, string? DashboardDomain, bool DashboardHttps, Guid? DashboardServerId, string? DashboardUrl,
    string? SuggestedDomain, string Source);

/// <summary>Replaces the settings. An empty <c>dashboardDomain</c> removes the domain (the UI stays on its own port).</summary>
public sealed record UpdateProxySettingsRequest
{
    public string? AcmeEmail { get; init; }
    public bool? AcmeStaging { get; init; }
    public string? DashboardDomain { get; init; }
    public bool? DashboardHttps { get; init; }
}

/// <summary>
/// The result of saving: the settings, and what happened on the dashboard server's proxy. <c>proxy</c> is null when there is no server to apply
/// it to yet (the local server is not connected); <c>proxyError</c> says why it could not be applied (the settings are saved anyway).
/// </summary>
public sealed record ProxySettingsUpdatedDto(ProxySettingsDto Settings, bool ProxyApplied, string? ProxyError, DnsCheckDto? Dns);

public sealed record DnsCheckDto(DnsStatus Status, string Message, IReadOnlyList<string> Expected, IReadOnlyList<string> Resolved);

public sealed class UpdateProxySettingsValidator : AbstractValidator<UpdateProxySettingsRequest>
{
    public UpdateProxySettingsValidator()
    {
        RuleFor(x => x.AcmeEmail).MaximumLength(254)
            .Must(e => e!.Contains('@') && !e.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\' or ':'))
            .WithErrorCode("pattern").WithMessage("Must be an email address.")
            .When(x => !string.IsNullOrWhiteSpace(x.AcmeEmail));
        RuleFor(x => x.DashboardDomain)
            .Must(d => HostnameRules.TryNormalize(d, allowWildcard: false, out _, out _)).WithErrorCode("pattern")
            .WithMessage("Must be a hostname such as aethera.example.com.")
            .When(x => !string.IsNullOrWhiteSpace(x.DashboardDomain));
    }
}

internal static class ProxySettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var system = api.MapGroup("/system").WithTags("System");
        system.MapGet("/proxy-settings", Get).WithName("getProxySettings")
            .WithSummary("The Let's Encrypt account and the domain of the Aethera UI")
            .RequireAdmin(Scopes.Read).Produces<ProxySettingsDto>();
        system.MapPut("/proxy-settings", Update).WithName("updateProxySettings")
            .WithSummary("Set the Let's Encrypt account and the domain of the Aethera UI")
            .WithDescription("Saved settings win over the installer's environment from then on. The proxy of the dashboard server (the `local` one) is updated at once; the UI keeps answering on its own port, so a wrong domain cannot lock anyone out. Point a DNS A record at the server first: `dns` reports what the name resolves to.")
            .Validate<UpdateProxySettingsRequest>()
            .RequireAdmin(Scopes.Admin).Produces<ProxySettingsUpdatedDto>().ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Ok<ProxySettingsDto>> Get(AetheraDbContext db, IOptions<ProxyOptions> options, CancellationToken ct) =>
        TypedResults.Ok(await BuildAsync(db, options.Value, ct));

    private static async Task<Ok<ProxySettingsUpdatedDto>> Update(
        UpdateProxySettingsRequest request, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, IOptions<ProxyOptions> options,
        IServiceScopeFactory scopes, DomainDnsChecker dns, CancellationToken ct)
    {
        var current = await ProxySettingsStore.GetAsync(db, options.Value, ct);
        string? domain = null;
        if (!string.IsNullOrWhiteSpace(request.DashboardDomain))
        {
            HostnameRules.TryNormalize(request.DashboardDomain, allowWildcard: false, out var normalized, out _);
            domain = normalized;
            if (await db.Domains.AnyAsync(d => d.Hostname == domain, ct))
                throw new ApiProblemException(ApiProblems.Conflict("proxy.domain_in_use", $"{domain} is already a domain of an application or service."));
        }

        var acme = new AcmeSetting(string.IsNullOrWhiteSpace(request.AcmeEmail) ? null : request.AcmeEmail.Trim(), request.AcmeStaging ?? current.Acme.Staging);
        var dashboard = new DashboardSetting(domain, request.DashboardHttps ?? current.Dashboard.Https, current.Dashboard.ServerId);
        await ProxySettingsStore.SaveAsync(db, acme, dashboard, actor.UserId, clock.UtcNow, ct);
        await audit.RecordAsync("system.proxy_settings_updated", "system", null,
            new { acmeEmail = acme.AcmeEmail is not null, acme.Staging, dashboardDomain = domain, dashboard.Https }, ct); // saves the settings

        // Apply now on the dashboard server; a failure (agent offline) is reported, the settings stay saved and the next connect applies them.
        var serverId = await ProxySettingsStore.DashboardServerIdAsync(db, dashboard, ct);
        var applied = false;
        string? error = null;
        if (serverId is { } id)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                applied = await ProxyAutoEnsurer.EnsureAsync(scopes, id, timeout.Token) is not null;
                if (!applied) error = "The dashboard server has no agent connected (SSH servers have no proxy).";
            }
            catch (Exception ex) when (ex is ServerTransportException or OperationCanceledException or InvalidOperationException)
            {
                error = ex.Message;
            }
        }

        DnsCheckDto? check = null;
        if (domain is not null && serverId is { } dnsServer)
        {
            var probe = new WorkloadDomain { ServerId = dnsServer };
            probe.SetHostname(domain);
            var outcome = await dns.CheckAsync(actor.Org(), probe, ct);
            check = new DnsCheckDto(outcome.Status, outcome.Message, outcome.Expected, outcome.Resolved);
        }

        return TypedResults.Ok(new ProxySettingsUpdatedDto(await BuildAsync(db, options.Value, ct), applied, error, check));
    }

    private static async Task<ProxySettingsDto> BuildAsync(AetheraDbContext db, ProxyOptions options, CancellationToken ct)
    {
        var s = await ProxySettingsStore.GetAsync(db, options, ct);
        var serverId = await ProxySettingsStore.DashboardServerIdAsync(db, s.Dashboard, ct);
        var server = serverId is { } id ? await db.Servers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        var suggested = server is not null && DefaultDomainService.KnownBaseDomain(server is { WildcardDomain: null } ? server : CloneWithoutWildcard(server)) is { } sslip
            ? $"aethera.{sslip}" : null;
        var url = s.Dashboard.Domain is { Length: > 0 } d ? $"{(s.Dashboard.Https ? "https" : "http")}://{d}" : null;
        return new ProxySettingsDto(s.Acme.AcmeEmail, s.Acme.Staging, s.Dashboard.Domain, s.Dashboard.Https, serverId, url, suggested,
            s.Stored ? "settings" : "environment");
    }

    /// <summary>The suggestion is the server's sslip.io name even when it has a wildcard domain (that one needs its own DNS record first).</summary>
    private static Server CloneWithoutWildcard(Server s) => new() { OrganizationId = s.OrganizationId, Name = s.Name, Host = s.Host, PublicIp = s.PublicIp };
}
