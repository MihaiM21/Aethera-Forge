using System.Globalization;
using System.Text;
using System.Text.Json;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Jobs;
using Aethera.Infrastructure.Ssh.Client;
using Aethera.Infrastructure.Ssh.Docker;

namespace Aethera.Infrastructure.Ssh;

/// <summary>
/// Runs one typed command over an established SSH connection by mapping it onto the allowlisted Docker CLI templates of
/// <see cref="DockerCommands"/> (ADR 0002 "SshTransport"). Anything outside the allowlist, or beyond what the CLI can do safely, is refused
/// with <c>transport.unsupported</c> and the hint to use the agent.
/// </summary>
public sealed class SshCommandRunner(
    ISshConnection connection, SshDockerPolicy policy, SshHealthProber prober, Action<string, bool> emit, TimeProvider time)
{
    private const int InspectBatch = 40;
    private const int MaxProjectFileBytes = 1024 * 1024;

    private static ServerTransportException Unsupported(string what) =>
        new(TransportErrors.Unsupported, $"{what} is not supported over SSH. {DockerCommands.Hint}");

    /// <summary>Executes <paramref name="command"/> and returns its typed result (as <see cref="object"/>; the transport casts).</summary>
    /// <exception cref="SshCommandFailure">The command ran and failed.</exception>
    /// <exception cref="ServerTransportException">The command is refused (validation, unsupported).</exception>
    public async Task<object> RunAsync(object command, CancellationToken ct) => command switch
    {
        ContainerCreateCommand c => await ContainerCreate(c, ct),
        ContainerStartCommand c => await Lifecycle(DockerCommands.ContainerStart(c.Container), c.Container, ct),
        ContainerStopCommand c => await Lifecycle(DockerCommands.ContainerStop(c.Container, c.Timeout), c.Container, ct),
        ContainerRestartCommand c => await Lifecycle(DockerCommands.ContainerRestart(c.Container, c.Timeout), c.Container, ct),
        ContainerRemoveCommand c => await Done(DockerCommands.ContainerRemove(c.Container, c.Force, c.RemoveAnonymousVolumes), ct),
        ContainerInspectCommand c => await InspectContainer(c.Container, c.IncludeRaw, ct),
        ContainerListCommand c => await ContainerList(c, ct),

        ImagePullCommand c => await ImagePull(c, ct),
        ImageListCommand c => await ImageList(c, ct),
        ImageRemoveCommand c => await Done(DockerCommands.ImageRemove(c.Image, c.Force), ct),
        ImagePruneCommand c => await ImagePrune(c.AllUnused, c.OlderThan, c.LabelFilters, c.Keep, ct),
        ImageInspectCommand c => await InspectImage(c.Image, ct),

        VolumeCreateCommand c => await VolumeCreate(c, ct),
        VolumeListCommand c => await VolumeList(c, ct),
        VolumeRemoveCommand c => await Done(DockerCommands.VolumeRemove(c.VolumeName, c.Force), ct),
        VolumePruneCommand c => await VolumePrune(c.LabelFilters, c.IncludeUnlabelled, ct),

        NetworkCreateCommand c => await NetworkCreate(c, ct),
        NetworkListCommand c => await NetworkList(c.LabelFilters, ct),
        NetworkRemoveCommand c => await Done(DockerCommands.NetworkRemove(c.Network), ct),
        NetworkConnectCommand c => await Done(DockerCommands.NetworkConnect(c.Network, c.Container, c.Aliases, c.Ipv4Address), ct),
        NetworkDisconnectCommand c => await Done(DockerCommands.NetworkDisconnect(c.Network, c.Container, c.Force), ct),
        NetworkPruneCommand c => DockerErrors.ParsePrune(await Ok(DockerCommands.NetworkPrune(c.OlderThan, c.LabelFilters), ct)),

        ComposeUpCommand c => await ComposeUp(c, ct),
        ComposeDownCommand c => await ComposeDown(c, ct),
        ComposePsCommand c => await ComposePs(c.Project, c.All, ct),
        ComposePullCommand c => await ComposePull(c, ct),

        BuildImageCommand c => await Build(c.Spec, ct),
        BuildDetectCommand => throw Unsupported("Build detection"),

        HealthProbeCommand c => await prober.ProbeAsync(connection, c, (remote, token) => Exec(remote, stream: false, token), ct),
        SystemPruneCommand c => await SystemPrune(c, ct),

        LogStreamStartCommand or LogStreamStopCommand => throw Unsupported("Log stream commands (logs are followed through the transport's stream)"),
        ProxyEnsureCommand => throw Unsupported("Managing the reverse proxy"),
        AgentSelfUpdateCommand => throw Unsupported("Updating the agent"),
        DiscoveryRefreshCommand => throw new InvalidOperationException("Discovery is handled by the transport."),
        _ => throw Unsupported($"The command '{(command as IServerCommand<object>)?.Name ?? command.GetType().Name}'"),
    };

    // ---- execution helpers ---------------------------------------------------------------------------------------------------------

    private async Task<RemoteResult> Exec(RemoteCommand command, bool stream, CancellationToken ct)
    {
        var redactor = new SecretRedactor();
        foreach (var secret in command.Secrets ?? []) redactor.Register(secret);
        var options = new RemoteExecOptions
        {
            OnLine = stream ? line => emit(redactor.Redact(line.Text), line.IsStderr) : null,
        };
        var result = await connection.ExecuteAsync(command, options, ct);
        return result with { Stdout = redactor.Redact(result.Stdout), Stderr = redactor.Redact(result.Stderr) };
    }

    private async Task<string> Ok(RemoteCommand command, CancellationToken ct, CommandErrorCode fallback = CommandErrorCode.Internal, bool stream = false)
    {
        var result = await Exec(command, stream, ct);
        if (!result.Succeeded)
            throw new SshCommandFailure(DockerErrors.Classify(result.Stderr, result.ExitStatus, fallback), DockerErrors.Summarize(result.Stderr, result.Stdout), result.ExitStatus);
        return result.Stdout;
    }

    private async Task<Unit> Done(RemoteCommand command, CancellationToken ct)
    {
        await Ok(command, ct);
        return Unit.Value;
    }

    private static IEnumerable<string> LinesOf(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<List<string>> Batches(IEnumerable<string> items) => items.Chunk(InspectBatch).Select(b => b.ToList());

    // ---- containers ----------------------------------------------------------------------------------------------------------------

    private async Task<DockerContainer> InspectContainer(string container, bool raw, CancellationToken ct)
    {
        var json = await Ok(DockerCommands.ContainerInspect([container]), ct);
        var first = DockerJson.Array(json).FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) throw new SshCommandFailure(CommandErrorCode.NotFound, "No such container.");
        return DockerJson.Container(first, raw);
    }

    private async Task<DockerContainer> Lifecycle(RemoteCommand command, string container, CancellationToken ct)
    {
        await Ok(command, ct);
        return await InspectContainer(container, raw: false, ct);
    }

    private async Task<ContainerCreated> ContainerCreate(ContainerCreateCommand c, CancellationToken ct)
    {
        var plan = DockerCommands.ContainerCreate(c.Spec, c.Start, c.PullIfMissing, c.PullAuth, policy);
        if (c.ReplaceExisting)
        {
            var removal = await Exec(DockerCommands.ContainerRemove(c.Spec.Name, force: true, removeVolumes: false), stream: false, ct);
            if (!removal.Succeeded && DockerErrors.Classify(removal.Stderr, removal.ExitStatus, CommandErrorCode.Internal) != CommandErrorCode.NotFound)
                throw new SshCommandFailure(DockerErrors.Classify(removal.Stderr, removal.ExitStatus, CommandErrorCode.Internal), DockerErrors.Summarize(removal.Stderr, removal.Stdout), removal.ExitStatus);
        }

        await Ok(plan.Create, ct, CommandErrorCode.Internal, stream: true);
        try
        {
            foreach (var connect in plan.ConnectNetworks) await Ok(connect, ct);
            if (plan.Start is { } start) await Ok(start, ct);
        }
        catch (SshCommandFailure)
        {
            // Do not leave a half-configured container behind (a retry would hit "name already in use").
            await Exec(DockerCommands.ContainerRemove(c.Spec.Name, force: true, removeVolumes: false), stream: false, CancellationToken.None);
            throw;
        }

        var created = await InspectContainer(c.Spec.Name, raw: false, ct);
        return new ContainerCreated(created.Id, created.Name, created.State, plan.Warnings);
    }

    private async Task<IReadOnlyList<DockerContainer>> ContainerList(ContainerListCommand c, CancellationToken ct)
    {
        var ids = LinesOf(await Ok(DockerCommands.ContainerIds(c.All, c.LabelFilters, c.NameFilter), ct)).Distinct().ToList();
        var result = new List<DockerContainer>();
        foreach (var batch in Batches(ids))
            result.AddRange(DockerJson.Array(await Ok(DockerCommands.ContainerInspect(batch), ct)).Select(e => DockerJson.Container(e, includeRaw: false)));
        return result;
    }

    // ---- images --------------------------------------------------------------------------------------------------------------------

    private async Task<ImagePulled> ImagePull(ImagePullCommand c, CancellationToken ct)
    {
        var output = await Ok(DockerCommands.ImagePull(c.Reference, c.Auth, c.Platform), ct, CommandErrorCode.ImagePullFailed, stream: true);
        var image = await InspectImage(c.Reference, ct);
        var digest = image.RepoDigests.Select(d => d[(d.IndexOf('@') + 1)..]).FirstOrDefault() ?? "";
        return new ImagePulled(image.Id, digest, image.SizeBytes, image.RepoDigests, output.Contains("Image is up to date", StringComparison.Ordinal));
    }

    private async Task<DockerImage> InspectImage(string reference, CancellationToken ct)
    {
        var first = DockerJson.Array(await Ok(DockerCommands.ImageInspect([reference]), ct)).FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) throw new SshCommandFailure(CommandErrorCode.NotFound, "No such image.");
        return DockerJson.Image(first);
    }

    private async Task<IReadOnlyList<DockerImage>> ImageList(ImageListCommand c, CancellationToken ct)
    {
        var ids = LinesOf(await Ok(DockerCommands.ImageIds(c.IncludeIntermediate, c.LabelFilters, c.ReferenceFilter), ct)).Distinct().ToList();
        var result = new List<DockerImage>();
        foreach (var batch in Batches(ids))
            result.AddRange(DockerJson.Array(await Ok(DockerCommands.ImageInspect(batch), ct)).Select(e => DockerJson.Image(e)));
        return result;
    }

    private async Task<PruneOutcome> ImagePrune(bool allUnused, TimeSpan? olderThan, IReadOnlyList<string>? labels, IReadOnlyList<string>? keep, CancellationToken ct)
    {
        if (!allUnused || keep is not { Count: > 0 })
            return DockerErrors.ParsePrune(await Ok(DockerCommands.ImagePrune(allUnused, olderThan, labels), ct));

        // `docker image prune --all` cannot exclude images. With a keep list the unused images are picked here and removed one by one;
        // an image still referenced is simply refused by Docker (no --force).
        var keepSet = new HashSet<string>(keep.Select(k => SshValidators.ImageRef(k, "A kept image")), StringComparer.Ordinal);
        var table = LinesOf(await Ok(DockerCommands.ImageTable(), ct)).Select(l => l.Split('\t')).Where(p => p.Length == 2).ToList();
        var inUse = new HashSet<string>(LinesOf(await Ok(DockerCommands.ContainerImageIds(), ct)), StringComparer.Ordinal);
        var kept = table.Where(row => keepSet.Contains(row[1]) || keepSet.Contains(row[0])).Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        var candidates = table.Select(row => row[0]).Distinct().Where(id => !inUse.Contains(id) && !kept.Contains(id)).ToList();

        var deleted = new List<string>();
        long reclaimed = 0;
        var cutoff = olderThan is { } age && age > TimeSpan.Zero ? time.GetUtcNow() - age : (DateTimeOffset?)null;
        foreach (var batch in Batches(candidates))
        {
            foreach (var element in DockerJson.Array(await Ok(DockerCommands.ImageInspect(batch), ct)))
            {
                var image = DockerJson.Image(element);
                if (cutoff is { } limit && image.CreatedAt is { } created && created > limit) continue;
                if (labels is { Count: > 0 } && !labels.All(filter => MatchesLabel(image.Labels, filter))) continue;
                var removal = await Exec(DockerCommands.ImageRemove(image.Id, force: false), stream: false, ct);
                if (!removal.Succeeded) continue;
                deleted.AddRange(removal.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                reclaimed += image.SizeBytes;
            }
        }

        return new PruneOutcome(deleted, reclaimed);
    }

    private static bool MatchesLabel(IReadOnlyDictionary<string, string> labels, string filter)
    {
        var parts = filter.Split('=', 2);
        return labels.TryGetValue(parts[0], out var value) && (parts.Length == 1 || value == parts[1]);
    }

    // ---- volumes, networks ---------------------------------------------------------------------------------------------------------

    private async Task<DockerVolume> VolumeCreate(VolumeCreateCommand c, CancellationToken ct)
    {
        await Ok(DockerCommands.VolumeCreate(c.VolumeName, c.Driver, c.DriverOptions, c.Labels), ct);
        var first = DockerJson.Array(await Ok(DockerCommands.VolumeInspect([c.VolumeName]), ct)).FirstOrDefault();
        return DockerJson.Volume(first);
    }

    private async Task<IReadOnlyList<DockerVolume>> VolumeList(VolumeListCommand c, CancellationToken ct) =>
        await InspectVolumes(LinesOf(await Ok(DockerCommands.VolumeNames(c.LabelFilters, danglingOnly: false), ct)).ToList(), ct);

    private async Task<List<DockerVolume>> InspectVolumes(List<string> names, CancellationToken ct)
    {
        var result = new List<DockerVolume>();
        foreach (var batch in Batches(names))
            result.AddRange(DockerJson.Array(await Ok(DockerCommands.VolumeInspect(batch), ct)).Select(DockerJson.Volume));
        return result;
    }

    /// <summary>Removes unused volumes that carry a label (matching the filters); unlabelled ones only with <paramref name="includeUnlabelled"/> (data-loss guard, as the agent).</summary>
    private async Task<PruneOutcome> VolumePrune(IReadOnlyList<string>? labels, bool includeUnlabelled, CancellationToken ct)
    {
        var unused = await InspectVolumes(LinesOf(await Ok(DockerCommands.VolumeNames(null, danglingOnly: true), ct)).ToList(), ct);
        var deleted = new List<string>();
        foreach (var volume in unused)
        {
            var eligible = labels is { Count: > 0 }
                ? labels.All(filter => MatchesLabel(volume.Labels, filter))
                : volume.Labels.Count > 0 || includeUnlabelled;
            if (!eligible) continue;
            var removal = await Exec(DockerCommands.VolumeRemove(volume.Name, force: false), stream: false, ct);
            if (removal.Succeeded) deleted.Add(volume.Name);
        }

        return new PruneOutcome(deleted, 0);
    }

    private async Task<DockerNetwork> NetworkCreate(NetworkCreateCommand c, CancellationToken ct)
    {
        if (c.IfNotExists)
        {
            var existing = await Exec(DockerCommands.NetworkInspect([c.NetworkName]), stream: false, ct);
            if (existing.Succeeded && DockerJson.Array(existing.Stdout).FirstOrDefault() is { ValueKind: JsonValueKind.Object } found) return DockerJson.Network(found);
        }

        await Ok(DockerCommands.NetworkCreate(c), ct);
        return DockerJson.Network(DockerJson.Array(await Ok(DockerCommands.NetworkInspect([c.NetworkName]), ct)).First());
    }

    private async Task<IReadOnlyList<DockerNetwork>> NetworkList(IReadOnlyList<string>? labels, CancellationToken ct)
    {
        var ids = LinesOf(await Ok(DockerCommands.NetworkIds(labels), ct)).Distinct().ToList();
        var result = new List<DockerNetwork>();
        foreach (var batch in Batches(ids))
            result.AddRange(DockerJson.Array(await Ok(DockerCommands.NetworkInspect(batch), ct)).Select(DockerJson.Network));
        return result;
    }

    private async Task<PruneOutcome> SystemPrune(SystemPruneCommand c, CancellationToken ct)
    {
        if (!c.AnyScope) throw new ServerTransportException(TransportErrors.CommandRejected, "At least one prune scope must be selected.");
        var deleted = new List<string>();
        long reclaimed = 0;

        void Add(PruneOutcome outcome)
        {
            deleted.AddRange(outcome.Deleted);
            reclaimed += outcome.SpaceReclaimedBytes;
        }

        if (c.StoppedContainers) Add(DockerErrors.ParsePrune(await Ok(DockerCommands.ContainerPrune(c.OlderThan), ct)));
        if (c.UnusedImages) Add(await ImagePrune(allUnused: true, c.OlderThan, null, c.KeepImages, ct));
        else if (c.DanglingImages) Add(await ImagePrune(allUnused: false, c.OlderThan, null, null, ct));
        if (c.UnusedNetworks) Add(DockerErrors.ParsePrune(await Ok(DockerCommands.NetworkPrune(c.OlderThan, null), ct)));
        if (c.BuildCache) Add(DockerErrors.ParsePrune(await Ok(DockerCommands.BuilderPrune(c.OlderThan), ct)));
        if (c.Volumes) Add(await VolumePrune(null, includeUnlabelled: false, ct));
        return new PruneOutcome(deleted, reclaimed);
    }

    // ---- compose -------------------------------------------------------------------------------------------------------------------

    private async Task<ComposeOutcome> ComposeUp(ComposeUpCommand c, CancellationToken ct)
    {
        await WriteProjectAsync(c.Project, ct);
        await Ok(DockerCommands.ComposeUp(policy, c), ct, CommandErrorCode.Internal, stream: true);
        return await ComposePs(c.Project, all: true, ct);
    }

    private async Task<ComposeOutcome> ComposeDown(ComposeDownCommand c, CancellationToken ct)
    {
        var directory = DockerCommands.ProjectDirectory(policy, c.Project.ProjectName);
        var exists = await Exec(new Cmd("test").Lit("-d").Arg(directory).ToRemote(), stream: false, ct);
        if (!exists.Succeeded) return new ComposeOutcome(c.Project.ProjectName, []); // never deployed over SSH: nothing to take down
        await Ok(DockerCommands.ComposeDown(policy, c), ct, CommandErrorCode.Internal, stream: true);
        return new ComposeOutcome(c.Project.ProjectName, []);
    }

    private async Task<ComposeOutcome> ComposePs(ComposeProjectSpec project, bool all, CancellationToken ct)
    {
        var output = await Ok(DockerCommands.ComposePs(policy, new ComposePsCommand(project, all)), ct);
        return new ComposeOutcome(project.ProjectName, DockerJson.Lines(output).Select(DockerJson.ComposeService).ToList());
    }

    private async Task<Unit> ComposePull(ComposePullCommand c, CancellationToken ct)
    {
        await WriteProjectAsync(c.Project, ct);
        await Ok(DockerCommands.ComposePull(policy, c), ct, CommandErrorCode.ImagePullFailed, stream: true);
        return Unit.Value;
    }

    /// <summary>Uploads the Compose file, the override and the <c>.env</c> to the project directory (mode 0600; content is data, never a command).</summary>
    private async Task WriteProjectAsync(ComposeProjectSpec project, CancellationToken ct)
    {
        var directory = DockerCommands.ProjectDirectory(policy, project.ProjectName);
        await Ok(DockerCommands.MakeProjectDirectory(directory), ct);
        await UploadTextAsync(directory + "/compose.yaml", project.ComposeFile, ct);
        if (project.OverrideFile is { Length: > 0 } overrideFile) await UploadTextAsync(directory + "/compose.override.yaml", overrideFile, ct);
        await UploadTextAsync(directory + "/.env", DotEnv(project.Env), ct);
    }

    private async Task UploadTextAsync(string path, string content, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaxProjectFileBytes) throw new ServerTransportException(TransportErrors.CommandRejected, "A Compose file is larger than 1 MiB.");
        await using var stream = new MemoryStream(bytes);
        await connection.UploadAsync(path, stream, "0600", ct);
    }

    /// <summary>The project's environment as a Compose <c>.env</c> file, values quoted so nothing is interpolated or taken as a comment.</summary>
    public static string DotEnv(IReadOnlyList<EnvVarSpec>? env)
    {
        var builder = new StringBuilder();
        foreach (var entry in env ?? [])
        {
            var name = SshValidators.EnvName(entry.Name);
            var value = SshValidators.EnvValue(name, entry.Secret?.Value ?? entry.Plain);
            builder.Append(name).Append('=');
            if (!value.Contains('\'')) builder.Append('\'').Append(value).Append('\'');
            else builder.Append('"').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal)).Append('"');
            builder.Append('\n');
        }

        return builder.ToString();
    }

    // ---- builds --------------------------------------------------------------------------------------------------------------------

    private async Task<BuildOutcome> Build(BuildSpec spec, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        string engine;
        switch (spec.Engine)
        {
            case BuildEngineKind.Image:
            {
                var reference = spec.ImageReference ?? throw new ServerTransportException(TransportErrors.CommandRejected, "An image build needs an image reference.");
                if (spec.Push || spec.PushAuth is not null) throw Unsupported("Pushing an image");
                await Ok(DockerCommands.ImagePull(reference, spec.ImagePullAuth, platform: spec.TargetPlatforms is { Count: 1 } p ? p[0] : null), ct, CommandErrorCode.ImagePullFailed, stream: true);
                foreach (var tag in spec.ImageTags)
                    await Ok(new Cmd("docker").Lit("image").Lit("tag").Lit("--").Arg(SshValidators.NotOptionImage(reference)).Arg(SshValidators.ImageTag(tag)).ToRemote(), ct);
                engine = "image";
                break;
            }

            case BuildEngineKind.Dockerfile:
                await Ok(DockerCommands.BuildFromGit(spec), ct, CommandErrorCode.BuildFailed, stream: true);
                engine = "dockerfile";
                break;

            default:
                throw Unsupported($"Building with the {spec.Engine} engine");
        }

        var reference0 = spec.ImageTags.Count > 0 ? spec.ImageTags[0] : spec.ImageReference ?? throw new ServerTransportException(TransportErrors.CommandRejected, "The build has no image tag.");
        var image = await InspectImage(reference0, ct);
        return new BuildOutcome(
            spec.BuildId, image.Id, image.RepoDigests.Select(d => d[(d.IndexOf('@') + 1)..]).FirstOrDefault() ?? "", spec.ImageTags, image.RepoDigests, image.SizeBytes,
            time.GetElapsedTime(started), engine, "", false, $"{image.Os}/{image.Architecture}", false);
    }

    public static string FormatCpu(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
