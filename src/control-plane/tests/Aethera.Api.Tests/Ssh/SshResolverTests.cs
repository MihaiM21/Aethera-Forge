using Aethera.Api.Tests.Agents;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Transport;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Ssh;

/// <summary>
/// The resolver with the real agent and SSH transports: the agent first, SSH only when the server has SSH credentials and its fallback is
/// allowed, capability gaps fail fast with <c>transport.unsupported</c> (ADR 0002 "IServerTransport").
/// </summary>
public sealed class SshResolverTests : IAsyncLifetime
{
    private SshTestHost? _host;
    private SshTestHost Host => _host!;

    public async Task InitializeAsync() => _host = await SshTestHost.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private IServerTransportResolver Resolver => Host.Get<IServerTransportResolver>();

    private async Task<(Guid Id, FakeAgent? Agent)> ServerAsync(bool agentUp, bool credential = true, bool allowFallback = true, ServerLifecycle lifecycle = ServerLifecycle.Active)
    {
        var id = await Host.SeedServerAsync(withCredential: credential);
        await Host.WithDbAsync(async db =>
        {
            var server = await db.Servers.FirstAsync(s => s.Id == id);
            server.Lifecycle = lifecycle;
            await db.SaveChangesAsync();
            return 0;
        });
        if (!allowFallback) await Host.Get<SshSettingsStore>().SetAsync(SshSettingsStore.FallbackKey(id), new SshFallbackSetting(false), CancellationToken.None);

        FakeAgent? agent = null;
        if (agentUp)
        {
            agent = FakeAgent.Start(Host.Services, id);
            await GatewayFixture.EventuallyAsync(() => Task.FromResult(Host.Get<AgentSessionRegistry>().IsConnected(id)), "the agent session");
        }

        return (id, agent);
    }

    [RequiresDatabaseFact]
    public async Task A_connected_agent_always_wins_even_when_ssh_would_be_possible()
    {
        var (id, agent) = await ServerAsync(agentUp: true);
        await using var _ = agent!;
        var resolved = await Resolver.ResolveAsync(id, TransportCapabilities.ContainerOps | TransportCapabilities.Builds, CancellationToken.None);
        Assert.Equal(TransportKind.Agent, resolved.Kind);
        Assert.False(resolved.UsedFallback);
        Assert.IsType<AgentTransport>(resolved.Transport);
    }

    [RequiresDatabaseFact]
    public async Task Without_an_agent_but_with_credentials_ssh_stands_in_and_is_marked_as_the_fallback()
    {
        var (id, _) = await ServerAsync(agentUp: false);
        var resolved = await Resolver.ResolveAsync(id, TransportCapabilities.ContainerOps, CancellationToken.None);
        Assert.Equal(TransportKind.Ssh, resolved.Kind);
        Assert.True(resolved.UsedFallback);
        Assert.IsType<SshTransport>(resolved.Transport);
    }

    public static TheoryData<bool, bool, ServerLifecycle, string> Unavailable() => new()
    {
        { false, false, ServerLifecycle.Active, "no ssh credential" },
        { true, false, ServerLifecycle.Active, "fallback switched off" },
        { true, true, ServerLifecycle.Disabled, "server disabled" },
    };

