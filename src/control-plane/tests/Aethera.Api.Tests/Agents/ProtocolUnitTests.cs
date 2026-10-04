using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Aethera.Agent.V1;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Agents.Ingest;
using Aethera.Infrastructure.Agents.Pki;
using Aethera.Infrastructure.Agents.Protocol;
using Aethera.Infrastructure.Agents.Sessions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using D = Aethera.Domain.Transport;
using DomainContainerSpec = Aethera.Domain.Transport.ContainerSpec;
using DomainSecretValue = Aethera.Domain.Transport.SecretValue;
using ProtoContainerSpec = Aethera.Agent.V1.ContainerSpec;
using ProtoCommandStatus = Aethera.Agent.V1.CommandStatus;
using ProtoSecretValue = Aethera.Agent.V1.SecretValue;

namespace Aethera.Api.Tests.Agents;

public sealed class CsrValidatorTests
{
    [Fact]
    public void EcP256_EcP384_and_Rsa3072_are_accepted()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(3072);
        Assert.Equal("EC", CsrValidator.Validate(new CertificateRequest("CN=x", p256, HashAlgorithmName.SHA256).CreateSigningRequestPem()).KeyDescription);
        Assert.Equal("EC", CsrValidator.Validate(new CertificateRequest("CN=x", p384, HashAlgorithmName.SHA384).CreateSigningRequestPem()).KeyDescription);
        Assert.Equal("RSA", CsrValidator.Validate(new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem()).KeyDescription);
    }

    [Fact]
    public void Weak_or_unlisted_keys_are_rejected()
    {
        using var rsa2048 = RSA.Create(2048);
        using var p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        Assert.Throws<CsrValidationException>(() =>
            CsrValidator.Validate(new CertificateRequest("CN=x", rsa2048, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem()));
        Assert.Throws<CsrValidationException>(() => CsrValidator.Validate(new CertificateRequest("CN=x", p521, HashAlgorithmName.SHA512).CreateSigningRequestPem()));
    }

    [Fact]
    public void A_request_without_proof_of_possession_is_rejected()
    {
        // The public key of A, signed by the private key of B: whoever sent this does not hold A's private key.
        using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=x"), new PublicKey(a), HashAlgorithmName.SHA256);
        var forged = request.CreateSigningRequestPem(X509SignatureGenerator.CreateForECDsa(b));
        Assert.Throws<CsrValidationException>(() => CsrValidator.Validate(forged));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a csr")]
    [InlineData("-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----")]
    public void Garbage_is_rejected(string? pem) => Assert.Throws<CsrValidationException>(() => CsrValidator.Validate(pem));

    [Fact]
    public void An_oversized_request_is_rejected() =>
        Assert.Throws<CsrValidationException>(() => CsrValidator.Validate(new string('A', CsrValidator.MaxCsrChars + 1)));

    [Fact]
    public void Only_the_public_key_is_carried_over()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=admin,O=evil", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("spiffe://aethera/server/00000000-0000-0000-0000-000000000001"));
        request.CertificateExtensions.Add(san.Build());
        var validated = CsrValidator.Validate(request.CreateSigningRequestPem());
        // ValidatedCsr exposes nothing but the key: subject and SANs cannot leak into the certificate.
        Assert.Equal(typeof(ValidatedCsr).GetProperties().Select(p => p.Name).Order(), new[] { nameof(ValidatedCsr.KeyDescription), nameof(ValidatedCsr.PublicKey) });
        Assert.NotNull(validated.PublicKey);
    }
}

public sealed class AgentVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.0", true)]
    [InlineData("v1.2.3-rc1+build", "1.2.3", true)]
    [InlineData("1.1.9", "1.2.0", false)]
    [InlineData("0.9", "1.0.0", false)]
    [InlineData("dev", "1.0.0", false)]
    [InlineData("dev", "0.0.0", true)]
    [InlineData("", "0.0.0", true)]
    [InlineData("2.0.0", "garbage", true)]
    public void IsAtLeast(string agent, string minimum, bool expected) => Assert.Equal(expected, AgentVersions.IsAtLeast(agent, minimum));
}

public sealed class CommandMapperTests
{
    private static readonly CommandMapper Mapper = CommandMapper.Default;

