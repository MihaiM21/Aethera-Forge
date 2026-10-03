using Aethera.Api.Features.Resources.Workloads;
using Aethera.Api.Http.Errors;
using Aethera.Domain;

namespace Aethera.Api.Features.Resources.Trust;

/// <summary>
/// The API-level enforcement of the trust model (ADR 0006): Developers deploy applications normally, anything root-equivalent on a server
/// needs the Administrator role. Each check throws an <see cref="ApiProblemException"/>. Values that are <em>unchanged</em> by an update
/// (same host path, same port, same compose text) are not checked again, so a Developer can edit an application that an Administrator set up.
/// </summary>
public static class TrustChecks
{
    // ---- volume host paths ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Host-path bind mounts: Administrator only (403 <c>volume.host_path_requires_admin</c>), and even then never the denied directories
    /// (422 <c>volume.host_path_forbidden</c>). <paramref name="normalizedPath"/> is the output of <see cref="TrustPolicy.TryNormalizeHostPath"/>.
    /// </summary>
    public static void CheckHostPath(ICurrentActor actor, TrustPolicy trust, string normalizedPath, string pointer)
    {
        if (!actor.IsAdmin()) throw new ApiProblemException(TrustProblems.HostPathNeedsAdmin(pointer));
        if (trust.HostPathDenial(normalizedPath) is { } reason) throw new ApiProblemException(TrustProblems.HostPathDenied(pointer, reason));
    }

    // ---- published ports ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Published (host) ports: below 1024 is Administrator only (403 <c>port.privileged_requires_admin</c>); the ports Aethera itself uses are
    /// refused (422 <c>port.reserved</c>) unless the caller is an Administrator and passes <c>allowReserved</c> on the port.
    /// </summary>
    /// <param name="ports">The requested port list (from <c>/runtime/ports</c>).</param>
    /// <param name="current">The ports the workload already has; an unchanged (container port, protocol, published port) is not checked again.</param>
    public static void CheckPorts(ICurrentActor actor, TrustPolicy trust, IReadOnlyList<PortRequest>? ports, IEnumerable<WorkloadPort>? current = null)
    {
        if (ports is null) return;
        var admin = actor.IsAdmin();
        var existing = (current ?? []).Select(p => (p.ContainerPort, p.Protocol, p.PublishedPort)).ToHashSet();

        var privileged = new List<(string Pointer, int Port)>();
        var reserved = new List<(string Pointer, int Port)>();
        for (var i = 0; i < ports.Count; i++)
        {
            var request = ports[i];
            if (request.PublishedPort is not { } published || request.ContainerPort is not { } container) continue;
            var protocol = EnumText.Parse<PortProtocol>(request.Protocol) ?? PortProtocol.Tcp;
            if (existing.Contains((container, protocol, published))) continue;

            var pointer = $"/runtime/ports/{i}/publishedPort";
            if (trust.IsPrivilegedPort(published) && !admin) privileged.Add((pointer, published));
            if (trust.IsReservedPort(published) && !(admin && request.AllowReserved == true)) reserved.Add((pointer, published));
        }

        if (privileged.Count > 0) throw new ApiProblemException(TrustProblems.PrivilegedPort(privileged));
        if (reserved.Count > 0) throw new ApiProblemException(TrustProblems.ReservedPort(reserved, admin));
    }

    // ---- compose --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Inline compose content. Invalid YAML or a bomb: 422 <c>compose.invalid</c>. A Developer using any root-equivalent option (privileged,
    /// host namespaces, added capabilities, devices, unconfined security profiles, a cgroup parent, host bind mounts, ...): 403
    /// <c>compose.option_requires_admin</c> with the offending pointers. An Administrator may use them, but bind sources still pass the
    /// host-path denylist (422 <c>volume.host_path_forbidden</c>).
    /// </summary>
    /// <param name="field">JSON Pointer of the request property that holds the content (<c>/compose/inlineContent</c>).</param>
    /// <param name="stored">The content that is already stored; identical content is accepted as is.</param>
    public static void CheckCompose(ICurrentActor actor, TrustPolicy trust, string? content, string field, string? stored = null)
    {
        if (string.IsNullOrWhiteSpace(content) || content == stored) return;

        ComposeAnalysis analysis;
        try
        {
            analysis = ComposeInspector.Analyze(content, trust.ReservedPorts);
        }
        catch (ComposeInvalidException e)
        {
            throw new ApiProblemException(TrustProblems.ComposeBroken(field, e.Message));
        }

        if (!actor.IsAdmin())
        {
            if (analysis.RestrictedOptions.Count > 0)
                throw new ApiProblemException(TrustProblems.ComposeNeedsAdmin(field, analysis.RestrictedOptions));
            return;
        }

        var denied = analysis.BindSources
            .Select(b => (b.Pointer, Reason: BindSourceDenial(trust, b.Source)))
            .Where(b => b.Reason is not null)
            .Select(b => new ComposeFinding(b.Pointer, b.Reason!))
            .ToList();
        if (denied.Count > 0) throw new ApiProblemException(TrustProblems.ComposeHostPathDenied(field, denied));
    }

    /// <summary>Why an Administrator may not use this compose bind source, or null. Relative sources are resolved by the engine inside the project directory.</summary>
    private static string? BindSourceDenial(TrustPolicy trust, string source)
    {
        var text = source.Trim();
        if (text.Contains('$')) return "variables are not allowed in host paths";
        if (text.StartsWith('~')) return "the home directory of the host cannot be mounted";
        if (text.StartsWith('/'))
        {
            if (!TrustPolicy.TryNormalizeHostPath(text, out var normalized)) return "not a valid absolute path";
            return trust.HostPathDenial(normalized);
        }

        return text.Replace('\\', '/').Split('/').Contains("..") ? "a relative path must stay inside the project directory" : null;
    }
}
