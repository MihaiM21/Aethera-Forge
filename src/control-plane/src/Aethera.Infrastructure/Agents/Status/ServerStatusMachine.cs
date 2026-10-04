using Aethera.Domain;

namespace Aethera.Infrastructure.Agents.Status;

/// <summary>State of one axis in the derived server health view.</summary>
public enum AxisHealth
{
    /// <summary>Nothing observed yet, or the layer below hides it (see <see cref="AxisView.BlockedBy"/>).</summary>
    Unknown = 0,
    Available = 1,
    Unavailable = 2,

    /// <summary>Agent axis only: SSH-only server, no agent installed.</summary>
    NotInstalled = 3,
}

/// <summary>One axis of the failure model (spec section 44, ADR 0002): never a combined "online".</summary>
/// <param name="Axis"><c>server</c>, <c>agent</c>, <c>docker</c> or <c>application</c>.</param>
/// <param name="BlockedBy">The first failing layer above this one when this axis is shown as unknown because of it ("unknown (blocked by agent)").</param>
/// <param name="Since">When the underlying observation last changed; with <see cref="Stale"/> this is the "stale since" timestamp.</param>
/// <param name="Stale">True when the shown value is the last known one, not a current observation.</param>
public sealed record AxisView(string Axis, AxisHealth Health, string? BlockedBy, DateTimeOffset? Since, bool Stale, string? Detail = null);

/// <summary>Summary of the application axis: counts of workloads on the server.</summary>
public sealed record ApplicationSummary(int Total, int Unavailable, int Healthy, int Unknown);

/// <summary>The derived view of a server's health. The control-plane axis is only observable client-side and is not part of it.</summary>
public sealed record ServerHealth(AxisView Server, AxisView Agent, AxisView Docker, AxisView Application, string? FirstFailingLayer);

/// <summary>
/// Pure derivation of the five-axis failure model from the stored observations (ADR 0002 "Failure model"). Layers are evaluated in the
/// order server, agent, Docker, application and the problem is attributed to the <b>first failing layer</b>; the layers below it are
/// reported as <c>unknown (blocked by &lt;layer&gt;)</c> instead of falsely "down", keeping their last known value flagged stale.
/// </summary>
public static class ServerStatusMachine
{
    public const string ServerAxis = "server";
    public const string AgentAxis = "agent";
    public const string DockerAxis = "docker";
    public const string ApplicationAxis = "application";