    private static ComposeProjectSpec Project => new("shop", "services:\n  web:\n    image: nginx\n") { OverrideFile = "labels: {}" };

    /// <summary>One sample of every command of the allowlist. A new command type fails <see cref="Every_command_type_has_a_sample"/> until it is added.</summary>
    public static IEnumerable<object[]> Samples() =>
        new object[][]
        {
            [new ContainerCreateCommand(new DomainContainerSpec("nginx:1.27", "web") { Env = [EnvVarSpec.OfPlain("A", "1"), EnvVarSpec.OfSecret("TOKEN", "t0k3n-value")], Ports = [new PortMappingSpec(80, 8080)] }, true, true, new RegistryCredentials("ghcr.io", "u", new DomainSecretValue("pw-secret")), true), Command.RequestOneofCase.ContainerCreate],
            [new ContainerStartCommand("web"), Command.RequestOneofCase.ContainerStart],
            [new ContainerStopCommand("web", TimeSpan.FromSeconds(5)), Command.RequestOneofCase.ContainerStop],
            [new ContainerRestartCommand("web"), Command.RequestOneofCase.ContainerRestart],
            [new ContainerRemoveCommand("web", true, true), Command.RequestOneofCase.ContainerRemove],
            [new ContainerInspectCommand("web", true), Command.RequestOneofCase.ContainerInspect],
            [new ContainerListCommand(true, ["a=b"], "web"), Command.RequestOneofCase.ContainerList],
            [new ImagePullCommand("nginx:1.27"), Command.RequestOneofCase.ImagePull],
            [new ImageListCommand(), Command.RequestOneofCase.ImageList],
            [new ImageRemoveCommand("nginx"), Command.RequestOneofCase.ImageRemove],
            [new ImagePruneCommand(true, TimeSpan.FromHours(1), null, ["keep:1"]), Command.RequestOneofCase.ImagePrune],
            [new ImageInspectCommand("nginx"), Command.RequestOneofCase.ImageInspect],
            [new VolumeCreateCommand("data", null, null, new Dictionary<string, string> { ["k"] = "v" }), Command.RequestOneofCase.VolumeCreate],
            [new VolumeListCommand(), Command.RequestOneofCase.VolumeList],
            [new VolumeRemoveCommand("data"), Command.RequestOneofCase.VolumeRemove],
            [new VolumePruneCommand(["k"], false), Command.RequestOneofCase.VolumePrune],
            [new NetworkCreateCommand("net", Internal: true), Command.RequestOneofCase.NetworkCreate],
            [new NetworkListCommand(), Command.RequestOneofCase.NetworkList],
            [new NetworkRemoveCommand("net"), Command.RequestOneofCase.NetworkRemove],
            [new NetworkConnectCommand("net", "web", ["alias"]), Command.RequestOneofCase.NetworkConnect],
            [new NetworkDisconnectCommand("net", "web"), Command.RequestOneofCase.NetworkDisconnect],
            [new NetworkPruneCommand(), Command.RequestOneofCase.NetworkPrune],
            [new ComposeUpCommand(Project, ["web"]), Command.RequestOneofCase.ComposeUp],
            [new ComposeDownCommand(Project, true), Command.RequestOneofCase.ComposeDown],
            [new ComposePsCommand(Project), Command.RequestOneofCase.ComposePs],
            [new ComposePullCommand(Project), Command.RequestOneofCase.ComposePull],
            [new BuildImageCommand(new BuildSpec("b1", BuildEngineKind.Dockerfile, ["app:1"]) { Git = new GitSourceSpec("https://example.com/r.git", Credentials: new GitCredentialSpec("cred", "u", new DomainSecretValue("gh-token-value"))), Secrets = [new BuildSecretSpec("npm", new DomainSecretValue("npm-secret-value"))] }), Command.RequestOneofCase.Build],
            [new BuildDetectCommand(new GitSourceSpec("https://example.com/r.git")), Command.RequestOneofCase.BuildDetect],
            [new LogStreamStartCommand("s1", "web"), Command.RequestOneofCase.LogStreamStart],
            [new LogStreamStopCommand("s1"), Command.RequestOneofCase.LogStreamStop],
            [new HealthProbeCommand(new HttpProbeTarget("http://127.0.0.1/health"), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 3), Command.RequestOneofCase.HealthProbe],
            [new ProxyEnsureCommand("traefik", [1, 2, 3]), Command.RequestOneofCase.ProxyEnsure],
            [new DiscoveryRefreshCommand(), Command.RequestOneofCase.DiscoveryRefresh],
            [new SystemPruneCommand(StoppedContainers: true, KeepImages: ["a:1"]), Command.RequestOneofCase.SystemPrune],
            [new AgentSelfUpdateCommand("https://example.com/agent", new string('a', 64), "1.2.3"), Command.RequestOneofCase.AgentSelfUpdate],
        };

