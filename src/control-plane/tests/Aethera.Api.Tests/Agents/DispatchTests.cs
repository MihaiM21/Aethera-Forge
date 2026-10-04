using Aethera.Agent.V1;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using D = Aethera.Domain.Transport;
using DomainSecretValue = Aethera.Domain.Transport.SecretValue;
using DomainContainerSpec = Aethera.Domain.Transport.ContainerSpec;
using ProtoLogSource = Aethera.Agent.V1.LogSource;
using ProtoLogStream = Aethera.Agent.V1.LogStream;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aethera.Api.Tests.Agents;

/// <summary>Command dispatch (ADR 0002 "Command idempotency, timeouts, cancellation") against a scripted agent.</summary>
[Collection(GatewayCollection.Name)]
public sealed class DispatchTests(GatewayFixture fixture)
{
    private Task<CommandOutcome<IReadOnlyList<DockerContainer>>> List(Guid serverId, CommandOptions? options = null, CancellationToken ct = default) =>
        fixture.Transport.ExecuteAsync<ContainerListCommand, IReadOnlyList<DockerContainer>>(serverId, new ContainerListCommand(), options ?? Opts(), ct);

    private static CommandOptions Opts(string key = "job-1:list:0", Guid? job = null, DateTimeOffset? deadline = null, Guid? org = null) =>
        new() { IdempotencyKey = key, JobId = job, Deadline = deadline ?? DateTimeOffset.UtcNow.AddMinutes(1), OrganizationId = org };

    private static CommandResult ContainerListResult(string commandId, params string[] names)
    {
        var result = new CommandResult { CommandId = commandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, ContainerList = new ContainerListResult() };
        foreach (var name in names) result.ContainerList.Containers.Add(new ContainerInfo { Id = "id-" + name, Name = name, State = ContainerState.Running });
        return result;
    }

    private async Task<(Guid Org, Guid Server, FakeAgent Agent)> ConnectedAsync(Action<Hello>? hello = null)
    {
        var (org, serverId) = await fixture.SeedServerAsync();
        var agent = FakeAgent.Start(fixture, serverId, hello);
        await agent.ExpectWelcomeAsync();
        return (org, serverId, agent);
    }

