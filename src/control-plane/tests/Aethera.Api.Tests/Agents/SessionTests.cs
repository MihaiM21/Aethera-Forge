using Aethera.Agent.V1;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Agents.Transport;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using DomainDockerStatus = Aethera.Domain.DockerStatus;
using ProtoDockerStatus = Aethera.Agent.V1.DockerStatus;

namespace Aethera.Api.Tests.Agents;

/// <summary>The Connect stream (ADR 0002 "The Connect stream"), driven by a scripted agent: Hello/Welcome, liveness, supersede, ingestion.</summary>
[Collection(GatewayCollection.Name)]
public sealed class SessionTests(GatewayFixture fixture)
{
    private AgentSessionRegistry Registry => fixture.Get<AgentSessionRegistry>();

    [RequiresDatabaseFact]
    public async Task Hello_is_answered_with_Welcome_and_the_server_becomes_connected()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, hello => hello.AgentVersion = "0.3.1");

        var welcome = await agent.ExpectWelcomeAsync();
        Assert.Equal(serverId.ToString("D"), welcome.ServerId);
        Assert.NotEmpty(welcome.SessionId);
        Assert.Equal(TimeSpan.FromSeconds(0.3), welcome.HeartbeatInterval.ToTimeSpan());
        Assert.Equal(200UL, welcome.LogInitialWindowBytes);
        Assert.Equal(1000U, welcome.LogChunkMaxBytes);
        Assert.NotEmpty(welcome.ConfigVersion);
        Assert.InRange((welcome.ServerTime.ToDateTimeOffset() - DateTimeOffset.UtcNow).Duration().TotalSeconds, 0, 5);

        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).AgentStatus == AgentStatus.Connected, "agent status connected");
        var server = await fixture.LoadServerAsync(serverId);
        Assert.Equal("0.3.1", server.AgentVersion);
        Assert.Equal(ReachabilityStatus.Reachable, server.ReachabilityStatus); // a live stream proves the machine answers
        Assert.NotNull(server.LastHeartbeatAt);
        Assert.True(Registry.IsConnected(serverId));

        var transitions = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Kind == "status.changed").ToListAsync());
        Assert.Contains(transitions, e => e.Axis == "agent" && e.OldValue == "unknown" && e.NewValue == "connected");
    }

    [RequiresDatabaseFact]
    public async Task A_hello_naming_another_server_is_a_protocol_violation()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, hello => hello.ServerId = Guid.NewGuid().ToString("D"));

        var goodbye = await agent.NextDisconnectAsync();
        Assert.Equal(DisconnectReason.ProtocolViolation, goodbye.Reason);
        await agent.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Registry.IsConnected(serverId));
        Assert.NotEqual(AgentStatus.Connected, (await fixture.LoadServerAsync(serverId)).AgentStatus);
    }

    [RequiresDatabaseFact]
    public async Task The_first_message_must_be_Hello()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false, sendHello: false);
        agent.Heartbeat();
        Assert.Equal(DisconnectReason.ProtocolViolation, (await agent.NextDisconnectAsync()).Reason);
    }

    [RequiresDatabaseFact]
    public async Task A_silent_new_stream_is_closed_after_the_hello_timeout()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false, sendHello: false);
        Assert.Equal(DisconnectReason.ProtocolViolation, (await agent.NextDisconnectAsync()).Reason);
    }

    [RequiresDatabaseFact]
    public async Task A_second_hello_is_a_protocol_violation()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        agent.Send(new AgentMessage { Hello = new Hello { ServerId = serverId.ToString("D") } });
        Assert.Equal(DisconnectReason.ProtocolViolation, (await agent.NextDisconnectAsync()).Reason);
    }

    [RequiresDatabaseFact]
    public async Task An_old_protocol_revision_is_told_to_upgrade()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, hello => hello.ProtocolVersion = 0);
        Assert.Equal(DisconnectReason.UpgradeRequired, (await agent.NextDisconnectAsync()).Reason);
    }

    [RequiresDatabaseFact]
    public async Task A_deleted_server_is_told_it_was_revoked()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await fixture.WithDbAsync(async db =>
        {
            (await db.Servers.SingleAsync(s => s.Id == serverId)).MarkDeleted(DateTimeOffset.UtcNow);
            return await db.SaveChangesAsync();
        });
        await using var agent = FakeAgent.Start(fixture, serverId);
        Assert.Equal(DisconnectReason.Revoked, (await agent.NextDisconnectAsync()).Reason);
    }

    [RequiresDatabaseFact]
    public async Task A_newer_stream_supersedes_the_older_one_without_flapping_the_status()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var first = FakeAgent.Start(fixture, serverId);
        await first.ExpectWelcomeAsync();
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).AgentStatus == AgentStatus.Connected, "first connected");

        await using var second = FakeAgent.Start(fixture, serverId);
        await second.ExpectWelcomeAsync();

        Assert.Equal(DisconnectReason.Superseded, (await first.NextDisconnectAsync()).Reason);
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.Equal(AgentStatus.Connected, (await fixture.LoadServerAsync(serverId)).AgentStatus); // the old stream ending must not mark the server down
        Assert.Equal(second.Welcome!.SessionId, Registry.Session(serverId)!.SessionId);

        var transitions = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Axis == "agent").ToListAsync());
        Assert.DoesNotContain(transitions, t => t.NewValue == "unavailable");
    }

    [RequiresDatabaseFact]
    public async Task Three_missed_heartbeats_close_the_stream_with_a_timeout()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false);
        await agent.ExpectWelcomeAsync();

        var started = DateTime.UtcNow;
        var goodbye = await agent.NextDisconnectAsync(8000);
        Assert.Equal(DisconnectReason.HeartbeatTimeout, goodbye.Reason);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.7, 4); // 3 x 0.3 s of silence

        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).AgentStatus == AgentStatus.Unavailable, "agent status unavailable");
        Assert.False(Registry.IsConnected(serverId));
        var transitions = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Axis == "agent").ToListAsync());
        Assert.Contains(transitions, t => t.NewValue == "unavailable" && t.Detail == "heartbeat timeout");
    }

    [RequiresDatabaseFact]
    public async Task Any_message_counts_as_proof_of_life_and_idle_streams_are_pinged()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false);
        await agent.ExpectWelcomeAsync();

        // Silence for longer than the ping interval (0.5 s) but shorter than the timeout (0.9 s) draws a Ping ...
        var ping = await agent.NextAsync(m => m.Ping);
        agent.Send(new AgentMessage { Pong = new Pong { Nonce = ping.Nonce, PingSentAt = ping.SentAt } });
        // ... and keeping the stream busy with something other than heartbeats (a metrics report) prevents the timeout.
        for (var i = 0; i < 12; i++)
        {
            agent.Send(new AgentMessage { Metrics = new MetricsReport { Host = new HostMetrics { CpuPercent = 1 } } });
            await Task.Delay(100);
        }

        Assert.True(Registry.IsConnected(serverId));
        await GatewayFixture.EventuallyAsync(() => Task.FromResult(Registry.Session(serverId)?.LastRtt is not null), "round trip time measured");
    }

    [RequiresDatabaseFact]
    public async Task A_dropped_stream_marks_the_agent_unavailable_at_once()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).AgentStatus == AgentStatus.Connected, "connected");

        agent.Drop();
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).AgentStatus == AgentStatus.Unavailable, "unavailable after the drop", 2000);
        var server = await fixture.LoadServerAsync(serverId);
        Assert.NotNull(server.AgentStatusChangedAt);
    }

    [RequiresDatabaseFact]
    public async Task The_docker_axis_follows_heartbeats_and_each_change_is_an_event()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false);
        await agent.ExpectWelcomeAsync();

        agent.Heartbeat(ProtoDockerStatus.Running);
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).DockerStatus == DomainDockerStatus.Running, "docker running");
        agent.Heartbeat(ProtoDockerStatus.Stopped);
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).DockerStatus == DomainDockerStatus.Stopped, "docker stopped");

        var events = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Axis == "docker").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["running", "stopped"], events.Select(e => e.NewValue));
        Assert.Equal("running", events[1].OldValue);
        Assert.Equal(AgentStatus.Connected, (await fixture.LoadServerAsync(serverId)).AgentStatus); // docker down is not agent down
    }

    [RequiresDatabaseFact]
    public async Task The_registry_can_close_a_live_stream_as_revoked()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        Assert.True(Registry.Disconnect(serverId, DisconnectReason.Revoked, "bye"));
        var goodbye = await agent.NextDisconnectAsync();
        Assert.Equal(DisconnectReason.Revoked, goodbye.Reason);
        Assert.False(Registry.Disconnect(Guid.NewGuid(), DisconnectReason.Revoked, "nobody there"));
    }

    [RequiresDatabaseFact]
    public async Task A_certificate_close_to_expiry_draws_a_rotation_hint()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, certNotAfter: DateTimeOffset.UtcNow.AddDays(3));
        await agent.ExpectWelcomeAsync();
        var hint = await agent.NextAsync(m => m.CertRotation);
        Assert.InRange((hint.CurrentNotAfter.ToDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 2.9, 3.1);
        Assert.True(hint.RenewBy.ToDateTimeOffset() < hint.CurrentNotAfter.ToDateTimeOffset());
    }

    [RequiresDatabaseFact]
    public async Task A_healthy_certificate_draws_no_hint()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        Assert.DoesNotContain(await agent.DrainAsync(), m => m.CertRotation is not null);
    }

    [RequiresDatabaseFact]
    public async Task Reconnecting_with_a_renewed_certificate_revokes_the_one_it_replaced()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        using var k1 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var k2 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var older = await PkiHelpers.IssueAsync(fixture, serverId, k1, now: DateTimeOffset.UtcNow.AddDays(-20));
        var newer = await PkiHelpers.IssueAsync(fixture, serverId, k2);

        await using var agent = FakeAgent.Start(fixture, serverId, serial: newer.Serial);
        await agent.ExpectWelcomeAsync();

        await GatewayFixture.EventuallyAsync(async () =>
            (await fixture.WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == older.Serial))).RevokedAt is not null, "the old certificate revoked");
        Assert.True(fixture.Get<IInternalCa>().IsRevoked(older.Serial));
        var current = await fixture.WithDbAsync(db => db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Serial == newer.Serial));
        Assert.Null(current.RevokedAt);
        Assert.Equal(newer.Serial, (await fixture.LoadServerAsync(serverId)).CertSerial);
    }

    // ---- ingestion -------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Metrics_reports_become_host_and_container_samples()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();

        var report = new MetricsReport
        {
            CollectedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Host = new HostMetrics { CpuPercent = 33, MemoryTotalBytes = 1000, MemoryUsedBytes = 250, Load1 = 0.5 },
        };
        report.Containers.Add(new ContainerMetrics { ContainerId = "c1", CpuPercent = 150, MemoryUsedBytes = 10 });
        agent.Send(new AgentMessage { Metrics = report });

        await GatewayFixture.EventuallyAsync(async () => await fixture.WithDbAsync(db => db.MetricSamples.CountAsync(m => m.ServerId == serverId)) == 2, "two samples");
        var rows = await fixture.WithDbAsync(db => db.MetricSamples.AsNoTracking().Where(m => m.ServerId == serverId).ToListAsync());
        Assert.Equal(33, rows.Single(r => r.ContainerId is null).CpuPercent);
        Assert.Equal(150, rows.Single(r => r.ContainerId == "c1").CpuPercent);
        Assert.All(rows, r => Assert.Equal(MetricResolution.Raw, r.Resolution));
    }

    [RequiresDatabaseFact]
    public async Task A_discovery_report_updates_facts_docker_status_and_is_stored()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false);
        await agent.ExpectWelcomeAsync();

        var report = new DiscoveryReport
        {
            CollectedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Host = new HostFacts { Hostname = "box", OsName = "Debian", OsVersion = "12", KernelVersion = "6.1", Architecture = "arm64", CpuModel = "Neoverse", CpuCoresLogical = 8, MemoryTotalBytes = 16L << 30 },
            Docker = new DockerInfo { Status = ProtoDockerStatus.Running, Version = "27.3.1", ContainersRunning = 3 },
            Disks = { new DiskUsage { MountPoint = "/", TotalBytes = 500L << 30, UsedBytes = 100L << 30 }, new DiskUsage { MountPoint = "/data", TotalBytes = 900L << 30 } },
        };
        report.Containers.Add(new ContainerInfo { Id = "c1", Name = "web", Image = "nginx", State = ContainerState.Running, InspectJson = ByteString.CopyFromUtf8("{\"Env\":[\"PASSWORD=raw\"]}") });
        report.Tools.Add(new Aethera.Agent.V1.ToolInfo { Name = "git", Version = "2.43" });
        agent.Send(new AgentMessage { Discovery = report });

        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).Facts.DockerVersion == "27.3.1", "facts updated");
        var server = await fixture.LoadServerAsync(serverId);
        Assert.Equal("Debian", server.Facts.Os);
        Assert.Equal("arm64", server.Facts.Architecture);
        Assert.Equal(8, server.Facts.CpuCores);
        Assert.Equal(500L << 30, server.Facts.DiskBytes); // the root file system, not the largest disk
        Assert.Equal(DomainDockerStatus.Running, server.DockerStatus);

        var stored = await fixture.Get<AgentDiscoveryStore>().LoadAsync(serverId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("27.3.1", stored.Info.Docker.Version);
        var container = Assert.Single(stored.Info.Containers);
        Assert.Equal("web", container.Name);
        Assert.Null(container.InspectJson); // raw inspect output is not kept
        Assert.Equal("git", Assert.Single(stored.Info.Tools).Name);
        var blob = await fixture.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync(s => s.Key == AgentDiscoveryStore.Key(serverId)));
        Assert.DoesNotContain("PASSWORD", blob.ValueJson);
    }

    [RequiresDatabaseFact]
    public async Task Container_events_are_recorded_once_and_published()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var workloadId = await SeedWorkloadAsync(serverId);
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();

        var received = new List<ServerEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = Task.Run(async () =>
        {
            await foreach (var e in fixture.Transport.SubscribeEventsAsync(serverId, cts.Token))
            {
                received.Add(e);
                if (received.Count(r => r.Kind == ServerEventKind.ContainerDied) == 1) return;
            }
        });
        await Task.Delay(100);

        var notice = new EventNotice { EventId = "ev-1", Type = EventType.ContainerDied, OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), ContainerId = "c1", ContainerName = "web", ExitCode = 137, OomKilled = true };
        notice.Labels["aethera.application.id"] = workloadId.ToString();
        agent.Send(new AgentMessage { Event = notice });
        agent.Send(new AgentMessage { Event = notice }); // replayed after a reconnect: must not be recorded twice
        await subscriber.WaitAsync(TimeSpan.FromSeconds(10));

        await Task.Delay(200);
        var rows = await fixture.WithDbAsync(db => db.ResourceEvents.AsNoTracking().Where(e => e.Kind == "container.died" && (e.ResourceId == serverId || e.ResourceId == workloadId)).ToListAsync());
        Assert.Equal(2, rows.Count); // one for the server, one for its workload
        Assert.Contains(rows, r => r.ResourceType == "server" && r.Detail!.Contains("web") && r.Detail.Contains("exit=137") && r.Detail.Contains("oom"));
        Assert.Contains(rows, r => r.ResourceType == "workload" && r.ResourceId == workloadId);
        Assert.Equal("c1", received.Single(r => r.Kind == ServerEventKind.ContainerDied).ContainerId);
    }

    [RequiresDatabaseFact]
    public async Task A_workload_label_for_a_workload_of_another_server_is_not_trusted()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var (_, otherServer) = await fixture.SeedServerAsync();
        var foreign = await SeedWorkloadAsync(otherServer);
        await using var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();

        var notice = new EventNotice { EventId = "ev-x", Type = EventType.ContainerDied, ContainerId = "c1", ContainerName = "web" };
        notice.Labels["aethera.application.id"] = foreign.ToString();
        agent.Send(new AgentMessage { Event = notice });
        await GatewayFixture.EventuallyAsync(async () => await fixture.WithDbAsync(db => db.ResourceEvents.AnyAsync(e => e.ResourceId == serverId && e.Kind == "container.died")), "server event");
        await Task.Delay(150);
        Assert.False(await fixture.WithDbAsync(db => db.ResourceEvents.AnyAsync(e => e.ResourceId == foreign)));
    }

    [RequiresDatabaseFact]
    public async Task Docker_daemon_events_drive_the_docker_axis()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        await using var agent = FakeAgent.Start(fixture, serverId, heartbeats: false);
        await agent.ExpectWelcomeAsync();
        agent.Send(new AgentMessage { Event = new EventNotice { EventId = "d1", Type = EventType.DockerDaemonDown } });
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).DockerStatus == DomainDockerStatus.Stopped, "docker stopped by event");
        agent.Send(new AgentMessage { Event = new EventNotice { EventId = "d2", Type = EventType.DockerDaemonUp } });
        await GatewayFixture.EventuallyAsync(async () => (await fixture.LoadServerAsync(serverId)).DockerStatus == DomainDockerStatus.Running, "docker running by event");
    }

    private async Task<Guid> SeedWorkloadAsync(Guid serverId)
    {
        return await fixture.WithDbAsync(async db =>
        {
            var server = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == serverId);
            var project = new Project { OrganizationId = server.OrganizationId, Name = "p-" + Guid.NewGuid().ToString("N")[..6], Slug = "p-" + Guid.NewGuid().ToString("N")[..8] };
            var environment = new ProjectEnvironment { Project = project, Name = "production", Slug = "production", IsProduction = true };
            var application = new Application { Environment = environment, ServerId = serverId, Name = "app", Slug = "app-" + Guid.NewGuid().ToString("N")[..6], SourceKind = ApplicationSourceKind.DockerImage };
            db.AddRange(project, environment, application);
            await db.SaveChangesAsync();
            return application.Id;
        });
    }
}