    [Fact]
    public void Every_command_type_has_a_sample()
    {
        var sampled = Samples().Select(s => s[0].GetType()).ToHashSet();
        Assert.Equal(Mapper.CommandTypes.OrderBy(t => t.Name), sampled.OrderBy(t => t.Name));
    }

    [Fact]
    public void The_mapper_covers_the_whole_protocol_allowlist_and_nothing_else()
    {
        var arms = Command.Descriptor.Oneofs.Single(o => o.Name == "request").Fields.Count;
        Assert.Equal(arms, Mapper.CommandTypes.Count);

        // Every concrete domain command is mapped (no command can exist that the agent could not receive) ...
        var domainCommands = typeof(IServerCommand<>).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IServerCommand<>)))
            .ToHashSet();
        Assert.Equal(domainCommands.OrderBy(t => t.Name), Mapper.CommandTypes.OrderBy(t => t.Name));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_command_maps_to_exactly_its_request_arm(object command, Command.RequestOneofCase expected)
    {
        var options = new CommandOptions { IdempotencyKey = "job:step:0", JobId = Guid.NewGuid(), TraceParent = "00-abc-def-01" };
        var proto = Mapper.ToProto(command, "cmd-1", options, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(expected, proto.RequestCase);
        Assert.Equal("cmd-1", proto.CommandId);
        Assert.Equal("job:step:0", proto.IdempotencyKey);
        Assert.Equal(options.JobId!.Value.ToString("D"), proto.JobId);
        Assert.Equal(proto, Command.Parser.ParseFrom(proto.ToByteArray())); // survives the wire
    }

    [Fact]
    public void A_command_outside_the_allowlist_cannot_be_mapped() =>
        Assert.Equal(TransportErrors.Unsupported, Assert.Throws<ServerTransportException>(() => Mapper.For(typeof(string))).Code);

    [Fact]
    public void Results_come_back_typed()
    {
        var container = new ContainerInfo { Id = "c1", Name = "web", Image = "nginx", State = ContainerState.Running, Health = ContainerHealth.Healthy, RestartCount = 2, ExitCode = 0 };
        container.Labels["aethera.application.id"] = "x";
        container.Ports.Add(new PortMapping { ContainerPort = 80, HostPort = 8080, Protocol = PortProtocol.Tcp });

        var started = (D.DockerContainer)Mapper.For(typeof(ContainerStartCommand)).Read(new CommandResult { ContainerState = new ContainerStateResult { Container = container } });
        Assert.Equal(ContainerRunState.Running, started.State);
        Assert.Equal(ContainerHealthState.Healthy, started.Health);
        Assert.Equal(2, started.RestartCount);
        Assert.Equal("x", started.Labels["aethera.application.id"]);
        Assert.Equal(8080, Assert.Single(started.Ports).HostPort);

        var list = (IReadOnlyList<D.DockerContainer>)Mapper.For(typeof(ContainerListCommand)).Read(new CommandResult { ContainerList = new ContainerListResult { Containers = { container, container } } });
        Assert.Equal(2, list.Count);

        var prune = (PruneOutcome)Mapper.For(typeof(SystemPruneCommand)).Read(new CommandResult { Prune = new PruneResult { Deleted = { "a", "b" }, SpaceReclaimedBytes = 99 } });
        Assert.Equal(["a", "b"], prune.Deleted);
        Assert.Equal(99, prune.SpaceReclaimedBytes);

        var health = (HealthProbeOutcome)Mapper.For(typeof(HealthProbeCommand)).Read(new CommandResult
            { HealthProbe = new HealthProbeResult { Healthy = true, Attempts = 2, HttpStatus = 204, Latency = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(40)) } });
        Assert.True(health.Healthy);
        Assert.Equal(TimeSpan.FromMilliseconds(40), health.Latency);

        var build = (BuildOutcome)Mapper.For(typeof(BuildImageCommand)).Read(new CommandResult { Build = new BuildResult { BuildId = "b1", Digest = "sha256:1", Tags = { "app:1" }, CacheHit = true } });
        Assert.Equal("sha256:1", build.Digest);
        Assert.True(build.CacheHit);

        // Commands with nothing to return accept an empty result or none at all.
        Assert.Same(D.Unit.Value, Mapper.For(typeof(ContainerRemoveCommand)).Read(new CommandResult { Empty = new EmptyResult() }));
        Assert.Same(D.Unit.Value, Mapper.For(typeof(ContainerRemoveCommand)).Read(new CommandResult()));
    }

    [Fact]
    public void A_result_of_the_wrong_shape_is_reported()
    {
        var read = Mapper.For(typeof(ContainerStartCommand)).Read;
        Assert.Throws<UnexpectedResultException>(() => read(new CommandResult { Prune = new PruneResult() }));
        Assert.Throws<UnexpectedResultException>(() => read(new CommandResult()));
    }

    [Fact]
    public void Capabilities_gate_the_commands_that_need_one()
    {
        Assert.Equal("compose.v2", Mapper.CapabilityOf(new ComposeUpCommand(Project)));
        Assert.Equal("logs.follow", Mapper.CapabilityOf(new LogStreamStartCommand("s", "c")));
        Assert.Equal("selfupdate", Mapper.CapabilityOf(new AgentSelfUpdateCommand("u", "h", "v")));
        Assert.Equal("build.nixpacks", Mapper.CapabilityOf(new BuildImageCommand(new BuildSpec("b", BuildEngineKind.Nixpacks, ["t"]))));
        Assert.Null(Mapper.CapabilityOf(new BuildImageCommand(new BuildSpec("b", BuildEngineKind.Image, ["t"]))));
        Assert.Null(Mapper.CapabilityOf(new ContainerListCommand()));
    }

    [Fact]
    public void Domain_enums_equal_the_protocol_numbers_by_name()
    {
        AssertSame<ContainerRunState, ContainerState>();
        AssertSame<ContainerHealthState, ContainerHealth>();
        AssertSame<PortProtocolKind, PortProtocol>();
        AssertSame<MountKind, MountType>();
        AssertSame<RestartPolicyKind, RestartPolicyName>();
        AssertSame<BuildEngineKind, BuildEngine>();
        AssertSame<ComposePullPolicyKind, ComposePullPolicy>();
        AssertSame<D.CommandStatus, ProtoCommandStatus>();
        AssertSame<CommandErrorCode, ErrorCode>();
    }

    private static void AssertSame<TDomain, TProto>() where TDomain : struct, System.Enum where TProto : struct, System.Enum
    {
        foreach (var name in Enum.GetNames<TDomain>().Where(n => n is not ("None" or "Unspecified")))
        {
            Assert.True(Enum.TryParse<TProto>(name, out var proto), $"{typeof(TProto).Name} has no {name}");
            Assert.Equal(Convert.ToInt32(Enum.Parse<TDomain>(name)), Convert.ToInt32(proto));
        }
    }
}

