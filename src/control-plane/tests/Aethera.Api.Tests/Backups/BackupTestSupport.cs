using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Features.Backups;
using Aethera.Api.Tests.Agents;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Persistence;
using Aethera.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.Backups;

/// <summary>An S3 store that remembers what it was asked to do and answers a scripted test result.</summary>
public sealed class FakeObjectStore : IBackupObjectStore
{
    private readonly object _gate = new();

    public StoreTestResult TestResult { get; set; } = new(true, null);

    public List<S3Target> Tested { get; } = [];

    public Dictionary<string, byte[]> Uploaded { get; } = [];

    public List<string> Deleted { get; } = [];

    public void Reset()
    {
        lock (_gate)
        {
            TestResult = new StoreTestResult(true, null);
            Tested.Clear();
            Uploaded.Clear();
            Deleted.Clear();
        }
    }

    public Task<StoreTestResult> TestAsync(S3Target target, string? pathPrefix, CancellationToken cancellationToken)
    {
        lock (_gate) Tested.Add(target);
        return Task.FromResult(TestResult);
    }

    public async Task UploadFileAsync(S3Target target, string key, string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        lock (_gate) Uploaded[key] = bytes;
    }

    public Task DeleteAsync(S3Target target, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        lock (_gate) Deleted.AddRange(keys);
        return Task.CompletedTask;
    }
}

public sealed class RecordingNotifier : INotifier
{
    private readonly object _gate = new();
    private readonly List<NotificationEvent> _events = [];

    public IReadOnlyList<NotificationEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public void Reset()
    {
        lock (_gate) _events.Clear();
    }

    public Task PublishAsync(NotificationEvent notification, CancellationToken cancellationToken)
    {
        lock (_gate) _events.Add(notification);
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for <c>pg_dump</c> of Aethera's own database: a file of known bytes.</summary>
public sealed class FakeControlPlaneDump : IControlPlaneDump
{
    public static readonly byte[] Content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7 % 253)).ToArray();

    public Task<ControlPlaneDumpFile> CreateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aethera-test-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "aethera-offsite.dump");
        File.WriteAllBytes(path, Content);
        return Task.FromResult(new ControlPlaneDumpFile(directory, path));
    }
}

