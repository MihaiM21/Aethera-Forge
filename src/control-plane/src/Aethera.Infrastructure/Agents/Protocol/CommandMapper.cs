using Aethera.Domain.Transport;
using Google.Protobuf;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using P = Aethera.Agent.V1;

namespace Aethera.Infrastructure.Agents.Protocol;

/// <summary>The agent answered with a result of the wrong shape for the command.</summary>
public sealed class UnexpectedResultException(string message) : Exception(message);

/// <summary>
/// Maps every domain command of the allowlist to its <c>Command.request</c> arm and its <c>CommandResult.result</c> arm back to the typed
/// domain result (ADR 0002 "IServerTransport": "a mapper converts to/from Aethera.Agent.V1, so the engine never depends on generated code").
/// </summary>
public sealed class CommandMapper
{
    public static readonly CommandMapper Default = new();

    /// <param name="Fill">Sets the request arm.</param>
    /// <param name="Read">Reads the typed result of a succeeded command.</param>
    /// <param name="Capability">The agent capability string (<c>Hello.capabilities</c>) the command needs, or null.</param>
    public sealed record Entry(Action<P.Command, object> Fill, Func<P.CommandResult, object> Read, Func<object, string?> Capability);

    private readonly Dictionary<Type, Entry> _entries = [];