public sealed class CommandRedactionTests
{
    private static Command SecretCommand()
    {
        var command = new Command { CommandId = "c1", ContainerCreate = new ContainerCreate { Spec = new ProtoContainerSpec { Image = "nginx", Name = "web" }, PullAuth = new RegistryAuth { Server = "ghcr.io", Username = "u", Password = new ProtoSecretValue { Value = "registry-pw-123" } } } };
        command.ContainerCreate.Spec.Env.Add(new EnvVar { Name = "PLAIN", Plain = "visible" });
        command.ContainerCreate.Spec.Env.Add(new EnvVar { Name = "TOKEN", Secret = new ProtoSecretValue { Value = "env-secret-456" } });
        return command;
    }

    [Fact]
    public void Secret_values_never_appear_in_the_log_string()
    {
        var text = CommandRedaction.ToLogString(SecretCommand());
        Assert.DoesNotContain("registry-pw-123", text);
        Assert.DoesNotContain("env-secret-456", text);
        Assert.Contains(CommandRedaction.Placeholder, text);
        Assert.Contains("visible", text); // plain values stay: they are not secrets
        Assert.Contains("ghcr.io", text);
    }

    [Fact]
    public void The_original_message_is_not_modified()
    {
        var command = SecretCommand();
        _ = CommandRedaction.ToLogString(command);
        Assert.Equal("registry-pw-123", command.ContainerCreate.PullAuth.Password.Value);
    }

