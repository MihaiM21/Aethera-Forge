using System.Text.RegularExpressions;
using Aethera.Domain;
using FluentValidation;
using FluentValidation.Results;

namespace Aethera.Api.Features.Resources.Workloads;

// ---- requests -------------------------------------------------------------------------------------------------------------------------

public sealed record GitSourceRequest
{
    public string? Provider { get; init; }
    public string? RepositoryUrl { get; init; }
    public string? Branch { get; init; }
    public string? CommitPin { get; init; }
    public Guid? GitCredentialId { get; init; }
    public bool? AutoDeploy { get; init; }
}

public sealed record ImageSourceRequest
{
    public Guid? RegistryId { get; init; }
    public string? Image { get; init; }
    public string? Tag { get; init; }
    public string? PullPolicy { get; init; }
}

public sealed record ComposeSourceRequest
{
    public string? FilePath { get; init; }
    public string? InlineContent { get; init; }
}

public sealed record BuildConfigRequest
{
    public string? Engine { get; init; }
    public string? Context { get; init; }
    public string? DockerfilePath { get; init; }
    public string? DockerfileInline { get; init; }
    public string? InstallCommand { get; init; }
    public string? BuildCommand { get; init; }
    public string? StartCommand { get; init; }
    public string? OutputDirectory { get; init; }

    /// <summary>Plain build arguments. Secrets belong in build-time environment variables, not here.</summary>
    public Dictionary<string, string>? BuildArgs { get; init; }

    public bool? CacheEnabled { get; init; }
    public string? TargetPlatform { get; init; }
}

public sealed record CreateApplicationRequest
{
    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }
    public Guid? EnvironmentId { get; init; }
    public Guid? ServerId { get; init; }

    /// <summary><c>git</c>, <c>dockerImage</c>, <c>dockerfile</c>, <c>compose</c>, <c>static</c> or <c>nixpacks</c>.</summary>
    public string? SourceKind { get; init; }

    public GitSourceRequest? GitSource { get; init; }
    public ImageSourceRequest? Image { get; init; }
    public ComposeSourceRequest? Compose { get; init; }
    public BuildConfigRequest? Build { get; init; }
    public RuntimeRequest? Runtime { get; init; }
}

/// <summary>JSON merge patch of an application: nested objects are merged property by property, arrays (ports) are replaced.</summary>
public sealed record UpdateApplicationRequest
{
    [NotClearable] public string? Name { get; init; }
    public string? Description { get; init; }
    [NotClearable] public Guid? ServerId { get; init; }
    [NotClearable] public GitSourceRequest? GitSource { get; init; }
    [NotClearable] public ImageSourceRequest? Image { get; init; }
    [NotClearable] public ComposeSourceRequest? Compose { get; init; }
    public BuildConfigRequest? Build { get; init; }
    [NotClearable] public RuntimeRequest? Runtime { get; init; }
}

// ---- responses ------------------------------------------------------------------------------------------------------------------------

public sealed record GitSourceResponse(
    GitProvider Provider, string RepositoryUrl, string Branch, string? CommitPin, Guid? GitCredentialId, bool AutoDeploy);

public sealed record ImageSourceResponse(Guid? RegistryId, string Image, string Tag, ImagePullPolicy PullPolicy);

public sealed record ComposeSourceResponse(string? FilePath, string? InlineContent);

public sealed record BuildConfigResponse(
    string Engine, string Context, string? DockerfilePath, string? DockerfileInline, string? InstallCommand, string? BuildCommand,
    string? StartCommand, string? OutputDirectory, System.Text.Json.Nodes.JsonObject BuildArgs, bool CacheEnabled, string? TargetPlatform);

/// <summary>The shape of the workload fields common to applications and services.</summary>
public sealed record WorkloadStatusResponse(
    DesiredState DesiredState, WorkloadStatus Status, string? StatusReason, DateTimeOffset? StatusChangedAt, DateTimeOffset? StatusObservedAt,
    Guid? CurrentDeploymentId);