    public CommandMapper()
    {
        // containers
        Add<ContainerCreateCommand, ContainerCreated>((c, m) => c.ContainerCreate = new P.ContainerCreate
            { Spec = m.Spec.ToProto(), Start = m.Start, PullIfMissing = m.PullIfMissing, PullAuth = m.PullAuth.ToProto(), ReplaceExisting = m.ReplaceExisting },
            r => Expect(r, P.CommandResult.ResultOneofCase.ContainerCreate, x => new ContainerCreated(
                x.ContainerCreate.ContainerId, x.ContainerCreate.Name, ProtoConvert.EnumOf<ContainerRunState>((int)x.ContainerCreate.State), x.ContainerCreate.Warnings.ToList())));
        Add<ContainerStartCommand, DockerContainer>((c, m) => c.ContainerStart = new P.ContainerStart { Container = m.Container }, ReadState);
        Add<ContainerStopCommand, DockerContainer>((c, m) => c.ContainerStop = new P.ContainerStop { Container = m.Container, Timeout = m.Timeout.ToProto() }, ReadState);
        Add<ContainerRestartCommand, DockerContainer>((c, m) => c.ContainerRestart = new P.ContainerRestart { Container = m.Container, Timeout = m.Timeout.ToProto() }, ReadState);
        Add<ContainerRemoveCommand, Unit>((c, m) => c.ContainerRemove = new P.ContainerRemove { Container = m.Container, Force = m.Force, RemoveAnonymousVolumes = m.RemoveAnonymousVolumes }, ReadUnit);
        Add<ContainerInspectCommand, DockerContainer>((c, m) => c.ContainerInspect = new P.ContainerInspect { Container = m.Container, IncludeRaw = m.IncludeRaw },
            r => Expect(r, P.CommandResult.ResultOneofCase.ContainerInspect, x => x.ContainerInspect.Container.ToDomain()));
        Add<ContainerListCommand, IReadOnlyList<DockerContainer>>((c, m) =>
            {
                c.ContainerList = new P.ContainerList { All = m.All, NameFilter = m.NameFilter ?? "" };
                if (m.LabelFilters is not null) c.ContainerList.LabelFilters.Add(m.LabelFilters);
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.ContainerList, x => (IReadOnlyList<DockerContainer>)x.ContainerList.Containers.Select(ProtoConvert.ToDomain).ToList()));

        // images
        Add<ImagePullCommand, ImagePulled>((c, m) => c.ImagePull = new P.ImagePull { Reference = m.Reference, Auth = m.Auth.ToProto(), Platform = m.Platform ?? "" },
            r => Expect(r, P.CommandResult.ResultOneofCase.ImagePull, x => new ImagePulled(
                x.ImagePull.ImageId, x.ImagePull.Digest, x.ImagePull.SizeBytes, x.ImagePull.RepoDigests.ToList(), x.ImagePull.AlreadyPresent)));
        Add<ImageListCommand, IReadOnlyList<DockerImage>>((c, m) =>
            {
                c.ImageList = new P.ImageList { IncludeIntermediate = m.IncludeIntermediate, ReferenceFilter = m.ReferenceFilter ?? "" };
                if (m.LabelFilters is not null) c.ImageList.LabelFilters.Add(m.LabelFilters);
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.ImageList, x => (IReadOnlyList<DockerImage>)x.ImageList.Images.Select(ProtoConvert.ToDomain).ToList()));
        Add<ImageRemoveCommand, Unit>((c, m) => c.ImageRemove = new P.ImageRemove { Image = m.Image, Force = m.Force }, ReadUnit);
        Add<ImagePruneCommand, PruneOutcome>((c, m) =>
            {
                c.ImagePrune = new P.ImagePrune { AllUnused = m.AllUnused, OlderThan = m.OlderThan.ToProto() };
                if (m.LabelFilters is not null) c.ImagePrune.LabelFilters.Add(m.LabelFilters);
                if (m.Keep is not null) c.ImagePrune.Keep.Add(m.Keep);
            }, ReadPrune);
        Add<ImageInspectCommand, DockerImage>((c, m) => c.ImageInspect = new P.ImageInspect { Image = m.Image },
            r => Expect(r, P.CommandResult.ResultOneofCase.ImageInspect, x => x.ImageInspect.Image.ToDomain()));

        // volumes
        Add<VolumeCreateCommand, DockerVolume>((c, m) =>
            {
                c.VolumeCreate = new P.VolumeCreate { Name = m.VolumeName, Driver = m.Driver ?? "" };
                foreach (var (k, v) in m.DriverOptions ?? new Dictionary<string, string>()) c.VolumeCreate.DriverOptions[k] = v;
                foreach (var (k, v) in m.Labels ?? new Dictionary<string, string>()) c.VolumeCreate.Labels[k] = v;
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.VolumeCreate, x => x.VolumeCreate.Volume.ToDomain()));
        Add<VolumeListCommand, IReadOnlyList<DockerVolume>>((c, m) =>
            {
                c.VolumeList = new P.VolumeList { IncludeSizes = m.IncludeSizes };
                if (m.LabelFilters is not null) c.VolumeList.LabelFilters.Add(m.LabelFilters);
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.VolumeList, x => (IReadOnlyList<DockerVolume>)x.VolumeList.Volumes.Select(ProtoConvert.ToDomain).ToList()));
        Add<VolumeRemoveCommand, Unit>((c, m) => c.VolumeRemove = new P.VolumeRemove { Name = m.VolumeName, Force = m.Force }, ReadUnit);
        Add<VolumePruneCommand, PruneOutcome>((c, m) =>
            {
                c.VolumePrune = new P.VolumePrune { IncludeUnlabelled = m.IncludeUnlabelled };
                if (m.LabelFilters is not null) c.VolumePrune.LabelFilters.Add(m.LabelFilters);
            }, ReadPrune);

        // networks
        Add<NetworkCreateCommand, DockerNetwork>((c, m) => c.NetworkCreate = m.ToProto(),
            r => Expect(r, P.CommandResult.ResultOneofCase.NetworkCreate, x => x.NetworkCreate.Network.ToDomain()));
        Add<NetworkListCommand, IReadOnlyList<DockerNetwork>>((c, m) =>
            {
                c.NetworkList = new P.NetworkList();
                if (m.LabelFilters is not null) c.NetworkList.LabelFilters.Add(m.LabelFilters);
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.NetworkList, x => (IReadOnlyList<DockerNetwork>)x.NetworkList.Networks.Select(ProtoConvert.ToDomain).ToList()));
        Add<NetworkRemoveCommand, Unit>((c, m) => c.NetworkRemove = new P.NetworkRemove { Network = m.Network }, ReadUnit);
        Add<NetworkConnectCommand, Unit>((c, m) =>
            {
                c.NetworkConnect = new P.NetworkConnect { Network = m.Network, Container = m.Container, Ipv4Address = m.Ipv4Address ?? "" };
                if (m.Aliases is not null) c.NetworkConnect.Aliases.Add(m.Aliases);
            }, ReadUnit);
        Add<NetworkDisconnectCommand, Unit>((c, m) => c.NetworkDisconnect = new P.NetworkDisconnect { Network = m.Network, Container = m.Container, Force = m.Force }, ReadUnit);
        Add<NetworkPruneCommand, PruneOutcome>((c, m) =>
            {
                c.NetworkPrune = new P.NetworkPrune { OlderThan = m.OlderThan.ToProto() };
                if (m.LabelFilters is not null) c.NetworkPrune.LabelFilters.Add(m.LabelFilters);
            }, ReadPrune);

        // compose
        Add<ComposeUpCommand, ComposeOutcome>((c, m) =>
            {
                c.ComposeUp = new P.ComposeUp
                {
                    Project = m.Project.ToProto(), PullPolicy = (P.ComposePullPolicy)(int)m.PullPolicy, Build = m.Build, ForceRecreate = m.ForceRecreate,
                    RemoveOrphans = m.RemoveOrphans, Wait = m.Wait, WaitTimeout = m.WaitTimeout.ToProto(),
                };
                if (m.Services is not null) c.ComposeUp.Services.Add(m.Services);
            },
            ReadCompose, _ => "compose.v2");
        Add<ComposeDownCommand, ComposeOutcome>((c, m) => c.ComposeDown = new P.ComposeDown
            { Project = m.Project.ToProto(), RemoveVolumes = m.RemoveVolumes, RemoveOrphans = m.RemoveOrphans, Timeout = m.Timeout.ToProto() },
            ReadCompose, _ => "compose.v2");
        Add<ComposePsCommand, ComposeOutcome>((c, m) => c.ComposePs = new P.ComposePs { Project = m.Project.ToProto(), All = m.All }, ReadCompose, _ => "compose.v2");
        Add<ComposePullCommand, Unit>((c, m) =>
            {
                c.ComposePull = new P.ComposePull { Project = m.Project.ToProto() };
                if (m.Services is not null) c.ComposePull.Services.Add(m.Services);
            }, ReadUnit, _ => "compose.v2");

        // builds
        Add<BuildImageCommand, BuildOutcome>((c, m) => c.Build = m.Spec.ToProto(),
            r => Expect(r, P.CommandResult.ResultOneofCase.Build, x => x.Build.ToDomain()),
            m => m.Spec.Engine switch { BuildEngineKind.Dockerfile => "build.dockerfile", BuildEngineKind.Nixpacks => "build.nixpacks", _ => null });
        Add<BuildDetectCommand, BuildDetection>((c, m) => c.BuildDetect = new P.BuildDetect { Git = m.Git.ToProto(), ContextPath = m.ContextPath ?? "" },
            r => Expect(r, P.CommandResult.ResultOneofCase.BuildDetect, x => x.BuildDetect.ToDomain()));

        // logs
        Add<LogStreamStartCommand, LogStreamStarted>((c, m) => c.LogStreamStart = new P.LogStreamStart
            {
                StreamId = m.StreamId, Container = m.Container, Follow = m.Follow, Since = m.Since.ToProto(), Tail = m.Tail,
                IncludeStdout = m.IncludeStdout, IncludeStderr = m.IncludeStderr,
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.LogStreamStart, x => new LogStreamStarted(x.LogStreamStart.StreamId, x.LogStreamStart.Started)),
            _ => "logs.follow");
        Add<LogStreamStopCommand, Unit>((c, m) => c.LogStreamStop = new P.LogStreamStop { StreamId = m.StreamId }, ReadUnit, _ => "logs.follow");

        // health, proxy, system
        Add<HealthProbeCommand, HealthProbeOutcome>((c, m) => c.HealthProbe = m.ToProto(),
            r => Expect(r, P.CommandResult.ResultOneofCase.HealthProbe, x => x.HealthProbe.ToDomain()));
        Add<ProxyEnsureCommand, ProxyEnsured>((c, m) =>
            {
                c.ProxyEnsure = new P.ProxyEnsure
                {
                    Provider = m.Provider, Image = m.Image ?? "", Version = m.Version ?? "", Config = ByteString.CopyFrom(m.Config), Network = m.Network ?? "",
                    RestartOnChange = m.RestartOnChange,
                };
                c.ProxyEnsure.Env.Add(m.Env.ToProto());
            },
            r => Expect(r, P.CommandResult.ResultOneofCase.ProxyEnsure, x => new ProxyEnsured(
                x.ProxyEnsure.Provider, x.ProxyEnsure.ContainerId, x.ProxyEnsure.Version, x.ProxyEnsure.Running, x.ProxyEnsure.Changed, x.ProxyEnsure.ConfigSha256)),
            m => m.Provider == "traefik" ? "proxy.traefik" : null);
        Add<DiscoveryRefreshCommand, DiscoveryInfo>((c, _) => c.DiscoveryRefresh = new P.DiscoveryRefresh(),
            r => Expect(r, P.CommandResult.ResultOneofCase.Discovery, x => x.Discovery.ToDomain()));
        Add<SystemPruneCommand, PruneOutcome>((c, m) =>
            {
                c.SystemPrune = new P.SystemPrune
                {
                    StoppedContainers = m.StoppedContainers, DanglingImages = m.DanglingImages, UnusedImages = m.UnusedImages, UnusedNetworks = m.UnusedNetworks,
                    BuildCache = m.BuildCache, Volumes = m.Volumes, OlderThan = m.OlderThan.ToProto(),
                };
                if (m.KeepImages is not null) c.SystemPrune.KeepImages.Add(m.KeepImages);
            }, ReadPrune);
        Add<AgentSelfUpdateCommand, SelfUpdateOutcome>((c, m) => c.AgentSelfUpdate = new P.AgentSelfUpdate
            { Url = m.Url, Sha256 = m.Sha256, TargetVersion = m.TargetVersion, Signature = ByteString.CopyFrom(m.Signature ?? []) },
            r => Expect(r, P.CommandResult.ResultOneofCase.AgentSelfUpdate, x => new SelfUpdateOutcome(
                x.AgentSelfUpdate.PreviousVersion, x.AgentSelfUpdate.NewVersion, x.AgentSelfUpdate.Restarting)),
            _ => "selfupdate");
    }

