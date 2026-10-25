using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.ScheduledTasks;

/// <summary>Remembers every notification the code under test published.</summary>
public sealed class RecordingNotifier : INotifier
{
    private readonly List<NotificationEvent> _events = [];

    public IReadOnlyList<NotificationEvent> Events
    {
        get
        {
            lock (_events) return [.. _events];
        }
    }

    public Task PublishAsync(NotificationEvent notification, CancellationToken cancellationToken)
    {
        lock (_events) _events.Add(notification);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The API with a real database, a <see cref="FakeServerTransport"/> instead of agents, a <see cref="RecordingNotifier"/> and the scheduler's
/// own timer switched off (tests call <c>TickAsync</c> themselves). Used as a class fixture: every test class gets its own database.
/// <paramref name="runJobs"/> false also stops the job workers, so a tick's runs stay pending and can be inspected.
/// </summary>
public class ScheduledTasksFixture : IDisposable
{
    private readonly Lazy<State> _state;

    public ScheduledTasksFixture() : this(true)
    {
    }

    protected ScheduledTasksFixture(bool runJobs) => _state = new(() => new State(runJobs));

    public sealed class State
    {
        public State(bool runJobs)
        {
            Base = new AetheraApiFactory();
            Transport = new FakeServerTransport();
            Notifier = new RecordingNotifier();
            Factory = Jobs.JobsApiFixture.Configure(Base, builder =>
            {
                builder.UseSetting("Aethera:ScheduledTasks:Enabled", "false");
                if (!runJobs) builder.UseSetting("Aethera:Jobs:WorkerCount", "0");
            }).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServerTransport>();
                services.AddSingleton<IServerTransport>(Transport);
                services.RemoveAll<INotifier>();
                services.AddSingleton<INotifier>(Notifier);
            }));
        }

        public AetheraApiFactory Base { get; }

        public WebApplicationFactory<Program> Factory { get; }

        public FakeServerTransport Transport { get; }

        public RecordingNotifier Notifier { get; }
    }

    public WebApplicationFactory<Program> Factory => _state.Value.Factory;

    public FakeServerTransport Transport => _state.Value.Transport;

    public RecordingNotifier Notifier => _state.Value.Notifier;

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose()
    {
        if (_state.IsValueCreated) _state.Value.Base.Dispose();
    }
}

/// <summary>The same fixture without job workers.</summary>
public sealed class IdleScheduledTasksFixture : ScheduledTasksFixture
{
    public IdleScheduledTasksFixture() : base(runJobs: false)
    {
    }
}

internal static class TaskTestHelpers
{
    public static async Task<T> WithDbAsync<T>(this WebApplicationFactory<Program> factory, Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    public static Task WithDbAsync(this WebApplicationFactory<Program> factory, Func<AetheraDbContext, Task> action) =>
        factory.WithDbAsync(async db =>
        {
            await action(db);
            return 0;
        });

    /// <summary>Gives the workload a (queued) deployment as its current one, as a deployed workload has, and the desired state.</summary>
    public static Task DeployedAsync(this WebApplicationFactory<Program> factory, string workloadId, DesiredState state = DesiredState.Running) =>
        factory.WithDbAsync(async db =>
        {
            var workload = await db.Workloads.FirstAsync(w => w.Id == Guid.Parse(workloadId));
            var deployment = Deployment.Queue(workload.Id, workload.EnvironmentId, workload.ServerId, workload.AllocateDeploymentNumber(), DeploymentTrigger.Manual, DateTimeOffset.UtcNow);
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
            workload.CurrentDeploymentId = deployment.Id;
            workload.DesiredState = state;
            await db.SaveChangesAsync();
        });

    public static async Task<JsonNode> CreateTaskAsync(this Tenant tenant, string workloadId, string kind = "applications", object? extra = null)
    {
        var body = new JsonObject
        {
            ["name"] = Tenant.Unique("task"), ["command"] = "echo hello", ["schedule"] = "*/5 * * * *", ["timeZone"] = "UTC",
        };
        if (extra is not null)
            foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(extra, Json.Options)!.AsObject()) body[key] = value?.DeepClone();
        return await tenant.Developer.CreateAsync($"/api/v1/{kind}/{workloadId}/scheduled-tasks", body);
    }

    public static async Task<JsonNode> RunNowAsync(this HttpClient client, string taskId)
    {
        var response = await client.PostAsync($"/api/v1/scheduled-tasks/{taskId}/run", new { });
        var body = await response.ReadAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"run -> {(int)response.StatusCode}: {body.ToJsonString()}");
        return body;
    }

    /// <summary>Waits until the run is finished and its job ended.</summary>
    public static async Task<JsonNode> WaitForRunAsync(this HttpClient client, string runId)
    {
        JsonNode? last = null;
        await Jobs.Eventually.UntilAsync(async () =>
        {
            last = await client.GetJsonAsync($"/api/v1/scheduled-task-runs/{runId}");
            if (last["status"]!.GetValue<string>() is "pending" or "running" || last["jobId"] is null) return false;
            var job = await client.GetJsonAsync($"/api/v1/jobs/{last["jobId"]!.GetValue<string>()}");
            return job["status"]!.GetValue<string>() is "succeeded" or "failed" or "cancelled";
        }, TimeSpan.FromSeconds(30), "the run to finish");
        return last!;
    }

    public static DockerContainer Container(string id, ContainerRunState state = ContainerRunState.Running, IReadOnlyDictionary<string, string>? labels = null) =>
        new(id, id + "-name", "img", "sha", state, state.ToString(), ContainerHealthState.None, null, DateTimeOffset.UtcNow, null, 0, false, 0,
            labels ?? new Dictionary<string, string>(), [], [], [], null);

    public static FakeServerTransport Containers(this FakeServerTransport transport, params DockerContainer[] containers) =>
        transport.On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => containers);

    public static FakeServerTransport Exec(this FakeServerTransport transport, Func<ContainerExecCommand, ContainerExecOutcome> handler) =>
        transport.On<ContainerExecCommand, ContainerExecOutcome>(handler);
}