/// <summary>The API with workers, a <see cref="FakeServerTransport"/>, a <see cref="FakeObjectStore"/> and a recording notifier.</summary>
public sealed class BackupsFixture : IDisposable
{
    public BackupsFixture()
    {
        BackupRestoreJobHandler.PollInterval = TimeSpan.FromMilliseconds(100);
        Base = new AetheraApiFactory();
        Transport = new FakeServerTransport();
        Store = new FakeObjectStore();
        Notifier = new RecordingNotifier();
        Factory = Jobs.JobsApiFixture.Configure(Base).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IServerTransport>();
            services.AddSingleton<IServerTransport>(Transport);
            services.RemoveAll<IBackupObjectStore>();
            services.AddSingleton<IBackupObjectStore>(Store);
            services.RemoveAll<INotifier>();
            services.AddSingleton<INotifier>(Notifier);
            services.RemoveAll<IControlPlaneDump>();
            services.AddSingleton<IControlPlaneDump>(new FakeControlPlaneDump());
        }));
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeServerTransport Transport { get; }

    public FakeObjectStore Store { get; }

    public RecordingNotifier Notifier { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class BackupsCollection : ICollectionFixture<BackupsFixture>
{
    public const string Name = "backups";
}

/// <summary>A tenant with a server, a service, a backup destination and shortcuts the backup tests share.</summary>
public sealed class BackupStack(BackupsFixture fixture, Tenant tenant, string serverId, string environmentId, string serviceId, string serviceName, string destinationId)
{
    public Tenant Tenant { get; } = tenant;
    public string ServerId { get; } = serverId;
    public string EnvironmentId { get; } = environmentId;
    public string ServiceId { get; } = serviceId;
    public string ServiceName { get; } = serviceName;
    public string DestinationId { get; } = destinationId;
    public const string Secret = "r2-secret-access-key-0123456789";

    public string PoliciesUrl => $"/api/v1/services/{ServiceId}/backup-policies";

    public static async Task<BackupStack> CreateAsync(BackupsFixture fixture, string template = "postgres", Tenant? existing = null)
    {
        var tenant = existing ?? await fixture.NewTenantAsync();
        var server = await tenant.CreateServerAsync(publicIp: "203.0.113.10");
        var (_, environmentId) = await tenant.CreateProjectWithEnvironmentAsync();
        var name = Tenant.Unique("db");
        var service = await tenant.Developer.CreateAsync("/api/v1/services", new { name, environmentId, serverId = server.Id(), templateKey = template });
        var destination = await tenant.Admin.CreateAsync("/api/v1/backup-destinations", new
        {
            name = Tenant.Unique("r2"), endpoint = "https://acct123.r2.cloudflarestorage.com", region = "auto", bucket = "turn-one-backups", pathPrefix = "aethera",
            accessKeyId = "AKIA-TEST-KEY", secretAccessKey = Secret,
        });
        return new BackupStack(fixture, tenant, server.Id(), environmentId, service.Id(), name, destination.Id());
    }

    public async Task<JsonNode> PutPolicyAsync(object? body = null, HttpClient? client = null)
    {
        var response = await (client ?? Tenant.Developer).PutAsync(PoliciesUrl, Json.Body(body ?? new { kind = "database", destinationId = DestinationId }));
        var node = await response.ReadAsync();
        Assert.True(response.IsSuccessStatusCode, $"PUT {PoliciesUrl} -> {(int)response.StatusCode}: {node.ToJsonString()}");
        return node;
    }

    public async Task<string> CreateDatabasePolicyAsync(object? body = null) => (await PutPolicyAsync(body))["id"]!.GetValue<string>();

    public async Task<JsonNode> ServiceAsync() => await Tenant.Developer.GetJsonAsync($"/api/v1/services/{ServiceId}");

    public async Task<T> WithDbAsync<T>(Func<AetheraDbContext, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AetheraDbContext>());
    }

    public Task<List<BackupRun>> RunsAsync(string policyId) =>
        WithDbAsync(db => db.BackupRuns.AsNoTracking().Where(r => r.PolicyId == Guid.Parse(policyId)).OrderBy(r => r.CreatedAt).ToListAsync());

    /// <summary>Back up now through the API; returns the job id.</summary>
    public async Task<string> RunNowAsync(string policyId, HttpClient? client = null)
    {
        var response = await (client ?? Tenant.Developer).PostAsync($"/api/v1/backup-policies/{policyId}/run", new { });
        var body = await response.ReadAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"run -> {(int)response.StatusCode}: {body.ToJsonString()}");
        return body.Id();
    }

    /// <summary>Waits until the job is finished and returns it.</summary>
    public async Task<Job> WaitForJobAsync(string jobId)
    {
        await GatewayFixture.EventuallyAsync(async () => await WithDbAsync(db => db.Jobs.AsNoTracking()
            .AnyAsync(j => j.Id == Guid.Parse(jobId) && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed || j.Status == JobStatus.Cancelled))),
            "the job to finish", timeoutMs: 20000);
        return await WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == Guid.Parse(jobId)));
    }

    /// <summary>Runs a backup and waits for it; returns the run.</summary>
    public async Task<BackupRun> BackUpAsync(string policyId)
    {
        var job = await WaitForJobAsync(await RunNowAsync(policyId));
        return await WithDbAsync(db => db.BackupRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstAsync(r => r.PolicyId == Guid.Parse(policyId) && r.JobId == job.Id));
    }
}

/// <summary>Scripts for the fake agent that the backup tests share.</summary>
public static class FakeAgent
{
    public static DockerContainer Container(string name, ContainerRunState state = ContainerRunState.Running) =>
        new(Guid.NewGuid().ToString("N"), "/" + name, "img", "sha", state, state.ToString(), ContainerHealthState.None, null, null, null, 0, false, 0,
            new Dictionary<string, string>(), [], [], [], null);

    /// <summary>A running container for every label filter, and a successful backup of 1000 bytes (1200 stored).</summary>
    public static FakeServerTransport Healthy(this FakeServerTransport transport, string container = "db-1234")
    {
        transport.Commands.Clear();
        return transport
            .On<ContainerListCommand, IReadOnlyList<DockerContainer>>(_ => [Container(container)])
            .On<BackupRunCommand, BackupRunOutcome>(c => new BackupRunOutcome(c.BackupId, 1000, 1200, new string('a', 64), TimeSpan.FromSeconds(2)))
            .On<BackupRestoreCommand, BackupRestoreOutcome>(_ => new BackupRestoreOutcome(1000, TimeSpan.FromSeconds(1)))
            .On<BackupDeleteCommand, Unit>(_ => Unit.Value)
            .On<ContainerStopCommand, DockerContainer>(c => Container(c.Container, ContainerRunState.Exited))
            .On<ContainerStartCommand, DockerContainer>(c => Container(c.Container));
    }
}