    /// <summary>The mapping of a command type. Throws for a type outside the allowlist (including any test double).</summary>
    public Entry For(Type commandType) =>
        _entries.TryGetValue(commandType, out var entry)
            ? entry
            : throw new ServerTransportException(TransportErrors.Unsupported, $"The command type {commandType.Name} is not part of the agent allowlist.");

    public IReadOnlyCollection<Type> CommandTypes => _entries.Keys;

    /// <summary>Builds the envelope; <c>command_id</c>/<c>idempotency_key</c>/<c>deadline</c> are filled by the caller.</summary>
    public P.Command ToProto(object command, string commandId, CommandOptions options, DateTimeOffset deadline)
    {
        var message = new P.Command
        {
            CommandId = commandId, IdempotencyKey = options.IdempotencyKey, Deadline = Timestamp.FromDateTimeOffset(deadline),
            JobId = options.JobId?.ToString("D") ?? "", Traceparent = options.TraceParent ?? "",
        };
        For(command.GetType()).Fill(message, command);
        return message;
    }

    public string? CapabilityOf(object command) => For(command.GetType()).Capability(command);

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------

    private void Add<TCommand, TResult>(Action<P.Command, TCommand> fill, Func<P.CommandResult, TResult> read, Func<TCommand, string?>? capability = null)
        where TCommand : IServerCommand<TResult> =>
        _entries.Add(typeof(TCommand), new Entry((c, m) => fill(c, (TCommand)m), r => read(r)!, m => capability?.Invoke((TCommand)m)));

    private static T Expect<T>(P.CommandResult result, P.CommandResult.ResultOneofCase expected, Func<P.CommandResult, T> read) =>
        result.ResultCase == expected ? read(result) : throw new UnexpectedResultException($"Expected a {expected} result, got {result.ResultCase}.");

    private static Unit ReadUnit(P.CommandResult result) => Unit.Value; // Empty, or no payload at all

    private static DockerContainer ReadState(P.CommandResult result) =>
        Expect(result, P.CommandResult.ResultOneofCase.ContainerState, x => x.ContainerState.Container.ToDomain());

    private static PruneOutcome ReadPrune(P.CommandResult result) =>
        Expect(result, P.CommandResult.ResultOneofCase.Prune, x => x.Prune.ToDomain());

    private static ComposeOutcome ReadCompose(P.CommandResult result) =>
        Expect(result, P.CommandResult.ResultOneofCase.Compose, x => x.Compose.ToDomain());
}