    [Fact]
    public void Secrets_are_found_by_type_wherever_they_sit()
    {
        var found = CommandRedaction.CollectSecrets(SecretCommand());
        Assert.Equal(["env-secret-456", "registry-pw-123"], found.Order(StringComparer.Ordinal));

        var build = new Command { Build = new BuildRequest { BuildId = "b", Git = new GitSource { Url = "u", Credentials = new GitCredentials { SshKey = new SshKeyCredential { PrivateKey = new ProtoSecretValue { Value = "-----BEGIN KEY-----" } } } } } };
        build.Build.Secrets.Add(new BuildSecret { Id = "npm", Value = new ProtoSecretValue { Value = "npm-secret" } });
        Assert.Equal(["-----BEGIN KEY-----", "npm-secret"], CommandRedaction.CollectSecrets(build).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Compose_bodies_are_reduced_to_length_and_hash()
    {
        const string body = "services:\n  db:\n    environment:\n      PASSWORD: embedded-compose-secret\n";
        var command = new Command { ComposeUp = new ComposeUp { Project = new ComposeProject { ProjectName = "shop", ComposeFile = body, OverrideFile = "labels: {x: embedded-override-secret}" } } };
        var text = CommandRedaction.ToLogString(command);
        Assert.DoesNotContain("embedded-compose-secret", text);
        Assert.DoesNotContain("embedded-override-secret", text);
        Assert.Contains($"{body.Length} chars", text);
        Assert.Contains("sha256:", text);
        Assert.Contains("shop", text);
    }

    [Fact]
    public void Proxy_configuration_is_reduced_to_its_size()
    {
        var command = new Command { ProxyEnsure = new ProxyEnsure { Provider = "traefik", Config = ByteString.CopyFromUtf8("token: dns-challenge-secret") } };
        var text = CommandRedaction.ToLogString(command);
        Assert.DoesNotContain("dns-challenge-secret", text);
        Assert.Contains("27 bytes", text);
    }

    [Fact]
    public void The_domain_secret_type_does_not_print_its_value()
    {
        var secret = new DomainSecretValue("super-secret");
        Assert.DoesNotContain("super-secret", secret.ToString());
        Assert.DoesNotContain("super-secret", EnvVarSpec.OfSecret("A", "super-secret").ToString());
        Assert.DoesNotContain("super-secret", new RegistryCredentials("r", "u", secret).ToString());
        Assert.DoesNotContain("super-secret", new ContainerCreateCommand(new DomainContainerSpec("i", "n") { Env = [EnvVarSpec.OfSecret("A", "super-secret")] }).ToString());
    }

    /// <summary>
    /// ADR 0002 "Threats": the descriptor walk. A string or bytes field named like a secret that is not a SecretValue could leak through the
    /// type-based redactor, so every such field has to be listed here after a review.
    /// </summary>
    [Fact]
    public void No_secret_looking_string_field_exists_outside_SecretValue()
    {
        var reviewedNotSecret = new HashSet<string>
        {
            "aethera.agent.v1.Command.idempotency_key",          // an operation id, not a credential
            "aethera.agent.v1.BuildCacheSettings.cache_key",     // a cache namespace
        };
        var pattern = new Regex("password|token|key|secret", RegexOptions.IgnoreCase);
        var files = new[]
        {
            AgentReflection.Descriptor, CommandsReflection.Descriptor, CommonReflection.Descriptor, DiscoveryReflection.Descriptor, DockerReflection.Descriptor,
            EnrollmentReflection.Descriptor, MetricsReflection.Descriptor, BuildReflection.Descriptor,
        };

        var offenders = new List<string>();
        void Visit(MessageDescriptor message)
        {
            if (message.FullName == ProtoSecretValue.Descriptor.FullName) return; // the container of the secret itself
            foreach (var field in message.Fields.InFieldNumberOrder())
                if (field.FieldType is FieldType.String or FieldType.Bytes && pattern.IsMatch(field.Name) && !reviewedNotSecret.Contains($"{message.FullName}.{field.Name}"))
                    offenders.Add($"{message.FullName}.{field.Name}");
            var mapEntries = message.Fields.InDeclarationOrder().Where(f => f.IsMap).Select(f => f.MessageType.FullName).ToHashSet();
            foreach (var nested in message.NestedTypes.Where(n => !mapEntries.Contains(n.FullName))) Visit(nested); // map<string,string> entries are data, not credentials
        }

        foreach (var file in files)
            foreach (var message in file.MessageTypes) Visit(message);
        Assert.Empty(offenders);
    }
}

public sealed class MetricsMappingTests
{
    [Fact]
    public void Host_and_container_rows_are_built_from_a_report()
    {
        var workload = Guid.NewGuid();
        var report = new MetricsReport
        {
            Host = new HostMetrics
            {
                CpuPercent = 12.5, Load1 = 1, Load5 = 2, Load15 = 3, MemoryTotalBytes = 1000, MemoryUsedBytes = 400,
                Disks = { new DiskUsage { MountPoint = "/boot", TotalBytes = 10, UsedBytes = 1 }, new DiskUsage { MountPoint = "/", TotalBytes = 500, UsedBytes = 250 } },
                Interfaces = { new NetworkInterfaceStats { Name = "lo", RxBytes = 999, TxBytes = 999 }, new NetworkInterfaceStats { Name = "eth0", RxBytes = 100, TxBytes = 50 }, new NetworkInterfaceStats { Name = "eth1", RxBytes = 1, TxBytes = 2 } },
            },
        };
        var container = new ContainerMetrics { ContainerId = "abc", CpuPercent = 200, MemoryUsedBytes = 64, MemoryLimitBytes = 0, NetRxBytes = 7, NetTxBytes = 8 };
        container.Labels["aethera.application.id"] = workload.ToString();
        report.Containers.Add(container);
        report.Containers.Add(new ContainerMetrics { ContainerId = "" }); // ignored

        var rows = AgentMetricsIngestor.Map(Guid.NewGuid(), report, DateTimeOffset.UtcNow, new HashSet<Guid> { workload });

        Assert.Equal(2, rows.Count);
        var host = rows.Single(r => r.ContainerId is null);
        Assert.Equal(12.5, host.CpuPercent);
        Assert.Equal(250, host.DiskUsedBytes); // the root file system, not the first disk
        Assert.Equal(500, host.DiskTotalBytes);
        Assert.Equal(101, host.NetRxBytes); // loopback excluded
        Assert.Equal(52, host.NetTxBytes);
        var c = rows.Single(r => r.ContainerId == "abc");
        Assert.Equal(workload, c.WorkloadId);
        Assert.Null(c.MemoryTotalBytes); // 0 = no limit
        Assert.All(rows, r => Assert.Equal(Aethera.Domain.MetricResolution.Raw, r.Resolution));
    }

    [Fact]
    public void A_container_label_for_a_foreign_workload_is_not_trusted()
    {
        var report = new MetricsReport();
        var container = new ContainerMetrics { ContainerId = "abc" };
        container.Labels["aethera.application.id"] = Guid.NewGuid().ToString();
        report.Containers.Add(container);
        var row = Assert.Single(AgentMetricsIngestor.Map(Guid.NewGuid(), report, DateTimeOffset.UtcNow, new HashSet<Guid>()));
        Assert.Null(row.WorkloadId);
    }

    [Fact]
    public void Retention_buckets_are_aligned()
    {
        var t = new DateTimeOffset(2026, 10, 3, 14, 7, 31, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 5, 0, TimeSpan.Zero), Aethera.Infrastructure.Agents.Monitoring.MetricsRetentionService.Align(t, TimeSpan.FromMinutes(5)));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero), Aethera.Infrastructure.Agents.Monitoring.MetricsRetentionService.Align(t, TimeSpan.FromHours(1)));
    }
}
