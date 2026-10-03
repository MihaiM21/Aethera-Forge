using Aethera.Agent.V1;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Aethera.Infrastructure.Jobs;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using ProtoLogSource = Aethera.Agent.V1.LogSource;
using DomainSecretValue = Aethera.Domain.Transport.SecretValue;

namespace Aethera.Api.Tests.Agents;

/// <summary>Log ingestion and flow control (ADR 0002 "Log streaming and flow control"). The window is 200 bytes, so credit is granted after 100.</summary>
[Collection(GatewayCollection.Name)]
public sealed class LogIngestTests(GatewayFixture fixture)
{
    private sealed record Build(Guid ServerId, FakeAgent Agent, string BuildId, Command Command, Task<CommandOutcome<BuildOutcome>> Call);

    private async Task<Build> StartBuildAsync(Action<BuildSpec>? _ = null, CommandOptions? options = null, IReadOnlyList<EnvVarSpec>? env = null)
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var agent = FakeAgent.Start(fixture, serverId);
        await agent.ExpectWelcomeAsync();
        var buildId = Guid.NewGuid().ToString("N");
        var spec = new BuildSpec(buildId, BuildEngineKind.Dockerfile, ["app:1"]) { Env = env };
        var call = fixture.Transport.ExecuteAsync<BuildImageCommand, BuildOutcome>(
            serverId, new BuildImageCommand(spec), options ?? new CommandOptions { IdempotencyKey = $"job:{buildId}:build:0", Deadline = DateTimeOffset.UtcNow.AddMinutes(5) }, default);
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId);
        await fixture.AckSettledAsync(serverId);
        return new Build(serverId, agent, buildId, command, call);
    }

    private static AgentMessage Chunk(string streamId, ulong sequence, string data, bool eof = false, ProtoLogSource source = ProtoLogSource.Build) => new()
    {
        LogChunk = new LogChunk
        {
            StreamId = streamId, Source = source, Sequence = sequence, Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Stream = Aethera.Agent.V1.LogStream.Stdout, Data = ByteString.CopyFromUtf8(data), Eof = eof,
        },
    };

    private Task<List<Aethera.Domain.LogChunk>> RowsAsync(string streamId) =>
        fixture.WithDbAsync(db => db.LogChunks.AsNoTracking().Where(c => c.StreamId == streamId).OrderBy(c => c.Sequence).ToListAsync());

    private static async Task FinishAsync(Build build)
    {
        build.Agent.Result(new CommandResult { CommandId = build.Command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, Build = new BuildResult { BuildId = build.BuildId, Digest = "sha256:abc" } });
        Assert.True((await build.Call).Succeeded);
    }

    [RequiresDatabaseFact]
    public async Task Build_chunks_are_stored_durably_before_credit_is_granted()
    {
        var build = await StartBuildAsync();
        await using var _ = build.Agent;
        var stream = $"build:{build.BuildId}";

        build.Agent.Send(Chunk(build.BuildId, 1, new string('a', 120) + "\n")); // 121 bytes: more than half of the 200 byte window
        var grant = await build.Agent.NextAsync(m => m.LogFlowControl);
        Assert.Equal(build.BuildId, grant.StreamId);
        Assert.Equal(1UL, grant.AckedSequence);
        Assert.Equal(200UL, grant.WindowBytes);
        Assert.Single(await RowsAsync(stream)); // the ack is only sent after the insert

        build.Agent.Send(Chunk(build.BuildId, 2, "b\n"));
        build.Agent.Send(Chunk(build.BuildId, 3, "c\n"));
        Assert.DoesNotContain(await build.Agent.DrainAsync(300), m => m.LogFlowControl is not null); // below half a window: no ack yet

        build.Agent.Send(Chunk(build.BuildId, 4, "", eof: true));
        var final = await build.Agent.NextAsync(m => m.LogFlowControl);
        Assert.Equal(4UL, final.AckedSequence); // the end of a stream is always acknowledged
        var rows = await RowsAsync(stream);
        Assert.Equal([1L, 2L, 3L, 4L], rows.Select(r => r.Sequence));
        Assert.All(rows, r => Assert.Equal(Aethera.Domain.LogSource.Build, r.Source));
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task A_replayed_chunk_is_stored_once()
    {
        var build = await StartBuildAsync();
        await using var _ = build.Agent;
        build.Agent.Send(Chunk(build.BuildId, 1, "one\n"));
        build.Agent.Send(Chunk(build.BuildId, 2, "two\n"));
        build.Agent.Send(Chunk(build.BuildId, 1, "one\n")); // sent again after a reconnect
        build.Agent.Send(Chunk(build.BuildId, 2, "two\n"));
        build.Agent.Send(Chunk(build.BuildId, 3, "three\n", eof: true));
        await build.Agent.NextAsync(m => m.LogFlowControl);

        var rows = await RowsAsync($"build:{build.BuildId}");
        Assert.Equal(["one\n", "two\n", "three\n"], rows.Select(r => r.Data));
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task A_chunk_for_a_stream_the_control_plane_did_not_ask_for_is_dropped_and_not_acknowledged()
    {
        var build = await StartBuildAsync();
        await using var _ = build.Agent;
        build.Agent.Send(Chunk("someone-elses-build", 1, new string('x', 300)));
        build.Agent.Send(Chunk("job:" + Guid.NewGuid(), 1, new string('x', 300), source: ProtoLogSource.Deploy));
        Assert.DoesNotContain(await build.Agent.DrainAsync(400), m => m.LogFlowControl is not null);
        Assert.Empty(await fixture.WithDbAsync(db => db.LogChunks.Where(c => c.StreamId == "build:someone-elses-build" || c.StreamId == "deploy:someone-elses-build").ToListAsync()));
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task Secrets_of_the_running_command_are_masked_on_ingest_as_defense_in_depth()
    {
        var build = await StartBuildAsync(env: [EnvVarSpec.OfSecret("NPM_TOKEN", "npm-token-value-9")]);
        await using var _ = build.Agent;
        build.Agent.Send(Chunk(build.BuildId, 1, "registry auth npm-token-value-9 failed\n", eof: true)); // the agent should have masked it; if not, we do
        await build.Agent.NextAsync(m => m.LogFlowControl);
        var row = Assert.Single(await RowsAsync($"build:{build.BuildId}"));
        Assert.DoesNotContain("npm-token-value-9", row.Data);
        Assert.Contains(ISecretRedactor.Mask, row.Data);
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task Nul_bytes_and_invalid_utf8_do_not_break_the_insert()
    {
        var build = await StartBuildAsync();
        await using var _ = build.Agent;
        build.Agent.Send(new AgentMessage
        {
            LogChunk = new LogChunk { StreamId = build.BuildId, Source = ProtoLogSource.Build, Sequence = 1, Eof = true, Data = ByteString.CopyFrom(0x61, 0x00, 0xFF, 0x62) },
        });
        await build.Agent.NextAsync(m => m.LogFlowControl);
        var row = Assert.Single(await RowsAsync($"build:{build.BuildId}"));
        Assert.DoesNotContain('\0', row.Data);
        Assert.StartsWith("a", row.Data);
        Assert.EndsWith("b", row.Data);
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task The_caller_can_choose_the_persisted_stream_and_sees_each_chunk()
    {
        var seen = new List<LogEntry>();
        var streamId = $"deploy:{Guid.NewGuid()}";
        var options = new CommandOptions { IdempotencyKey = "job:x:build:0", Deadline = DateTimeOffset.UtcNow.AddMinutes(5), LogStreamId = streamId, OnLog = seen.Add };
        var build = await StartBuildAsync(options: options);
        await using var _ = build.Agent;
        build.Agent.Send(Chunk(build.BuildId, 1, "line\n", eof: true));
        await build.Agent.NextAsync(m => m.LogFlowControl);

        Assert.Single(await RowsAsync(streamId));
        Assert.Empty(await RowsAsync($"build:{build.BuildId}"));
        Assert.Equal("line\n", Assert.Single(seen).Text);
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task Chunks_are_published_to_live_subscribers_after_they_are_stored()
    {
        var build = await StartBuildAsync();
        await using var _ = build.Agent;
        var bus = fixture.Get<ILiveBus>();
        var received = new TaskCompletionSource<LogBusMessage>();
        await using var subscription = await bus.SubscribeAsync(LiveChannels.Logs($"build:{build.BuildId}"), json =>
        {
            if (LogBusMessage.Parse(json) is { Line: not null } message) received.TrySetResult(message);
        });
        build.Agent.Send(Chunk(build.BuildId, 1, "live\n"));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("live\n", message.Line!.Text);
        Assert.Equal(1, message.Line.Sequence);
        await FinishAsync(build);
    }

    [RequiresDatabaseFact]
    public async Task After_a_reconnect_the_agent_is_told_how_far_the_control_plane_got()
    {
        var build = await StartBuildAsync();
        await using var first = build.Agent;
        for (ulong i = 1; i <= 3; i++) build.Agent.Send(Chunk(build.BuildId, i, $"line {i}\n"));
        await GatewayFixture.EventuallyAsync(async () => (await RowsAsync($"build:{build.BuildId}")).Count == 3, "three chunks stored");
        build.Agent.Drop();
        await build.Agent.Run.WaitAsync(TimeSpan.FromSeconds(5));

        await using var second = FakeAgent.Start(fixture, build.ServerId, hello => hello.ActiveLogStreamIds.Add(build.BuildId));
        await second.ExpectWelcomeAsync();
        var resume = await second.NextAsync(m => m.LogFlowControl);
        Assert.Equal(build.BuildId, resume.StreamId);
        Assert.Equal(3UL, resume.AckedSequence); // the agent re-sends everything after 3
        Assert.Equal(200UL, resume.WindowBytes);

        // The pending build is re-sent too (the agent does not list it as running); finish it on the new stream.
        var redelivered = await second.NextCommandAsync();
        Assert.Equal(build.Command.IdempotencyKey, redelivered.IdempotencyKey);
        Assert.Equal(build.BuildId, redelivered.Build.BuildId);
        second.Ack(redelivered.CommandId, AckStatus.DuplicateRunning);
        second.Result(new CommandResult { CommandId = redelivered.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, Build = new BuildResult { BuildId = build.BuildId } });
        Assert.True((await build.Call).Succeeded);
    }

    [RequiresDatabaseFact]
    public async Task A_redelivered_deploy_command_continues_its_stream_after_the_stored_chunks()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var first = FakeAgent.Start(fixture, serverId);
        await using var _ = first;
        await first.ExpectWelcomeAsync();
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(
            serverId, new ContainerStartCommand("web"), new CommandOptions { IdempotencyKey = "job:deploy:0", Deadline = DateTimeOffset.UtcNow.AddMinutes(1), LogStreamId = "deploy:d-1" }, default);
        var original = await first.NextCommandAsync();
        first.Ack(original.CommandId);
        await fixture.AckSettledAsync(serverId);
        first.Send(Chunk(original.CommandId, 1, "start\n", source: ProtoLogSource.Deploy));
        first.Send(Chunk(original.CommandId, 2, "pulling\n", source: ProtoLogSource.Deploy));
        await GatewayFixture.EventuallyAsync(async () => (await RowsAsync("deploy:d-1")).Count == 2, "two chunks stored");
        first.Drop();
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));

        await using var second = FakeAgent.Start(fixture, serverId);
        await second.ExpectWelcomeAsync();
        var redelivered = await second.NextCommandAsync();
        second.Ack(redelivered.CommandId);
        second.Send(Chunk(redelivered.CommandId, 1, "restarted\n", source: ProtoLogSource.Deploy)); // a fresh execution counts from 1 again
        await GatewayFixture.EventuallyAsync(async () => (await RowsAsync("deploy:d-1")).Count == 3, "the new chunk appended");
        Assert.Equal([1L, 2L, 3L], (await RowsAsync("deploy:d-1")).Select(r => r.Sequence)); // continued, not dropped as a duplicate of sequence 1
        second.Result(new CommandResult { CommandId = redelivered.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, ContainerState = new ContainerStateResult { Container = new ContainerInfo { Id = "c" } } });
        Assert.True((await call).Succeeded);
    }

    [RequiresDatabaseFact]
    public async Task An_oversized_chunk_is_refused()
    {
        var build = await StartBuildAsync();
        await using var _ = build.Agent;
        build.Agent.Send(Chunk(build.BuildId, 1, new string('z', 2500))); // the limit is 1000 bytes (2000 tolerated)
        Assert.DoesNotContain(await build.Agent.DrainAsync(300), m => m.LogFlowControl is not null);
        Assert.Empty(await RowsAsync($"build:{build.BuildId}"));
        await FinishAsync(build);
    }
}
