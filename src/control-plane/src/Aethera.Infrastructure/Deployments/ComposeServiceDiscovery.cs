using System.Globalization;
using Aethera.Domain.Transport;
using Aethera.Engine.Deployments;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Aethera.Infrastructure.Deployments;

/// <summary>The compose file of a deployment could not be inspected; carries the failure code stored on the deployment.</summary>
public sealed class ComposeDiscoveryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Which services a compose file has and which ports they declare (ADR 0010), so the override can put the environment network and the runtime
/// variables on every service, and a domain's service is checked before anything starts. A file in the repository is read by the agent
/// (<see cref="ComposeInspectCommand"/>); an inline file is read here.
/// </summary>
public static class ComposeServiceDiscovery
{
    /// <summary>
    /// The services of an inline compose file in file order, or null when it cannot be read (the API validates inline files when they are saved,
    /// so a deployment then proceeds without the service list and the agent reports the real error).
    /// </summary>
    public static IReadOnlyList<ComposeServiceDefinition>? FromInline(string content)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(content));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;
            if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var node) || node is not YamlMappingNode services) return null;

            var result = new List<ComposeServiceDefinition>();
            foreach (var (nameNode, serviceNode) in services.Children)
            {
                if (nameNode is not YamlScalarNode { Value: { Length: > 0 } name } || serviceNode is not YamlMappingNode service) return null;
                var ports = new List<int>();
                if (service.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode) && portsNode is YamlSequenceNode portList)
                    foreach (var item in portList) AddPort(ports, ContainerPortOf(item));
                if (service.Children.TryGetValue(new YamlScalarNode("expose"), out var exposeNode) && exposeNode is YamlSequenceNode exposeList)
                    foreach (var item in exposeList) AddPort(ports, ContainerPortOf(item));
                var image = service.Children.TryGetValue(new YamlScalarNode("image"), out var imageNode) && imageNode is YamlScalarNode { Value: { } i } ? i : "";
                var networkMode = service.Children.TryGetValue(new YamlScalarNode("network_mode"), out var modeNode) && modeNode is YamlScalarNode { Value: { Length: > 0 } m } ? m : null;
                result.Add(new ComposeServiceDefinition(name, image, service.Children.ContainsKey(new YamlScalarNode("build")), ports, networkMode));
            }

            return result;
        }
        catch (Exception e) when (e is YamlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static void AddPort(List<int> ports, int? port)
    {
        if (port is { } p && !ports.Contains(p)) ports.Add(p);
    }

    /// <summary>The container port of a <c>ports</c> or <c>expose</c> entry: <c>80</c>, <c>8080:80</c>, <c>127.0.0.1:8080:80/tcp</c> or <c>{ target: 80 }</c>.</summary>
    private static int? ContainerPortOf(YamlNode item)
    {
        if (item is YamlMappingNode map)
            return map.Children.TryGetValue(new YamlScalarNode("target"), out var target) && target is YamlScalarNode t
                   && int.TryParse(t.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
        if (item is not YamlScalarNode { Value: { } text }) return null;
        var spec = text.Split('/')[0].Split(':')[^1];
        return int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 0 and <= 65535 ? number : null;
    }

    /// <summary>
    /// Reads the file of a repository-backed compose application through the agent (a clone and <c>docker compose config</c> on the server).
    /// A file that breaks the policy fails here with the violations, before a build is started.
    /// </summary>
    /// <exception cref="ComposeDiscoveryException">The agent cannot inspect (too old), the checkout or the file failed, or the file has violations.</exception>
    public static async Task<ComposeInspection> InspectAsync(
        IServerTransportResolver resolver, Guid serverId, ComposeProjectSpec project, CommandOptions options, CancellationToken ct)
    {
        ResolvedOutcome<ComposeInspection> resolved;
        try
        {
            resolved = await resolver.ExecuteAsync<ComposeInspectCommand, ComposeInspection>(serverId, new ComposeInspectCommand(project), options, ct);
        }
        catch (ServerTransportException ex) when (ex.Code == TransportErrors.Unsupported)
        {
            throw new ComposeDiscoveryException("compose.agent_outdated", "The agent on this server is too old to deploy a compose file from the repository: update the agent.");
        }

        if (!resolved.Outcome.Succeeded)
            throw new ComposeDiscoveryException("compose.inspect_failed",
                $"The compose file could not be read from the repository: {resolved.Outcome.ErrorMessage ?? resolved.Outcome.Status.ToString()}");
        var inspection = resolved.Outcome.EnsureSucceeded();
        if (inspection.Violations.Count > 0)
            throw new ComposeDiscoveryException("compose.policy_violation", "The compose file is not allowed: " + string.Join("; ", inspection.Violations));
        return inspection;
    }
}
