using System.Text.Json;
using System.Text.Json.Serialization;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Infrastructure.Mesh;

/// <summary>The instance's private network (settings key <c>mesh</c>, ADR 0012).</summary>
/// <param name="Provider"><c>none</c> (default), <c>wireGuard</c> (the agent manages <c>aethera0</c>) or <c>tailscale</c> (the existing <c>tailscale0</c>).</param>
/// <param name="Cidr">The mesh range; every server gets one address from it.</param>
/// <param name="ListenPort">The WireGuard UDP port servers listen on (a server may override it).</param>
public sealed record MeshSetting(MeshProvider Provider, string Cidr, int ListenPort)
{
    public const int DefaultListenPort = 51820;

    public static MeshSetting Default { get; } = new(MeshProvider.None, MeshCidr.Default, DefaultListenPort);

    [JsonIgnore]
    public bool Enabled => Provider != MeshProvider.None;
}

/// <summary>Reads and writes the <c>mesh</c> instance setting; a missing or unreadable row means the mesh is off with the defaults.</summary>
public static class MeshSettingsStore
{
    public const string Key = "mesh";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public static async Task<MeshSetting> GetAsync(AetheraDbContext db, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        return row is null ? MeshSetting.Default : Parse(row.ValueJson);
    }

    public static async Task SaveAsync(AetheraDbContext db, MeshSetting setting, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(setting, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(new InstanceSetting { Key = Key, ValueJson = json, UpdatedAt = now, UpdatedByUserId = userId });
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = now;
            row.UpdatedByUserId = userId;
        }
    }

    /// <summary>Reads a stored value; anything unreadable falls back field by field to the defaults.</summary>
    public static MeshSetting Parse(string json)
    {
        MeshSetting? stored;
        try
        {
            stored = JsonSerializer.Deserialize<MeshSetting>(json, Json);
        }
        catch (JsonException)
        {
            return MeshSetting.Default;
        }

        if (stored is null) return MeshSetting.Default;
        var cidr = MeshCidr.TryParse(stored.Cidr, out var parsed, out _) ? parsed.ToString() : MeshCidr.Default;
        var port = stored.ListenPort is >= 1 and <= 65535 ? stored.ListenPort : MeshSetting.DefaultListenPort;
        return new MeshSetting(Enum.IsDefined(stored.Provider) ? stored.Provider : MeshProvider.None, cidr, port);
    }
}
