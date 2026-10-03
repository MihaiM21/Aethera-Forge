using System.Text.Json.Serialization;
using Aethera.Api.Http.Errors;
using Aethera.Domain;
using FluentValidation;

namespace Aethera.Api.Features.Resources.Workloads;

// ---- requests (enums arrive as strings so a bad value is a field-level 422 invalid_enum, not a 400) -------------------------------------

public sealed record PortRequest
{
    public int? ContainerPort { get; init; }
    public string? Protocol { get; init; }
    public int? PublishedPort { get; init; }
    public bool? IsHttp { get; init; }

    /// <summary>
    /// Administrators only: publish a host port that Aethera itself uses (22, 80, 443, 2375, 2376, 5080, 9443 by default) anyway. Not stored;
    /// repeat it whenever the port is (re)sent. See ADR 0006.
    /// </summary>
    public bool? AllowReserved { get; init; }
}

public sealed record ResourceLimitsRequest
{
    public double? CpuLimitCores { get; init; }
    public double? CpuReservationCores { get; init; }
    public long? MemoryLimitBytes { get; init; }
    public long? MemoryReservationBytes { get; init; }
    public int? PidsLimit { get; init; }
}

public sealed record HealthCheckRequest
{
    public string? Type { get; init; }
    public string? Path { get; init; }
    public int? Port { get; init; }
    public int? IntervalSeconds { get; init; }
    public int? TimeoutSeconds { get; init; }
    public int? Retries { get; init; }
    public int? StartPeriodSeconds { get; init; }
}

/// <summary>Runtime settings shared by applications and services.</summary>
public sealed record RuntimeRequest
{
    public string? RestartPolicy { get; init; }

    /// <summary><c>recreate</c> or <c>low-downtime</c>.</summary>
    public string? Strategy { get; init; }

    public List<PortRequest>? Ports { get; init; }
    public ResourceLimitsRequest? Resources { get; init; }
    public HealthCheckRequest? HealthCheck { get; init; }
}

// ---- responses ------------------------------------------------------------------------------------------------------------------------

public sealed record PortResponse(Guid Id, int ContainerPort, PortProtocol Protocol, int? PublishedPort, bool IsHttp);

public sealed record ResourceLimitsResponse(
    double? CpuLimitCores, double? CpuReservationCores, long? MemoryLimitBytes, long? MemoryReservationBytes, int? PidsLimit);

public sealed record HealthCheckResponse(
    HealthCheckType Type, string? Path, int? Port, int IntervalSeconds, int TimeoutSeconds, int Retries, int StartPeriodSeconds);

public sealed record RuntimeResponse(
    RestartPolicy RestartPolicy, string Strategy, IReadOnlyList<PortResponse> Ports, ResourceLimitsResponse Resources, HealthCheckResponse HealthCheck);

// ---- validation -----------------------------------------------------------------------------------------------------------------------

public sealed class PortRequestValidator : AbstractValidator<PortRequest>
{
    public PortRequestValidator()
    {
        RuleFor(p => p.ContainerPort).NotNull().InclusiveBetween(1, 65535);
        RuleFor(p => p.PublishedPort).InclusiveBetween(1, 65535).When(p => p.PublishedPort is not null);
        RuleFor(p => p.Protocol).MustBeEnum<PortRequest, PortProtocol>();
    }
}

public sealed class ResourceLimitsValidator : AbstractValidator<ResourceLimitsRequest>
{
    public const long MinMemoryBytes = 6L * 1024 * 1024;

    public ResourceLimitsValidator()
    {
        RuleFor(x => x.CpuLimitCores).GreaterThan(0).LessThanOrEqualTo(1024).When(x => x.CpuLimitCores is not null);
        RuleFor(x => x.CpuReservationCores).GreaterThan(0).LessThanOrEqualTo(1024).When(x => x.CpuReservationCores is not null);
        RuleFor(x => x.MemoryLimitBytes).GreaterThanOrEqualTo(MinMemoryBytes)
            .WithMessage("Must be at least 6291456 (6 MiB).").When(x => x.MemoryLimitBytes is not null);
        RuleFor(x => x.MemoryReservationBytes).GreaterThanOrEqualTo(MinMemoryBytes)
            .WithMessage("Must be at least 6291456 (6 MiB).").When(x => x.MemoryReservationBytes is not null);
        RuleFor(x => x.PidsLimit).GreaterThanOrEqualTo(1).When(x => x.PidsLimit is not null);
        RuleFor(x => x.CpuReservationCores).Must((x, v) => v is null || x.CpuLimitCores is null || v <= x.CpuLimitCores)
            .WithErrorCode("range").WithMessage("The reservation cannot exceed the limit.");
        RuleFor(x => x.MemoryReservationBytes).Must((x, v) => v is null || x.MemoryLimitBytes is null || v <= x.MemoryLimitBytes)
            .WithErrorCode("range").WithMessage("The reservation cannot exceed the limit.");
    }
}