public sealed record ApplicationResponse(
    Guid Id, string Name, string Slug, string? Description, Guid ProjectId, Guid EnvironmentId, Guid ServerId, ApplicationSourceKind SourceKind,
    WorkloadStatusResponse State, GitSourceResponse? GitSource, ImageSourceResponse? Image, ComposeSourceResponse? Compose,
    BuildConfigResponse? Build, RuntimeResponse Runtime, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Lighter list shape (no build/runtime blobs).</summary>
public sealed record ApplicationSummary(
    Guid Id, string Name, string Slug, string? Description, Guid ProjectId, Guid EnvironmentId, Guid ServerId, ApplicationSourceKind SourceKind,
    DesiredState DesiredState, WorkloadStatus Status, string? StatusReason, Guid? CurrentDeploymentId, string? RepositoryUrl, string? Image,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// ---- validation -----------------------------------------------------------------------------------------------------------------------

public static partial class ApplicationRules
{
    [GeneratedRegex(@"^(https?://|ssh://|git://)[^\s]+$|^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s]+$")]
    private static partial Regex RepositoryUrlPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/:@]*$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,127}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,64}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[a-z0-9]+/[a-z0-9]+(/[a-z0-9]+)?$")]
    private static partial Regex PlatformPattern();

    public static readonly string[] BuildEngineNames = [BuildEngines.Dockerfile, BuildEngines.Nixpacks, BuildEngines.Static];

    public static readonly ApplicationSourceKind[] GitBackedKinds =
        [ApplicationSourceKind.Git, ApplicationSourceKind.Dockerfile, ApplicationSourceKind.Static, ApplicationSourceKind.Nixpacks];

    public static bool UsesBuild(ApplicationSourceKind kind) => GitBackedKinds.Contains(kind);

    public static bool UsesGit(ApplicationSourceKind kind) => GitBackedKinds.Contains(kind) || kind == ApplicationSourceKind.Compose;

    /// <summary>The build engine a kind implies; <c>git</c> may pick any engine.</summary>
    public static string? EngineOf(ApplicationSourceKind kind) => kind switch
    {
        ApplicationSourceKind.Dockerfile => BuildEngines.Dockerfile,
        ApplicationSourceKind.Nixpacks => BuildEngines.Nixpacks,
        ApplicationSourceKind.Static => BuildEngines.Static,
        _ => null,
    };

    public static bool IsRepositoryUrl(string value) => RepositoryUrlPattern().IsMatch(value);
    public static bool IsImage(string value) => ImagePattern().IsMatch(value);
    public static bool IsTag(string value) => TagPattern().IsMatch(value);
    public static bool IsCommit(string value) => CommitPattern().IsMatch(value);
    public static bool IsPlatform(string value) => PlatformPattern().IsMatch(value);
}

public abstract class GitSourceValidator : AbstractValidator<GitSourceRequest>
{
    // Not constructible by the container (assembly scanning registers validators): use the two concrete classes.
    protected GitSourceValidator(bool create)
    {
        var url = RuleFor(x => x.RepositoryUrl);
        if (create) url.NotEmpty();
        url.MaximumLength(1000).Must(u => u is null || ApplicationRules.IsRepositoryUrl(u)).WithErrorCode("pattern")
            .WithMessage("Must be an http(s), ssh or git URL, or an scp-style address such as git@host:org/repo.git.");
        RuleFor(x => x.Provider).MustBeEnum<GitSourceRequest, GitProvider>();
        RuleFor(x => x.Branch).MaximumLength(255).Must(b => b is null || (b.Length > 0 && !b.Any(char.IsWhiteSpace))).WithErrorCode("pattern")
            .WithMessage("Must not contain whitespace.");
        RuleFor(x => x.CommitPin).Must(c => c is null || ApplicationRules.IsCommit(c)).WithErrorCode("pattern")
            .WithMessage("Must be a hexadecimal commit id (7-64 characters).");
    }
}

public abstract class ImageSourceValidator : AbstractValidator<ImageSourceRequest>
{
    protected ImageSourceValidator(bool create)
    {
        var image = RuleFor(x => x.Image);
        if (create) image.NotEmpty();
        image.MaximumLength(500).Must(v => v is null || ApplicationRules.IsImage(v)).WithErrorCode("pattern")
            .WithMessage("Must be an image reference such as nginx, ghcr.io/org/app or org/app@sha256:...");
        RuleFor(x => x.Tag).Must(t => t is null || ApplicationRules.IsTag(t)).WithErrorCode("pattern").WithMessage("Must be a valid image tag.");
        RuleFor(x => x.PullPolicy).MustBeEnum<ImageSourceRequest, ImagePullPolicy>();
    }
}

public sealed class GitSourceCreateValidator() : GitSourceValidator(create: true);

public sealed class GitSourceUpdateValidator() : GitSourceValidator(create: false);

public sealed class ImageSourceCreateValidator() : ImageSourceValidator(create: true);

public sealed class ImageSourceUpdateValidator() : ImageSourceValidator(create: false);

public sealed class ComposeSourceValidator : AbstractValidator<ComposeSourceRequest>
{
    public ComposeSourceValidator()
    {
        RuleFor(x => x.FilePath).MaximumLength(500);
        RuleFor(x => x.InlineContent).MaximumLength(512 * 1024);
    }
}

