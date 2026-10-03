using Aethera.Domain;
using Aethera.Infrastructure.Agents.Status;
using FluentValidation;

namespace Aethera.Api.Features.Agents;

// ---- join tokens / CA ----------------------------------------------------------------------------------------------------------------

public sealed record CreateJoinTokenRequest
{
    /// <summary>Lifetime in minutes: 1 to 1440 (24 hours). Default 60.</summary>
    public int? TtlMinutes { get; init; }
}

public sealed class CreateJoinTokenValidator : AbstractValidator<CreateJoinTokenRequest>
{
    public CreateJoinTokenValidator() => RuleFor(x => x.TtlMinutes).InclusiveBetween(1, 24 * 60).When(x => x.TtlMinutes is not null);
}

/// <summary>A new join token. <c>token</c> is shown here and never again: only its hash is stored.</summary>
/// <param name="Endpoint"><c>host:port</c> the agent dials.</param>
/// <param name="CaFingerprintSha256">The pin for the agent's first contact (<c>--ca-sha256</c>).</param>
/// <param name="InstallCommand">Ready to paste on the new server; contains the token.</param>
public sealed record JoinTokenResponse(
    Guid Id, Guid ServerId, string Token, DateTimeOffset ExpiresAt, string Endpoint, string CaFingerprintSha256, string InstallCommand);

/// <summary>A join token without its secret. <c>state</c> is <c>active</c>, <c>used</c>, <c>expired</c> or <c>revoked</c>.</summary>
public sealed record JoinTokenSummary(Guid Id, string State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt, Guid? CreatedByUserId);

public sealed record AgentCaResponse(string CertificatePem, string FingerprintSha256, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Endpoint);

// ---- status ----------------------------------------------------------------------------------------------------------------------------

/// <summary>One axis of the failure model. <c>blockedBy</c> names the first failing layer above when this axis is shown as unknown because of it.</summary>
public sealed record AxisResponse(string Axis, AxisHealth Health, string? BlockedBy, DateTimeOffset? Since, bool Stale, string? Detail);

/// <summary>The live agent stream, if any.</summary>
public sealed record AgentSessionResponse(
    string SessionId, string AgentVersion, DateTimeOffset ConnectedAt, double? RttMilliseconds, double? ClockSkewSeconds, IReadOnlyList<string> Capabilities);

public sealed record ApplicationSummaryResponse(int Total, int Unavailable, int Healthy, int Unknown);

/// <summary>
/// Server health as separate axes (spec section 44, ADR 0002): never a combined "online". The "control plane unavailable" axis is observable
/// only in the client and is not part of this response. <c>firstFailingLayer</c> is the layer the problem is attributed to.
/// </summary>
public sealed record ServerHealthResponse(
    Guid ServerId, AxisResponse Server, AxisResponse Agent, AxisResponse Docker, AxisResponse Application, string? FirstFailingLayer,
    ApplicationSummaryResponse Applications, AgentSessionResponse? Session, DateTimeOffset ObservedAt);

public sealed record ResourceEventResponse(Guid Id, string Kind, string? Axis, string? OldValue, string? NewValue, string? Detail, DateTimeOffset OccurredAt);

// ---- metrics ---------------------------------------------------------------------------------------------------------------------------

/// <summary>One metric sample. Network counters are cumulative; the per-second rates are derived from consecutive points (null at the first point or after a counter reset).</summary>
public sealed record MetricPointResponse(
    DateTimeOffset Timestamp, double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, long? DiskUsedBytes, long? DiskTotalBytes,
    long? NetRxBytes, long? NetTxBytes, double? NetRxBytesPerSecond, double? NetTxBytesPerSecond, double? Load1, double? Load5, double? Load15);

public sealed record ContainerMetricResponse(
    string ContainerId, Guid? WorkloadId, DateTimeOffset Timestamp, double? CpuPercent, long? MemoryUsedBytes, long? MemoryLimitBytes, long? NetRxBytes, long? NetTxBytes);

/// <param name="Stale">True when the newest sample is older than a minute (the agent is offline or not reporting).</param>
public sealed record LatestMetricsResponse(Guid ServerId, MetricPointResponse? Host, IReadOnlyList<ContainerMetricResponse> Containers, DateTimeOffset? ObservedAt, bool Stale);

/// <param name="Resolution"><c>raw</c> (10 s, kept 24 h), <c>fiveMinutes</c> or <c>oneHour</c>.</param>
public sealed record MetricSeriesResponse(
    Guid ServerId, string Resolution, DateTimeOffset From, DateTimeOffset To, string? ContainerId, IReadOnlyList<MetricPointResponse> Points);

// ---- discovery -------------------------------------------------------------------------------------------------------------------------

public sealed record ServerFactsResponse(
    string? Os, string? OsVersion, string? Kernel, string? Architecture, string? CpuModel, int? CpuCores, long? MemoryBytes, long? DiskBytes, string? DockerVersion, DateTimeOffset? DiscoveredAt);

/// <param name="Report">The last full discovery report; null before the first one.</param>
/// <param name="Stale">True when no agent session exists, so the report may be outdated.</param>
public sealed record DiscoveryResponse(
    Guid ServerId, ServerFactsResponse Facts, Aethera.Domain.Transport.DiscoveryInfo? Report, DateTimeOffset? StoredAt, bool Stale);

// ---- maintenance -----------------------------------------------------------------------------------------------------------------------

/// <summary>`docker system prune` scopes. At least one must be set. <c>volumes</c> deletes data and needs <c>?confirm=&lt;server name&gt;</c>.</summary>
public sealed record PruneRequest
{
    public bool StoppedContainers { get; init; }
    public bool DanglingImages { get; init; }
    public bool UnusedImages { get; init; }
    public bool UnusedNetworks { get; init; }
    public bool BuildCache { get; init; }
    public bool Volumes { get; init; }

    /// <summary>Only prune objects older than this many hours.</summary>
    public int? OlderThanHours { get; init; }
}

public sealed class PruneRequestValidator : AbstractValidator<PruneRequest>
{
    public PruneRequestValidator()
    {
        RuleFor(x => x).Must(x => x.StoppedContainers || x.DanglingImages || x.UnusedImages || x.UnusedNetworks || x.BuildCache || x.Volumes)
            .WithName("scope").WithErrorCode("required").WithMessage("Select at least one prune scope.");
        RuleFor(x => x.OlderThanHours).InclusiveBetween(1, 24 * 365).When(x => x.OlderThanHours is not null);
    }
}