    [RequiresDatabaseTheory, MemberData(nameof(Unavailable))]
    public async Task Without_an_agent_and_without_a_usable_ssh_fallback_the_server_is_unavailable(bool credential, bool allow, ServerLifecycle lifecycle, string why)
    {
        var (id, _) = await ServerAsync(agentUp: false, credential, allow, lifecycle);
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => Resolver.ResolveAsync(id, TransportCapabilities.ContainerOps, CancellationToken.None));
        Assert.True(ex.Code is TransportErrors.AgentUnavailable or TransportErrors.Unreachable, why);
        Assert.True(ex.Transient);
    }

    [RequiresDatabaseFact]
    public async Task A_capability_the_fallback_lacks_fails_fast_with_unsupported()
    {
        var (id, _) = await ServerAsync(agentUp: false);
        foreach (var missing in new[] { TransportCapabilities.PushEvents, TransportCapabilities.PushMetrics, TransportCapabilities.SelfUpdate, TransportCapabilities.PushEvents | TransportCapabilities.ContainerOps })
        {
            var ex = await Assert.ThrowsAsync<ServerTransportException>(() => Resolver.ResolveAsync(id, missing, CancellationToken.None));
            Assert.Equal(TransportErrors.Unsupported, ex.Code);
            Assert.Contains("Enable the agent on this server or use a build server", ex.Message);
        }

        // What it does have resolves.
        foreach (var capability in new[] { TransportCapabilities.Builds, TransportCapabilities.Compose, TransportCapabilities.LogFollow, TransportCapabilities.HealthProbe, TransportCapabilities.None })
            Assert.Equal(TransportKind.Ssh, (await Resolver.ResolveAsync(id, capability, CancellationToken.None)).Kind);
    }

    [RequiresDatabaseFact]
    public async Task Commands_run_through_the_resolver_report_that_they_used_the_fallback()
    {
        var (id, _) = await ServerAsync(agentUp: false);
        Host.Connector!.Connection.Handler = c => new RemoteResult(0, c.Line.Contains(" inspect ") ? """[{"Id":"x","Name":"/web","State":{"Status":"running"},"Config":{}}]""" : "", "");
        var result = await Resolver.ExecuteAsync<ContainerStartCommand, DockerContainer>(id, new ContainerStartCommand("web"), CommandOptions.For("k"), CancellationToken.None);

        Assert.Equal(TransportKind.Ssh, result.Transport);
        Assert.True(result.UsedFallback);
        Assert.True(result.Outcome.Succeeded);
    }

    [RequiresDatabaseFact]
    public async Task A_blocked_host_key_takes_ssh_out_of_the_resolution()
    {
        var id = await Host.SeedServerAsync(pinned: "SHA256:" + new string('Z', 43)); // the scripted server presents another key
        var first = await Assert.ThrowsAsync<ServerTransportException>(() =>
            Resolver.ExecuteAsync<ContainerStartCommand, DockerContainer>(id, new ContainerStartCommand("web"), CommandOptions.For("k"), CancellationToken.None));
        Assert.Equal(SshErrors.HostKeyChanged, first.Code);

        var second = await Assert.ThrowsAsync<ServerTransportException>(() => Resolver.ResolveAsync(id, TransportCapabilities.ContainerOps, CancellationToken.None));
        Assert.Equal(TransportErrors.AgentUnavailable, second.Code);
    }

    [RequiresDatabaseFact]
    public async Task The_fallback_policy_follows_credentials_the_switch_and_the_lifecycle()
    {
        var policy = Host.Get<ISshFallbackPolicy>();
        Assert.IsType<SshFallbackPolicy>(policy); // the real policy replaced the placeholder of WP2.2
        var (id, _) = await ServerAsync(agentUp: false);
        Assert.True(await policy.AllowsFallbackAsync(id, CancellationToken.None));

        await Host.Get<SshSettingsStore>().SetAsync(SshSettingsStore.FallbackKey(id), new SshFallbackSetting(false), CancellationToken.None);
        Assert.False(await policy.AllowsFallbackAsync(id, CancellationToken.None));
        await Host.Get<SshSettingsStore>().SetAsync(SshSettingsStore.FallbackKey(id), new SshFallbackSetting(true), CancellationToken.None);
        Assert.True(await policy.AllowsFallbackAsync(id, CancellationToken.None));

        Assert.False(await policy.AllowsFallbackAsync(Guid.NewGuid(), CancellationToken.None));
    }
}

/// <summary>A theory that needs the database, skipped like <see cref="RequiresDatabaseFactAttribute"/> when it is not configured.</summary>
public sealed class RequiresDatabaseTheoryAttribute : TheoryAttribute
{
    public RequiresDatabaseTheoryAttribute()
    {
        if (!TestDatabase.IsConfigured) Skip = $"{RequiresDatabaseFactAttribute.EnvironmentVariable} is not set.";
    }
}
