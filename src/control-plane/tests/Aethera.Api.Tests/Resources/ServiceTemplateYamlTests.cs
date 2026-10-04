using Aethera.Api.Features.Resources.Services;

namespace Aethera.Api.Tests.Resources;

/// <summary>The YAML template format (docs/architecture/0008-product-ui.md): parsing, defaults and the errors a template author sees.</summary>
public sealed class ServiceTemplateYamlTests
{
    private const string Minimal = """
        key: demo
        name: Demo
        description: A demo service.
        category: cache
        image: demo/demo
        versions: ["2", "1"]
        ports:
          - port: 8080
            http: true
        volumes:
          - name: data
            path: /data
        healthCheck:
          type: http
          path: /health
        env:
          - key: PLAIN
            value: x
          - key: SECRET
            generate: password
            length: 48
        """;

    [Fact]
    public void A_template_file_becomes_a_template_with_defaults_filled_in()
    {
        var t = ServiceTemplates.Parse(Minimal);

        Assert.Equal("demo", t.Key);
        Assert.Equal("2", t.DefaultVersion);
        Assert.Equal("demo/demo:2", t.DefaultImage);
        Assert.Equal([true, false], t.Versions.Select(v => v.IsDefault));
        Assert.Equal(new ServiceTemplatePort(8080, "tcp", true), t.Ports.Single());
        Assert.Equal(new ServiceTemplateVolume("data", "/data"), t.Volumes.Single());
        // The probe port defaults to the first port, the timings to the http defaults.
        Assert.Equal(new ServiceTemplateHealthCheck("http", "/health", 8080, 15, 5, 5, 30), t.HealthCheck);
        Assert.Equal(new ServiceTemplateEnvVar("SECRET", null, ServiceTemplates.Password, 48), t.Env[1]);
        Assert.Null(t.Command);
    }

    [Theory]
    [InlineData("key: Bad Key", "key")]
    [InlineData("name: x", "key")]
    [InlineData("versions: []", "version")]
    [InlineData("image: not a valid image", "image")]
    [InlineData("ports: [{port: 70000}]", "port")]
    [InlineData("volumes: [{name: d, path: relative}]", "volume")]
    [InlineData("healthCheck: {type: carrier-pigeon}", "healthCheck.type")]
    [InlineData("healthCheck: {type: http}", "path")]
    [InlineData("env: [{key: A}]", "needs a value")]
    [InlineData("env: [{key: A, generate: uuid}]", "generate")]
    public void A_broken_template_names_the_problem(string override_, string expected)
    {
        var lines = Minimal.Split('\n').ToList();
        var field = override_.Split(':')[0];
        var kept = lines.Where(l => !l.StartsWith(field + ":", StringComparison.Ordinal)).ToList();
        if (field is "ports" or "volumes" or "healthCheck" or "env" or "versions") kept = Strip(lines, field);
        var yaml = string.Join('\n', kept) + "\n" + (field == "name" ? "" : override_) + "\n";
        if (field == "name") yaml = string.Join('\n', lines.Where(l => !l.StartsWith("key:", StringComparison.Ordinal))) + "\n";

        var ex = Assert.Throws<ServiceTemplateFormatException>(() => ServiceTemplates.Parse(yaml, "demo.yaml"));
        Assert.Contains("demo.yaml", ex.Message);
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes a top-level key together with its indented block.</summary>
    private static List<string> Strip(List<string> lines, string key)
    {
        var result = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal)) { skipping = true; continue; }
            if (skipping && (line.StartsWith(' ') || line.StartsWith('-'))) continue;
            skipping = false;
            result.Add(line);
        }

        return result;
    }

    [Fact]
    public void Malformed_yaml_is_reported_with_the_file_name()
    {
        var ex = Assert.Throws<ServiceTemplateFormatException>(() => ServiceTemplates.Parse("key: [unclosed", "broken.yaml"));
        Assert.StartsWith("broken.yaml:", ex.Message);
    }

    [Fact]
    public void The_shipped_catalogue_is_unique_in_file_order_and_minio_carries_its_start_command()
    {
        var keys = ServiceTemplates.All.Select(t => t.Key).ToList();
        Assert.Equal("postgres", keys[0]);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(["server", "/data", "--console-address", ":9001"], ServiceTemplates.Find("minio")!.Command);
        Assert.All(ServiceTemplates.All.Where(t => t.Key != "minio"), t => Assert.Null(t.Command));
    }
}