    public static ServerHealth Derive(Server server, ApplicationSummary applications)
    {
        // Axis 1: the machine. Only the control plane's own reachability probe can say it is unavailable, and only while no agent session
        // exists (a live session is proof the machine answers; Server.RecordHeartbeat sets Reachable).
        var serverAxis = server.ReachabilityStatus switch
        {
            ReachabilityStatus.Reachable => new AxisView(ServerAxis, AxisHealth.Available, null, server.ReachabilityChangedAt, false),
            ReachabilityStatus.Unreachable => new AxisView(ServerAxis, AxisHealth.Unavailable, null, server.ReachabilityChangedAt, false, "The reachability probe fails."),
            _ => new AxisView(ServerAxis, AxisHealth.Unknown, null, server.ReachabilityChangedAt, false, "No reachability probe target is configured or no probe ran yet."),
        };
        if (serverAxis.Health == AxisHealth.Unavailable && server.AgentStatus == AgentStatus.Connected)
            serverAxis = new AxisView(ServerAxis, AxisHealth.Available, null, server.ReachabilityChangedAt, false); // a connected agent outranks an old probe result

        // Axis 2: the agent session.
        AxisView agentAxis;
        if (server.AgentStatus == AgentStatus.NotInstalled)
            agentAxis = new AxisView(AgentAxis, AxisHealth.NotInstalled, null, server.AgentStatusChangedAt, false);
        else if (serverAxis.Health == AxisHealth.Unavailable)
            agentAxis = Blocked(AgentAxis, ServerAxis, server.AgentStatusChangedAt, server.AgentStatus != AgentStatus.Unknown);
        else
            agentAxis = server.AgentStatus switch
            {
                AgentStatus.Connected => new AxisView(AgentAxis, AxisHealth.Available, null, server.AgentStatusChangedAt, false),
                AgentStatus.Unavailable => new AxisView(AgentAxis, AxisHealth.Unavailable, null, server.AgentStatusChangedAt, false,
                    serverAxis.Health == AxisHealth.Available ? "The host answers but no agent session exists." : "No agent session exists."),
                _ => new AxisView(AgentAxis, AxisHealth.Unknown, null, server.AgentStatusChangedAt, false),
            };

        // Axis 3: the Docker daemon, as reported by the agent. Only meaningful while the agent is connected.
        AxisView dockerAxis;
        if (agentAxis.Health is AxisHealth.NotInstalled)
            dockerAxis = server.DockerStatus == DockerStatus.Unknown
                ? new AxisView(DockerAxis, AxisHealth.Unknown, null, server.DockerStatusChangedAt, false, "Docker state needs the agent or SSH polling.")
                : FromDocker(server);
        else if (serverAxis.Health == AxisHealth.Unavailable)
            dockerAxis = Blocked(DockerAxis, ServerAxis, server.DockerStatusChangedAt, server.DockerStatus != DockerStatus.Unknown);
        else if (agentAxis.Health != AxisHealth.Available)
            dockerAxis = Blocked(DockerAxis, AgentAxis, server.DockerStatusChangedAt, server.DockerStatus != DockerStatus.Unknown);
        else
            dockerAxis = FromDocker(server);

        // Axis 4: the workloads. Attributed to the first failing layer above.
        AxisView appAxis;
        var blocker = serverAxis.Health == AxisHealth.Unavailable ? ServerAxis
            : agentAxis.Health is AxisHealth.Unavailable or AxisHealth.Unknown ? AgentAxis
            : dockerAxis.Health is AxisHealth.Unavailable ? DockerAxis
            : null;
        if (blocker is not null && agentAxis.Health != AxisHealth.NotInstalled)
            appAxis = new AxisView(ApplicationAxis, AxisHealth.Unknown, blocker, null, applications.Total > 0, $"{applications.Total} workload(s), last known state shown as stale.");
        else if (applications.Total == 0)
            appAxis = new AxisView(ApplicationAxis, AxisHealth.Unknown, null, null, false, "No workloads on this server.");
        else if (applications.Unavailable > 0)
            appAxis = new AxisView(ApplicationAxis, AxisHealth.Unavailable, null, null, false, $"{applications.Unavailable} of {applications.Total} workload(s) are not available.");
        else if (applications.Healthy == applications.Total)
            appAxis = new AxisView(ApplicationAxis, AxisHealth.Available, null, null, false);
        else
            appAxis = new AxisView(ApplicationAxis, AxisHealth.Unknown, null, null, false, $"{applications.Unknown} of {applications.Total} workload(s) have no known state.");

        var first = serverAxis.Health == AxisHealth.Unavailable ? ServerAxis
            : agentAxis.Health == AxisHealth.Unavailable ? AgentAxis
            : dockerAxis.Health == AxisHealth.Unavailable ? DockerAxis
            : appAxis.Health == AxisHealth.Unavailable ? ApplicationAxis
            : null;
        return new ServerHealth(serverAxis, agentAxis, dockerAxis, appAxis, first);
    }

    private static AxisView Blocked(string axis, string by, DateTimeOffset? since, bool hasLastKnown) =>
        new(axis, AxisHealth.Unknown, by, since, hasLastKnown, $"Unknown: blocked by {by}.");

    private static AxisView FromDocker(Server server) => server.DockerStatus switch
    {
        DockerStatus.Running => new AxisView(DockerAxis, AxisHealth.Available, null, server.DockerStatusChangedAt, false),
        DockerStatus.Unknown => new AxisView(DockerAxis, AxisHealth.Unknown, null, server.DockerStatusChangedAt, false),
        var other => new AxisView(DockerAxis, AxisHealth.Unavailable, null, server.DockerStatusChangedAt, false, $"The Docker daemon is {Camel(other)}."),
    };

    /// <summary>Counts the application axis input from workload rows (only workloads that should be running count as unavailable when stopped).</summary>
    public static ApplicationSummary Summarize(IEnumerable<(WorkloadStatus Status, DesiredState Desired)> workloads)
    {
        int total = 0, unavailable = 0, healthy = 0, unknown = 0;
        foreach (var (status, desired) in workloads)
        {
            total++;
            switch (status)
            {
                case WorkloadStatus.Running: healthy++; break;
                case WorkloadStatus.Unhealthy or WorkloadStatus.Failed: unavailable++; break;
                case WorkloadStatus.Stopped when desired == DesiredState.Running: unavailable++; break;
                case WorkloadStatus.Stopped or WorkloadStatus.NotDeployed: healthy++; break; // intended state, not a problem
                default: unknown++; break;
            }
        }

        return new ApplicationSummary(total, unavailable, healthy, unknown);
    }

    public static string Camel<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