public sealed class BuildConfigValidator : AbstractValidator<BuildConfigRequest>
{
    public BuildConfigValidator()
    {
        RuleFor(x => x.Engine).Must(e => e is null || ApplicationRules.BuildEngineNames.Contains(e)).WithErrorCode("invalid_enum")
            .WithMessage($"Must be one of: {string.Join(", ", ApplicationRules.BuildEngineNames)}.");
        RuleFor(x => x.Context).MaximumLength(500);
        RuleFor(x => x.DockerfilePath).MaximumLength(500);
        RuleFor(x => x.DockerfileInline).MaximumLength(64 * 1024);
        RuleFor(x => x.InstallCommand).MaximumLength(2000);
        RuleFor(x => x.BuildCommand).MaximumLength(2000);
        RuleFor(x => x.StartCommand).MaximumLength(2000);
        RuleFor(x => x.OutputDirectory).MaximumLength(500);
        RuleFor(x => x.TargetPlatform).Must(p => p is null || ApplicationRules.IsPlatform(p)).WithErrorCode("pattern")
            .WithMessage("Must look like linux/amd64 or linux/arm64.");
        RuleFor(x => x.BuildArgs).Must(a => a is null || a.Count <= 100).WithErrorCode("too_long").WithMessage("At most 100 build arguments.")
            .Must(a => a is null || a.Keys.All(EnvironmentVariable.IsValidKey)).WithErrorCode("pattern")
            .WithMessage("Argument names must look like environment variable names.");
    }
}

public sealed class CreateApplicationValidator : AbstractValidator<CreateApplicationRequest>
{
    public CreateApplicationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).Must(Slug.IsValid).WithErrorCode("pattern")
            .WithMessage("Use lowercase letters, digits and single hyphens (1-63 characters).").When(x => x.Slug is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.EnvironmentId).NotNull();
        RuleFor(x => x.ServerId).NotNull();
        RuleFor(x => x.SourceKind).NotEmpty().MustBeEnum<CreateApplicationRequest, ApplicationSourceKind>();

        RuleFor(x => x.GitSource).SetValidator(new GitSourceCreateValidator()!);
        RuleFor(x => x.Image).SetValidator(new ImageSourceCreateValidator()!);
        RuleFor(x => x.Compose).SetValidator(new ComposeSourceValidator()!);
        RuleFor(x => x.Build).SetValidator(new BuildConfigValidator()!);
        RuleFor(x => x.Runtime).SetValidator(new RuntimeRequestValidator()!);

        // Per source kind: which configuration is required, and which does not apply.
        When(x => EnumText.Parse<ApplicationSourceKind>(x.SourceKind) is not null, () =>
        {
            RuleFor(x => x).Custom((x, context) =>
            {
                var kind = EnumText.Parse<ApplicationSourceKind>(x.SourceKind)!.Value;
                void Fail(string property, string message, string code) =>
                    context.AddFailure(new ValidationFailure(property, message) { ErrorCode = code });
                void Missing(string property, string message) => Fail(property, message, "required");
                void NotApplicable(string property) => Fail(property, $"Does not apply to source kind '{x.SourceKind}'.", "not_applicable");

                if (ApplicationRules.UsesBuild(kind) && x.GitSource is null) Missing("GitSource", "A git source is required for this source kind.");
                if (kind == ApplicationSourceKind.DockerImage && x.Image is null) Missing("Image", "An image is required for source kind 'dockerImage'.");
                if (kind == ApplicationSourceKind.Compose)
                {
                    if (x.Compose is null) Missing("Compose", "A compose source is required for source kind 'compose'.");
                    else if (!string.IsNullOrWhiteSpace(x.Compose.FilePath) && x.GitSource is null)
                        Missing("GitSource", "A compose file path needs the git source it lives in.");
                    if (x.Compose is not null && !(!string.IsNullOrWhiteSpace(x.Compose.FilePath) ^ !string.IsNullOrWhiteSpace(x.Compose.InlineContent)))
                        Missing("Compose.FilePath", "Provide exactly one of filePath (a file in the repository) or inlineContent.");
                }

                if (!ApplicationRules.UsesGit(kind) && x.GitSource is not null) NotApplicable("GitSource");
                if (kind != ApplicationSourceKind.DockerImage && x.Image is not null) NotApplicable("Image");
                if (kind != ApplicationSourceKind.Compose && x.Compose is not null) NotApplicable("Compose");
                if (!ApplicationRules.UsesBuild(kind) && x.Build is not null) NotApplicable("Build");

                if (x.Build?.Engine is { } engine && ApplicationRules.EngineOf(kind) is { } implied && engine != implied)
                    Fail("Build.Engine", $"Source kind '{x.SourceKind}' builds with '{implied}'.", ResourceProblemCodes.Mismatch);
            });
        });
    }
}

public sealed class UpdateApplicationValidator : AbstractValidator<UpdateApplicationRequest>
{
    public UpdateApplicationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.GitSource).SetValidator(new GitSourceUpdateValidator()!);
        RuleFor(x => x.Image).SetValidator(new ImageSourceUpdateValidator()!);
        RuleFor(x => x.Compose).SetValidator(new ComposeSourceValidator()!);
        RuleFor(x => x.Build).SetValidator(new BuildConfigValidator()!);
        RuleFor(x => x.Runtime).SetValidator(new RuntimeRequestValidator()!);
    }
}
