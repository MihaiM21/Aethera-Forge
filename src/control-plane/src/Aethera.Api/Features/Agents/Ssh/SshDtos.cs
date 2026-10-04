using System.Text.RegularExpressions;
using Aethera.Api.Features.Jobs;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Servers;
using Aethera.Api.Http;
using Aethera.Infrastructure.Ssh;
using FluentValidation;

namespace Aethera.Api.Features.Agents.Ssh;

/// <summary>Problem codes of the SSH endpoints (the connection-level ones are shared with the transport: <see cref="SshErrors"/>).</summary>
public static class SshProblemCodes
{
    public const string HostKeyMismatch = "ssh.host_key_mismatch";
    public const string NothingToConfirm = "ssh.host_key_nothing_to_confirm";
    public const string NotConfigured = "ssh.no_credential";
}

// ---- add a server by SSH ---------------------------------------------------------------------------------------------------------------

/// <summary>
/// Adds a server that Aethera reaches over SSH. Exactly one of <c>privateKey</c> and <c>password</c> is required. The credential is stored
/// as an encrypted, organization-scoped secret that belongs to the server (managed: it is never returned by any endpoint).
/// </summary>
public sealed record AddSshServerRequest
{
    public string? Name { get; init; }
    public string? Host { get; init; }
    public int? SshPort { get; init; }

    /// <summary>SSH login; default <c>root</c>. A non-root user needs passwordless <c>sudo</c> for the agent install and Docker access for the fallback.</summary>
    public string? SshUser { get; init; }

    /// <summary>PEM or OpenSSH private key. Never logged, never returned.</summary>
    public string? PrivateKey { get; init; }

    /// <summary>Passphrase of <c>privateKey</c>, if it has one.</summary>
    public string? Passphrase { get; init; }

    /// <summary>Password login. Prefer a key.</summary>
    public string? Password { get; init; }

    public List<string>? Roles { get; init; }
    public string? PublicIp { get; init; }
    public int? MaxConcurrentBuilds { get; init; }

    /// <summary>Install the agent right after adding the server (a job). Default <c>true</c>.</summary>
    public bool? InstallAgent { get; init; }

    /// <summary>The host key fingerprint (<c>SHA256:...</c>) the user verified, for example from <c>POST /servers/ssh/host-key</c>. Pinned before the first connection; a different key is refused.</summary>
    public string? HostKeyFingerprint { get; init; }
}

/// <param name="InstallJob">The install job, when <c>installAgent</c> was requested.</param>
public sealed record AddSshServerResponse(ServerResponse Server, JobDto? InstallJob);

public sealed record InstallAgentRequest
{
    /// <summary>A host key fingerprint the user verified; pinned before the first command is sent.</summary>
    public string? HostKeyFingerprint { get; init; }
}

public sealed record ScanHostKeyRequest
{
    public string? Host { get; init; }
    public int? Port { get; init; }
}

public sealed record HostKeyResponse(string Algorithm, string Fingerprint);

// ---- state -----------------------------------------------------------------------------------------------------------------------------

public sealed record PendingHostKeyResponse(string Algorithm, string Fingerprint, DateTimeOffset SeenAt);

/// <param name="State"><c>unpinned</c> (no connection yet), <c>pinned</c> or <c>changed</c> (a different key was presented and awaits confirmation).</param>
public sealed record SshHostKeyStateResponse(string State, string? PinnedFingerprint, PendingHostKeyResponse? Pending);

public sealed record SshPollingResponse(DateTimeOffset? Since, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, string? LastError);

/// <param name="Mode"><c>agent</c> (a session is connected), <c>sshPolling</c> (degraded: polling over SSH), <c>sshIdle</c> (SSH usable but fallback is off or nothing polled yet) or <c>none</c>.</param>
/// <param name="Degraded">True when the server is served over SSH instead of the agent: no pushed events or metrics, builds limited.</param>
public sealed record SshStateResponse(
    Guid ServerId, bool HasCredential, string Host, int Port, string? User, SshHostKeyStateResponse HostKey, bool AllowSshFallback, string Mode, bool Degraded,
    string? DegradedReason, SshPollingResponse? Polling, DateTimeOffset ObservedAt);

public sealed record UpdateSshRequest
{
    public bool? AllowSshFallback { get; init; }
}

public sealed record ConfirmHostKeyRequest
{
    /// <summary>The fingerprint to trust. It must be the one the server presented last (shown in the SSH state).</summary>
    public string? Fingerprint { get; init; }
}