public sealed class HealthCheckValidator : AbstractValidator<HealthCheckRequest>
{
    public HealthCheckValidator()
    {
        RuleFor(x => x.Type).MustBeEnum<HealthCheckRequest, HealthCheckType>();
        RuleFor(x => x.Path).MaximumLength(500).Must(p => p is null || p.StartsWith('/')).WithErrorCode("pattern").WithMessage("Must start with '/'.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
        RuleFor(x => x.IntervalSeconds).InclusiveBetween(1, 3600).When(x => x.IntervalSeconds is not null);
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(1, 600).When(x => x.TimeoutSeconds is not null);
        RuleFor(x => x.Retries).InclusiveBetween(1, 100).When(x => x.Retries is not null);
        RuleFor(x => x.StartPeriodSeconds).InclusiveBetween(0, 3600).When(x => x.StartPeriodSeconds is not null);
    }
}

public sealed class RuntimeRequestValidator : AbstractValidator<RuntimeRequest>
{
    public RuntimeRequestValidator()
    {
        RuleFor(x => x.RestartPolicy).MustBeEnum<RuntimeRequest, RestartPolicy>();
        RuleFor(x => x.Strategy).Must(s => s is null or DeploymentStrategies.Recreate or DeploymentStrategies.LowDowntime)
            .WithErrorCode("invalid_enum").WithMessage($"Must be '{DeploymentStrategies.Recreate}' or '{DeploymentStrategies.LowDowntime}'.");
        RuleFor(x => x.Ports).Must(HaveUniquePorts).WithErrorCode("not_unique").WithMessage("Each container port and protocol may appear once.");
        RuleForEach(x => x.Ports).SetValidator(new PortRequestValidator());
        RuleFor(x => x.Resources).SetValidator(new ResourceLimitsValidator()!);
        RuleFor(x => x.HealthCheck).SetValidator(new HealthCheckValidator()!);
    }

    private static bool HaveUniquePorts(List<PortRequest>? ports) =>
        ports is null || ports.Where(p => p.ContainerPort is not null)
            .GroupBy(p => (p.ContainerPort, Protocol: EnumText.Parse<PortProtocol>(p.Protocol) ?? PortProtocol.Tcp)).All(g => g.Count() == 1);
}

// ---- mapping --------------------------------------------------------------------------------------------------------------------------

public static class RuntimeMapper
{
    public static RuntimeResponse ToResponse(Workload w) => new(
        w.Runtime.RestartPolicy,
        w.Runtime.DeploymentStrategy,
        w.Ports.OrderBy(p => p.ContainerPort).ThenBy(p => p.Protocol).Select(p => new PortResponse(p.Id, p.ContainerPort, p.Protocol, p.PublishedPort, p.IsHttp)).ToList(),
        new ResourceLimitsResponse(w.Runtime.CpuLimit, w.Runtime.CpuReservation, w.Runtime.MemoryLimitBytes, w.Runtime.MemoryReservationBytes, w.Runtime.PidsLimit),
        new HealthCheckResponse(w.Runtime.HealthCheck.Type, w.Runtime.HealthCheck.Path, w.Runtime.HealthCheck.Port, w.Runtime.HealthCheck.IntervalSeconds,
            w.Runtime.HealthCheck.TimeoutSeconds, w.Runtime.HealthCheck.Retries, w.Runtime.HealthCheck.StartPeriodSeconds));