    [RequiresDatabaseFact]
    public async Task A_command_is_delivered_with_its_key_deadline_and_job_and_answered_with_a_typed_result()
    {
        var (org, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var jobId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var call = List(serverId, Opts("job-9:list:2", jobId, deadline, org));

        var command = await agent.NextCommandAsync();
        Assert.Equal(Command.RequestOneofCase.ContainerList, command.RequestCase);
        Assert.Equal("job-9:list:2", command.IdempotencyKey);
        Assert.Equal(jobId.ToString("D"), command.JobId);
        Assert.InRange((command.Deadline.ToDateTimeOffset() - deadline).Duration().TotalSeconds, 0, 1);
        Assert.True(Guid.TryParse(command.CommandId, out _));

        agent.Ack(command.CommandId);
        agent.Result(ContainerListResult(command.CommandId, "web", "db"));
        var outcome = await call;

        Assert.True(outcome.Succeeded);
        Assert.Equal(["web", "db"], outcome.Result!.Select(c => c.Name));
        Assert.False(outcome.Replayed);
        Assert.Empty(fixture.Get<AgentSessionRegistry>().State(serverId).Pending);

        // "Every dispatched command writes an audit event (actor, server, command type, outcome) without payloads."
        var audit = Assert.Single(await fixture.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.ResourceId == serverId && e.Action == "agent.command").ToListAsync()));
        Assert.Equal(org, audit.OrganizationId);
        Assert.Contains("container.list", audit.MetadataJson);
        Assert.Contains("succeeded", audit.MetadataJson);
        Assert.Contains("job-9:list:2", audit.MetadataJson.Replace("idempotencyKey", "idempotencyKey")); // the key is metadata, the payload is not
        Assert.DoesNotContain("web", audit.MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task A_failed_command_is_an_outcome_not_an_exception_and_its_message_is_redacted()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var create = new ContainerCreateCommand(new DomainContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfSecret("TOKEN", "tok-secret-value-1")] });
        var call = fixture.Transport.ExecuteAsync<ContainerCreateCommand, ContainerCreated>(serverId, create, Opts("job:create:0"), default);

        var command = await agent.NextCommandAsync();
        Assert.Equal("tok-secret-value-1", command.ContainerCreate.Spec.Env.Single(e => e.Name == "TOKEN").Secret.Value); // the secret travels to the agent ...
        agent.Ack(command.CommandId);
        agent.Result(new CommandResult
        {
            CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Failed, ErrorCode = ErrorCode.ImagePullFailed,
            ErrorMessage = "pull denied for tok-secret-value-1", ExitCode = 1,
        });
        var outcome = await call;

        Assert.Equal(D.CommandStatus.Failed, outcome.Status);
        Assert.Equal(CommandErrorCode.ImagePullFailed, outcome.ErrorCode);
        Assert.DoesNotContain("tok-secret-value-1", outcome.ErrorMessage); // ... but never back into errors
        Assert.Contains("********", outcome.ErrorMessage);
        var thrown = Assert.Throws<ServerTransportException>(() => outcome.EnsureSucceeded());
        Assert.Equal(TransportErrors.CommandFailed, thrown.Code);
        Assert.Equal(CommandErrorCode.ImagePullFailed, thrown.AgentError);
        Assert.DoesNotContain("tok-secret-value-1", thrown.Message);
    }

    [RequiresDatabaseFact]
    public async Task A_result_of_the_wrong_shape_fails_the_command_instead_of_crashing()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var call = List(serverId);
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId);
        agent.Result(new CommandResult { CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, Prune = new PruneResult() });
        var outcome = await call;
        Assert.Equal(D.CommandStatus.Failed, outcome.Status);
        Assert.Equal(CommandErrorCode.Internal, outcome.ErrorCode);
    }

    [RequiresDatabaseFact]
    public async Task No_ack_within_the_timeout_fails_the_dispatch_and_forgets_the_command()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var call = List(serverId);
        await agent.NextCommandAsync(); // delivered, never acknowledged

        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => call);
        Assert.Equal(TransportErrors.AckTimeout, ex.Code);
        Assert.True(ex.Transient); // the job retries on the next session with the same key
        Assert.Empty(fixture.Get<AgentSessionRegistry>().State(serverId).Pending);
    }

    [RequiresDatabaseFact]
    public async Task Rejected_acks_fail_fast_with_the_reason()
    {
        foreach (var (status, code, transient) in new[]
        {
            (AckStatus.RejectedBusy, TransportErrors.AgentBusy, true),
            (AckStatus.RejectedExpired, TransportErrors.CommandRejected, false),
            (AckStatus.RejectedPolicy, TransportErrors.CommandRejected, false),
            (AckStatus.RejectedUnsupported, TransportErrors.Unsupported, false),
        })
        {
            var (_, serverId, agent) = await ConnectedAsync();
            await using var lifetime = agent;
            var call = List(serverId);
            var command = await agent.NextCommandAsync();
            agent.Ack(command.CommandId, status, status == AckStatus.RejectedPolicy ? ErrorCode.PolicyViolation : ErrorCode.Unspecified, "no");

            var ex = await Assert.ThrowsAsync<ServerTransportException>(() => call);
            Assert.Equal(code, ex.Code);
            Assert.Equal(transient, ex.Transient);
            if (status == AckStatus.RejectedPolicy) Assert.Equal(CommandErrorCode.PolicyViolation, ex.AgentError);
        }
    }

    [RequiresDatabaseFact]
    public async Task Without_a_session_the_agent_is_unavailable()
    {
        var (_, serverId) = await fixture.SeedServerAsync();
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => List(serverId));
        Assert.Equal(TransportErrors.AgentUnavailable, ex.Code);
        Assert.True(ex.Transient);
        Assert.Equal(TransportStatus.Down(TransportKind.Agent, "No agent session is connected for this server.").Available, (await fixture.Transport.GetStatusAsync(serverId, default)).Available);
    }

    [RequiresDatabaseFact]
    public async Task A_command_the_agent_has_no_capability_for_is_never_sent()
    {
        var (_, serverId, agent) = await ConnectedAsync(hello => hello.Capabilities.Remove("compose.v2"));
        await using var lifetime = agent;
        var compose = new ComposeUpCommand(new ComposeProjectSpec("shop", "services: {}"));
        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => fixture.Transport.ExecuteAsync<ComposeUpCommand, ComposeOutcome>(serverId, compose, Opts(), default));
        Assert.Equal(TransportErrors.Unsupported, ex.Code);
        Assert.DoesNotContain(await agent.DrainAsync(), m => m.Command is not null);
    }

    [RequiresDatabaseFact]
    public async Task No_result_by_the_deadline_plus_grace_is_agent_unavailable_not_failed()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var started = DateTime.UtcNow;
        var call = List(serverId, Opts(deadline: DateTimeOffset.UtcNow.AddMilliseconds(250)));
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId); // accepted ... and then silence

        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => call);
        Assert.Equal(TransportErrors.AgentUnavailable, ex.Code);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.45, 5); // 250 ms deadline + 300 ms grace
    }

    [RequiresDatabaseFact]
    public async Task Cancelling_sends_CancelCommand_and_returns_the_cancelled_result()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        using var cts = new CancellationTokenSource();
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), Opts(), cts.Token);
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId);

        await cts.CancelAsync();
        var cancel = await agent.NextAsync(m => m.CancelCommand);
        Assert.Equal(command.CommandId, cancel.CommandId);
        Assert.Equal(TimeSpan.FromSeconds(15), cancel.Grace.ToTimeSpan()); // ADR 0002: default grace 15 s
        agent.Result(new CommandResult { CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Cancelled, ErrorCode = ErrorCode.Cancelled });

        var outcome = await call;
        Assert.Equal(D.CommandStatus.Cancelled, outcome.Status);
        Assert.Empty(fixture.Get<AgentSessionRegistry>().State(serverId).Pending);
    }

    [RequiresDatabaseFact]
    public async Task Cancelling_a_command_the_agent_never_answers_ends_with_cancellation_after_the_grace()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        using var cts = new CancellationTokenSource();
        var options = new CommandOptions { IdempotencyKey = "k", Deadline = DateTimeOffset.UtcNow.AddMinutes(1), CancelGrace = TimeSpan.FromMilliseconds(100) };
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), options, cts.Token);
        agent.Ack((await agent.NextCommandAsync()).CommandId);
        await fixture.AckSettledAsync(serverId);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [RequiresDatabaseFact]
    public async Task A_duplicate_of_a_finished_command_is_answered_from_the_agents_cache()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var call = List(serverId);
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId, AckStatus.DuplicateCompleted);
        var result = ContainerListResult(command.CommandId, "web");
        result.Replayed = true;
        agent.Result(result);
        var outcome = await call;
        Assert.True(outcome.Replayed);
        Assert.True(outcome.Succeeded);
    }

    [RequiresDatabaseFact]
    public async Task Progress_reaches_the_callback_with_secrets_masked()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        var progress = new List<CommandProgressInfo>();
        var options = Opts("k:p:0");
        var withProgress = new CommandOptions { IdempotencyKey = options.IdempotencyKey, Deadline = options.Deadline, OnProgress = progress.Add };
        var create = new ContainerCreateCommand(new DomainContainerSpec("nginx", "web") { Env = [EnvVarSpec.OfSecret("TOKEN", "progress-secret-77")] });
        var call = fixture.Transport.ExecuteAsync<ContainerCreateCommand, ContainerCreated>(serverId, create, withProgress, default);
        var command = await agent.NextCommandAsync();
        agent.Ack(command.CommandId);
        agent.Send(new AgentMessage { CommandProgress = new CommandProgress { CommandId = command.CommandId, Step = "pull", Message = "pulling with progress-secret-77", Percent = 40, At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } });
        agent.Result(new CommandResult { CommandId = command.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, ContainerCreate = new ContainerCreateResult { ContainerId = "c", Name = "web", State = ContainerState.Running } });
        await call;

        var seen = Assert.Single(progress);
        Assert.Equal("pull", seen.Step);
        Assert.Equal(40, seen.Percent);
        Assert.DoesNotContain("progress-secret-77", seen.Message);
    }

    // ---- reconnect reconciliation ----------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task After_a_reconnect_an_unknown_command_is_redelivered_with_the_same_idempotency_key()
    {
        var (_, serverId, first) = await ConnectedAsync();
        await using var lifetime = first;
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), Opts("job:start:1"), default);
        var original = await first.NextCommandAsync();
        first.Ack(original.CommandId);
        await fixture.AckSettledAsync(serverId);
        first.Drop();
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(call.IsCompleted); // a dropped stream does not fail the command

        await using var second = FakeAgent.Start(fixture, serverId); // Hello: running_command_ids empty
        await second.ExpectWelcomeAsync();
        var redelivered = await second.NextCommandAsync();

        Assert.Equal(original.IdempotencyKey, redelivered.IdempotencyKey);
        Assert.NotEqual(original.CommandId, redelivered.CommandId); // command_id is per delivery
        Assert.Equal(Command.RequestOneofCase.ContainerStart, redelivered.RequestCase);

        second.Ack(redelivered.CommandId, AckStatus.DuplicateCompleted);
        second.Result(new CommandResult
        {
            CommandId = redelivered.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, Replayed = true,
            ContainerState = new ContainerStateResult { Container = new ContainerInfo { Id = "c1", Name = "web", State = ContainerState.Running } },
        });
        var outcome = await call;
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Replayed);
        Assert.Equal(ContainerRunState.Running, outcome.Result!.State);
    }

    [RequiresDatabaseFact]
    public async Task A_command_the_agent_still_runs_is_not_redelivered()
    {
        var (_, serverId, first) = await ConnectedAsync();
        await using var lifetime = first;
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), Opts("job:start:2"), default);
        var original = await first.NextCommandAsync();
        first.Ack(original.CommandId);
        await fixture.AckSettledAsync(serverId);
        first.Drop();
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));

        await using var second = FakeAgent.Start(fixture, serverId, hello => hello.RunningCommandIds.Add(original.CommandId));
        await second.ExpectWelcomeAsync();
        Assert.DoesNotContain(await second.DrainAsync(400), m => m.Command is not null);
        Assert.False(call.IsCompleted);

        second.Result(new CommandResult
        {
            CommandId = original.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded,
            ContainerState = new ContainerStateResult { Container = new ContainerInfo { Id = "c1", Name = "web", State = ContainerState.Running } },
        });
        Assert.True((await call).Succeeded);
    }

    [RequiresDatabaseFact]
    public async Task A_redelivery_the_agent_refuses_fails_the_waiting_call()
    {
        var (_, serverId, first) = await ConnectedAsync();
        await using var lifetime = first;
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), Opts("job:start:3"), default);
        first.Ack((await first.NextCommandAsync()).CommandId);
        await fixture.AckSettledAsync(serverId);
        first.Drop();
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));

        await using var second = FakeAgent.Start(fixture, serverId);
        await second.ExpectWelcomeAsync();
        var redelivered = await second.NextCommandAsync();
        second.Ack(redelivered.CommandId, AckStatus.RejectedExpired);

        var ex = await Assert.ThrowsAsync<ServerTransportException>(() => call);
        Assert.Equal(TransportErrors.CommandRejected, ex.Code);
    }

    [RequiresDatabaseFact]
    public async Task A_cancel_requested_while_disconnected_is_delivered_after_the_reconnect()
    {
        var (_, serverId, first) = await ConnectedAsync();
        await using var lifetime = first;
        using var cts = new CancellationTokenSource();
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), Opts("job:start:4"), cts.Token);
        var original = await first.NextCommandAsync();
        first.Ack(original.CommandId);
        await fixture.AckSettledAsync(serverId);
        first.Drop();
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        await cts.CancelAsync(); // nobody to tell: the cancel is remembered
        await Task.Delay(100);
        await using var second = FakeAgent.Start(fixture, serverId, hello => hello.RunningCommandIds.Add(original.CommandId));
        await second.ExpectWelcomeAsync();
        var cancel = await second.NextAsync(m => m.CancelCommand);
        Assert.Equal(original.CommandId, cancel.CommandId);
        second.Result(new CommandResult { CommandId = original.CommandId, Status = Aethera.Agent.V1.CommandStatus.Cancelled });
        Assert.Equal(D.CommandStatus.Cancelled, (await call).Status);
    }

    [RequiresDatabaseFact]
    public async Task A_job_cancelled_while_the_agent_was_away_and_unknown_to_it_ends_as_cancelled()
    {
        var (_, serverId, first) = await ConnectedAsync();
        await using var lifetime = first;
        using var cts = new CancellationTokenSource();
        var options = new CommandOptions { IdempotencyKey = "job:start:5", Deadline = DateTimeOffset.UtcNow.AddMinutes(1), CancelGrace = TimeSpan.FromSeconds(30) };
        var call = fixture.Transport.ExecuteAsync<ContainerStartCommand, DockerContainer>(serverId, new ContainerStartCommand("web"), options, cts.Token);
        first.Ack((await first.NextCommandAsync()).CommandId);
        await fixture.AckSettledAsync(serverId);
        first.Drop();
        await first.Run.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Task.Delay(100);

        await using var second = FakeAgent.Start(fixture, serverId); // does not know the command: nothing to cancel on the host
        await second.ExpectWelcomeAsync();
        Assert.Equal(D.CommandStatus.Cancelled, (await call).Status);
        Assert.DoesNotContain(await second.DrainAsync(), m => m.Command is not null);
    }

    // ---- container log streaming through the transport -------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Following_container_logs_starts_a_stream_fans_out_chunks_and_stops_it()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var logs = fixture.Transport.StreamLogsAsync(serverId, new LogStreamRequest("web", true, null, 10), cts.Token).GetAsyncEnumerator(cts.Token);
        var first = logs.MoveNextAsync().AsTask();

        var start = await agent.NextCommandAsync();
        Assert.Equal(Command.RequestOneofCase.LogStreamStart, start.RequestCase);
        Assert.Equal("web", start.LogStreamStart.Container);
        var streamId = start.LogStreamStart.StreamId;
        agent.Ack(start.CommandId);
        agent.Result(new CommandResult { CommandId = start.CommandId, Status = Aethera.Agent.V1.CommandStatus.Succeeded, LogStreamStart = new LogStreamStartResult { StreamId = streamId, Started = true } });

        agent.Send(new AgentMessage { LogChunk = new Aethera.Agent.V1.LogChunk { StreamId = streamId, Source = ProtoLogSource.Container, Sequence = 1, Stream = ProtoLogStream.Stderr, Data = Google.Protobuf.ByteString.CopyFromUtf8("hello\n"), Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } });
        Assert.True(await first);
        Assert.Equal("hello\n", logs.Current.Text);
        Assert.Equal(Aethera.Domain.LogStream.Stderr, logs.Current.Stream);

        agent.Send(new AgentMessage { LogChunk = new Aethera.Agent.V1.LogChunk { StreamId = streamId, Source = ProtoLogSource.Container, Sequence = 2, Eof = true, EofReason = "container removed" } });
        Assert.True(await logs.MoveNextAsync()); // the end marker carries the reason ...
        Assert.True(logs.Current.Eof);
        Assert.Equal("container removed", logs.Current.EofReason);
        Assert.False(await logs.MoveNextAsync()); // ... and then the enumeration ends

        var stop = await agent.NextCommandAsync();
        Assert.Equal(Command.RequestOneofCase.LogStreamStop, stop.RequestCase);
        Assert.Equal(streamId, stop.LogStreamStop.StreamId);
        agent.Ack(stop.CommandId);
        agent.Succeed(stop.CommandId);
    }

    [RequiresDatabaseFact]
    public async Task A_container_stream_nobody_listens_to_is_paused_once()
    {
        var (_, serverId, agent) = await ConnectedAsync();
        await using var lifetime = agent;
        for (ulong sequence = 1; sequence <= 3; sequence++)
            agent.Send(new AgentMessage { LogChunk = new Aethera.Agent.V1.LogChunk { StreamId = "container:gone", Source = ProtoLogSource.Container, Sequence = sequence, Data = Google.Protobuf.ByteString.CopyFromUtf8("x\n") } });

        var pause = await agent.NextAsync(m => m.LogFlowControl);
        Assert.Equal("container:gone", pause.StreamId);
        Assert.Equal(0UL, pause.WindowBytes);
        Assert.DoesNotContain(await agent.DrainAsync(), m => m.LogFlowControl is not null);
    }
}