public sealed record SshTestResponse(bool Connected, HostKeyResponse HostKey, string? Os, string? Architecture, string? DockerVersion, double LatencyMilliseconds);

// ---- validation ------------------------------------------------------------------------------------------------------------------------

public static partial class SshRules
{
    [GeneratedRegex(@"\ASHA256:[A-Za-z0-9+/]{43}\z")]
    private static partial Regex FingerprintPattern();

    public static bool IsFingerprint(string? value) => value is not null && FingerprintPattern().IsMatch(value);
}

public sealed class AddSshServerValidator : AbstractValidator<AddSshServerRequest>
{
    public AddSshServerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Host).NotEmpty();
        RuleFor(x => x.Host).Must(ServerRules.IsValidHost).WithErrorCode("pattern").WithMessage("Must be a DNS name or an IP address, without scheme or port.")
            .When(x => !string.IsNullOrEmpty(x.Host));
        RuleFor(x => x.SshPort).InclusiveBetween(1, 65535).When(x => x.SshPort is not null);
        RuleFor(x => x.SshUser).Must(ServerRules.IsValidSshUser).WithErrorCode("pattern").WithMessage("Must be a valid Unix user name.").When(x => x.SshUser is not null);
        RuleFor(x => x.PrivateKey).Must((x, _) => string.IsNullOrEmpty(x.PrivateKey) != string.IsNullOrEmpty(x.Password)).WithErrorCode("required")
            .WithMessage("Provide either a privateKey or a password, not both and not neither.");
        RuleFor(x => x.PrivateKey).Must(k => k!.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal)).WithErrorCode("pattern")
            .WithMessage("Must be a PEM or OpenSSH private key.").MaximumLength(32 * 1024).When(x => !string.IsNullOrEmpty(x.PrivateKey));
        RuleFor(x => x.Passphrase).Empty().When(x => string.IsNullOrEmpty(x.PrivateKey)).WithMessage("A passphrase only goes with a privateKey.");
        RuleFor(x => x.Passphrase).MaximumLength(1024);
        RuleFor(x => x.Password).MaximumLength(1024);
        RuleFor(x => x.HostKeyFingerprint).Must(SshRules.IsFingerprint).WithErrorCode("pattern").WithMessage("Must look like SHA256:<43 base64 characters>.")
            .When(x => x.HostKeyFingerprint is not null);
        RuleForEach(x => x.Roles).Must(r => EnumText.IsDefined<Aethera.Domain.ServerRole>(r)).WithErrorCode("invalid_enum")
            .WithMessage($"Must be one of: {EnumText.Names<Aethera.Domain.ServerRole>()}.");
        RuleFor(x => x.Roles).Must(r => r is null || r.Select(v => EnumText.Parse<Aethera.Domain.ServerRole>(v)).Distinct().Count() == r.Count)
            .WithErrorCode("not_unique").WithMessage("Each role may appear once.");
        RuleFor(x => x.PublicIp).Must(ServerRules.IsValidIp).WithErrorCode("pattern").WithMessage("Must be an IPv4 or IPv6 address.").When(x => x.PublicIp is not null);
        RuleFor(x => x.MaxConcurrentBuilds).InclusiveBetween(1, 64).When(x => x.MaxConcurrentBuilds is not null);
    }
}

public sealed class InstallAgentValidator : AbstractValidator<InstallAgentRequest>
{
    public InstallAgentValidator() =>
        RuleFor(x => x.HostKeyFingerprint).Must(SshRules.IsFingerprint).WithErrorCode("pattern").WithMessage("Must look like SHA256:<43 base64 characters>.").When(x => x.HostKeyFingerprint is not null);
}

public sealed class ScanHostKeyValidator : AbstractValidator<ScanHostKeyRequest>
{
    public ScanHostKeyValidator()
    {
        RuleFor(x => x.Host).NotEmpty().Must(ServerRules.IsValidHost).WithErrorCode("pattern").WithMessage("Must be a DNS name or an IP address, without scheme or port.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
    }
}

public sealed class ConfirmHostKeyValidator : AbstractValidator<ConfirmHostKeyRequest>
{
    public ConfirmHostKeyValidator() =>
        RuleFor(x => x.Fingerprint).NotEmpty().Must(SshRules.IsFingerprint).WithErrorCode("pattern").WithMessage("Must look like SHA256:<43 base64 characters>.");
}
