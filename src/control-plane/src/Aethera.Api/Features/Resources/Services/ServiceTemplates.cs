using Aethera.Domain;

namespace Aethera.Api.Features.Resources.Services;

public sealed record ServiceTemplateVersion(string Version, string Image, bool IsDefault);

public sealed record ServiceTemplatePort(int ContainerPort, string Protocol, bool IsHttp);

public sealed record ServiceTemplateVolume(string Name, string MountPath);

public sealed record ServiceTemplateHealthCheck(
    string Type, string? Path, int? Port, int IntervalSeconds, int TimeoutSeconds, int Retries, int StartPeriodSeconds);

/// <summary>
/// An environment variable of the template. <c>Generate</c> is <c>"password"</c> for a random strong secret (stored as a
/// <em>secret</em> and linked as a secret-backed variable), or <c>null</c> for a plain variable with <c>Value</c>.
/// </summary>
public sealed record ServiceTemplateEnvVar(string Key, string? Value, string? Generate, int Length = 32);

public sealed record ServiceTemplate(
    string Key,
    string Name,
    string Description,
    string Category,
    string DefaultVersion,
    string DefaultImage,
    IReadOnlyList<ServiceTemplateVersion> Versions,
    IReadOnlyList<ServiceTemplatePort> Ports,
    IReadOnlyList<ServiceTemplateVolume> Volumes,
    ServiceTemplateHealthCheck HealthCheck,
    IReadOnlyList<ServiceTemplateEnvVar> Env);

/// <summary>The code-defined catalogue of service templates (<c>GET /service-templates</c>).</summary>
public static class ServiceTemplates
{
    public const string Password = "password";

    public static IReadOnlyList<ServiceTemplate> All { get; } =
    [
        Make("postgres", "PostgreSQL", "Relational database.", "database", "postgres", ["17", "16", "15", "14"],
            [new(5432, false)], [("data", "/var/lib/postgresql/data")], Tcp(5432),
            [Plain("POSTGRES_USER", "postgres"), Secret("POSTGRES_PASSWORD"), Plain("POSTGRES_DB", "app")]),
        Make("mysql", "MySQL", "Relational database.", "database", "mysql", ["8.4", "8.0", "9.1"],
            [new(3306, false)], [("data", "/var/lib/mysql")], Tcp(3306),
            [Secret("MYSQL_ROOT_PASSWORD"), Plain("MYSQL_DATABASE", "app"), Plain("MYSQL_USER", "app"), Secret("MYSQL_PASSWORD")]),
        Make("mariadb", "MariaDB", "Relational database, MySQL compatible.", "database", "mariadb", ["11.4", "10.11", "10.6"],
            [new(3306, false)], [("data", "/var/lib/mysql")], Tcp(3306),
            [Secret("MARIADB_ROOT_PASSWORD"), Plain("MARIADB_DATABASE", "app"), Plain("MARIADB_USER", "app"), Secret("MARIADB_PASSWORD")]),
        Make("redis", "Redis", "In-memory key-value store and cache.", "cache", "redis", ["7.4", "7.2", "6.2"],
            [new(6379, false)], [("data", "/data")], Tcp(6379),
            [Secret("REDIS_PASSWORD")]),
        Make("mongodb", "MongoDB", "Document database.", "database", "mongo", ["8.0", "7.0", "6.0"],
            [new(27017, false)], [("data", "/data/db")], Tcp(27017),
            [Plain("MONGO_INITDB_ROOT_USERNAME", "admin"), Secret("MONGO_INITDB_ROOT_PASSWORD")]),
        Make("influxdb", "InfluxDB", "Time-series database.", "database", "influxdb", ["2.7", "2.6"],
            [new(8086, true)], [("data", "/var/lib/influxdb2"), ("config", "/etc/influxdb2")], Http("/health", 8086),
            [Plain("DOCKER_INFLUXDB_INIT_MODE", "setup"), Plain("DOCKER_INFLUXDB_INIT_USERNAME", "admin"),
             Secret("DOCKER_INFLUXDB_INIT_PASSWORD"), Plain("DOCKER_INFLUXDB_INIT_ORG", "aethera"),
             Plain("DOCKER_INFLUXDB_INIT_BUCKET", "default"), Secret("DOCKER_INFLUXDB_INIT_ADMIN_TOKEN", 48)]),
        Make("grafana", "Grafana", "Dashboards and visualization.", "monitoring", "grafana/grafana", ["11.4.0", "10.4.12"],
            [new(3000, true)], [("data", "/var/lib/grafana")], Http("/api/health", 3000),
            [Plain("GF_SECURITY_ADMIN_USER", "admin"), Secret("GF_SECURITY_ADMIN_PASSWORD")]),
        Make("prometheus", "Prometheus", "Metrics collection and alerting.", "monitoring", "prom/prometheus", ["v3.1.0", "v2.55.1"],
            [new(9090, true)], [("data", "/prometheus")], Http("/-/healthy", 9090), []),
        Make("minio", "MinIO", "S3-compatible object storage.", "storage", "minio/minio", ["RELEASE.2025-02-28T09-55-16Z", "latest"],
            [new(9000, false), new(9001, true)], [("data", "/data")], Http("/minio/health/live", 9000),
            [Plain("MINIO_ROOT_USER", "minio-admin"), Secret("MINIO_ROOT_PASSWORD")]),
    ];

    public static ServiceTemplate? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    private static ServiceTemplate Make(
        string key, string name, string description, string category, string image, string[] versions,
        (int Port, bool IsHttp)[] ports, (string Name, string Mount)[] volumes, ServiceTemplateHealthCheck health,
        ServiceTemplateEnvVar[] env) =>
        new(key, name, description, category, versions[0], $"{image}:{versions[0]}",
            versions.Select((v, i) => new ServiceTemplateVersion(v, $"{image}:{v}", i == 0)).ToList(),
            ports.Select(p => new ServiceTemplatePort(p.Port, "tcp", p.IsHttp)).ToList(),
            volumes.Select(v => new ServiceTemplateVolume(v.Name, v.Mount)).ToList(), health, env);

    private static ServiceTemplateHealthCheck Tcp(int port) => new("tcp", null, port, 10, 5, 5, 20);

    private static ServiceTemplateHealthCheck Http(string path, int port) => new("http", path, port, 15, 5, 5, 30);

    private static ServiceTemplateEnvVar Plain(string key, string value) => new(key, value, null);

    private static ServiceTemplateEnvVar Secret(string key, int length = 32) => new(key, null, Password, length);

    /// <summary>The image for a version of a template (<c>null</c> version = the default).</summary>
    public static ServiceTemplateVersion? Version(ServiceTemplate template, string? version) =>
        version is null
            ? template.Versions.First(v => v.IsDefault)
            : template.Versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

    internal static HealthCheckType ToHealthCheckType(string type) => type switch
    {
        "http" => HealthCheckType.Http,
        "tcp" => HealthCheckType.Tcp,
        "container" => HealthCheckType.Container,
        _ => HealthCheckType.None,
    };
}
