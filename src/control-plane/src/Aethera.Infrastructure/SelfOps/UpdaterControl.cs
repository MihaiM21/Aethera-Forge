using System.Text.Json;
using Aethera.Domain;
using Microsoft.Extensions.Options;

namespace Aethera.Infrastructure.SelfOps;

/// <summary>State of the update the updater container is carrying out, as it reports it.</summary>
/// <param name="State"><c>idle</c>, <c>requested</c>, <c>backing_up</c>, <c>pulling</c>, <c>restarting</c>, <c>done</c> or <c>failed</c>.</param>
public sealed record UpdaterStatus(bool Available, string State, string? TargetVersion, string? Message, DateTimeOffset? UpdatedAt);

/// <summary>
/// The file protocol between the control plane and the updater container (<see cref="UpdateOptions.ControlDirectory"/>): the API never
/// touches Docker. It writes <c>request.json</c> (atomically); the updater, which holds the Docker socket and the compose project,
/// takes it, writes <c>status.json</c> as it goes and touches <c>alive</c> every few seconds.
/// </summary>
public sealed class UpdaterControl(IOptions<UpdateOptions> options, IClock clock)
{
    /// <summary>How fresh the updater's <c>alive</c> file must be for one-click updates to be offered.</summary>
    public static readonly TimeSpan AliveWindow = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Dir => options.Value.ControlDirectory;
    private string RequestPath => Path.Combine(Dir, "request.json");
    private string StatusPath => Path.Combine(Dir, "status.json");
    private string AlivePath => Path.Combine(Dir, "alive");

    public bool IsAvailable()
    {
        try
        {
            return File.Exists(AlivePath) && clock.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(AlivePath), TimeSpan.Zero) < AliveWindow;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<UpdaterStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var available = IsAvailable();
        try
        {
            if (File.Exists(RequestPath))
            {
                // Taken by the updater when it starts working on it; until then it is merely requested.
                var pending = await ReadAsync<UpdateRequestFile>(RequestPath, cancellationToken);
                if (pending is not null) return new UpdaterStatus(available, "requested", pending.Version, "Waiting for the updater to pick the request up.", pending.RequestedAt);
            }
            var status = File.Exists(StatusPath) ? await ReadAsync<UpdateStatusFile>(StatusPath, cancellationToken) : null;
            return status is null
                ? new UpdaterStatus(available, "idle", null, null, null)
                : new UpdaterStatus(available, status.State ?? "idle", status.Version, status.Message, status.UpdatedAt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UpdaterStatus(available, "idle", null, null, null);
        }
    }

    /// <summary>True while a request is waiting or the updater reports an unfinished update.</summary>
    public static bool InFlight(UpdaterStatus status) => status.State is "requested" or "backing_up" or "pulling" or "restarting";

    /// <summary>Writes the request for <paramref name="version"/>. The caller has checked <see cref="IsAvailable"/> and <see cref="InFlight"/>.</summary>
    public async Task RequestAsync(string version, Guid? requestedBy, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Dir);
        var temp = RequestPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new UpdateRequestFile(version, requestedBy, clock.UtcNow), Json), cancellationToken);
        File.Move(temp, RequestPath, overwrite: true);
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
    }

    private sealed record UpdateRequestFile(string Version, Guid? RequestedBy, DateTimeOffset RequestedAt);

    private sealed record UpdateStatusFile(string? State, string? Version, string? Message, DateTimeOffset? UpdatedAt);
}