    /// <summary>
    /// Applies the runtime request to the workload. <paramref name="has"/> says whether a (dotted, camelCase, relative to <c>runtime</c>)
    /// property is part of the request: always true on create (an absent value means the default), the merge-patch presence test on PATCH
    /// (an explicit null resets to the default).
    /// </summary>
    public static void Apply(Workload workload, RuntimeRequest? request, Func<string, bool> has)
    {
        if (request is null) return;
        var runtime = workload.Runtime;
        var health = runtime.HealthCheck;
        var defaults = new RuntimeConfig();

        if (has("restartPolicy")) runtime.RestartPolicy = EnumText.Parse<RestartPolicy>(request.RestartPolicy) ?? defaults.RestartPolicy;
        if (has("strategy")) runtime.DeploymentStrategy = request.Strategy ?? defaults.DeploymentStrategy;

        var r = request.Resources;
        if (has("resources.cpuLimitCores")) runtime.CpuLimit = r?.CpuLimitCores;
        if (has("resources.cpuReservationCores")) runtime.CpuReservation = r?.CpuReservationCores;
        if (has("resources.memoryLimitBytes")) runtime.MemoryLimitBytes = r?.MemoryLimitBytes;
        if (has("resources.memoryReservationBytes")) runtime.MemoryReservationBytes = r?.MemoryReservationBytes;
        if (has("resources.pidsLimit")) runtime.PidsLimit = r?.PidsLimit;

        var h = request.HealthCheck;
        var healthDefaults = defaults.HealthCheck;
        if (has("healthCheck.type")) health.Type = EnumText.Parse<HealthCheckType>(h?.Type) ?? healthDefaults.Type;
        if (has("healthCheck.path")) health.Path = h?.Path;
        if (has("healthCheck.port")) health.Port = h?.Port;
        if (has("healthCheck.intervalSeconds")) health.IntervalSeconds = h?.IntervalSeconds ?? healthDefaults.IntervalSeconds;
        if (has("healthCheck.timeoutSeconds")) health.TimeoutSeconds = h?.TimeoutSeconds ?? healthDefaults.TimeoutSeconds;
        if (has("healthCheck.retries")) health.Retries = h?.Retries ?? healthDefaults.Retries;
        if (has("healthCheck.startPeriodSeconds")) health.StartPeriodSeconds = h?.StartPeriodSeconds ?? healthDefaults.StartPeriodSeconds;

        if (has("ports")) ApplyPorts(workload, request.Ports ?? []);
    }

    /// <summary>The presence test for a runtime request on create: a property counts when the request gives it a value.</summary>
    public static Func<string, bool> PresentIn(RuntimeRequest r) => path => path switch
    {
        "restartPolicy" => r.RestartPolicy is not null,
        "strategy" => r.Strategy is not null,
        "ports" => r.Ports is not null,
        "resources.cpuLimitCores" => r.Resources?.CpuLimitCores is not null,
        "resources.cpuReservationCores" => r.Resources?.CpuReservationCores is not null,
        "resources.memoryLimitBytes" => r.Resources?.MemoryLimitBytes is not null,
        "resources.memoryReservationBytes" => r.Resources?.MemoryReservationBytes is not null,
        "resources.pidsLimit" => r.Resources?.PidsLimit is not null,
        "healthCheck.type" => r.HealthCheck?.Type is not null,
        "healthCheck.path" => r.HealthCheck?.Path is not null,
        "healthCheck.port" => r.HealthCheck?.Port is not null,
        "healthCheck.intervalSeconds" => r.HealthCheck?.IntervalSeconds is not null,
        "healthCheck.timeoutSeconds" => r.HealthCheck?.TimeoutSeconds is not null,
        "healthCheck.retries" => r.HealthCheck?.Retries is not null,
        "healthCheck.startPeriodSeconds" => r.HealthCheck?.StartPeriodSeconds is not null,
        _ => false,
    };

    /// <summary>Replaces the port list: existing (port, protocol) pairs are updated in place, missing ones removed, new ones added.</summary>
    public static void ApplyPorts(Workload workload, IReadOnlyList<PortRequest> ports)
    {
        var wanted = ports
            .Where(p => p.ContainerPort is not null)
            .Select(p => (Port: p.ContainerPort!.Value, Protocol: EnumText.Parse<PortProtocol>(p.Protocol) ?? PortProtocol.Tcp, Request: p))
            .ToList();

        foreach (var existing in workload.Ports.ToList())
        {
            if (!wanted.Any(w => w.Port == existing.ContainerPort && w.Protocol == existing.Protocol)) workload.Ports.Remove(existing);
        }

        foreach (var (port, protocol, request) in wanted)
        {
            var current = workload.Ports.FirstOrDefault(p => p.ContainerPort == port && p.Protocol == protocol);
            if (current is null)
            {
                workload.Ports.Add(new WorkloadPort { WorkloadId = workload.Id, ContainerPort = port, Protocol = protocol });
                current = workload.Ports[^1];
            }

            current.PublishedPort = request.PublishedPort;
            current.IsHttp = request.IsHttp ?? false;
        }
    }
}
