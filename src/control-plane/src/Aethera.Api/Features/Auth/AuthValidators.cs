using Aethera.Api.Security;
using Aethera.Infrastructure.Auth;
using FluentValidation;

namespace Aethera.Api.Features.Auth;

/// <summary>Shared rules. Field-level codes: <c>required</c>, <c>too_long</c>, <c>email</c>, and for passwords <c>too_short</c>, <c>too_long</c>, <c>password.same_as_email</c>.</summary>
internal static class AuthRules
{
    public const int MaxEmailLength = 320;
    public const int MaxNameLength = 200;

    /// <summary>The password policy (<see cref="PasswordPolicy"/>); <paramref name="email"/> may return null when the email is not part of the request.</summary>
    public static IRuleBuilderOptions<T, string> PasswordPolicyFor<T>(this IRuleBuilderInitial<T, string> rule, Func<T, string?> email) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must((_, password) => password.Length >= PasswordPolicy.MinLength)
            .WithErrorCode("too_short").WithMessage($"Must be at least {PasswordPolicy.MinLength} characters.")
            .Must((_, password) => password.Length <= PasswordPolicy.MaxLength)
            .WithErrorCode("too_long").WithMessage($"Must be at most {PasswordPolicy.MaxLength} characters.")
            .Must((instance, password) => PasswordPolicy.Check(password, email(instance)) != PasswordProblem.SameAsEmail)
            .WithErrorCode("password.same_as_email").WithMessage("Must not be the same as the email address.");

    public static IRuleBuilderOptions<T, string> EmailField<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(MaxEmailLength).EmailAddress();

    public static IRuleBuilderOptions<T, string> NameField<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(MaxNameLength);
}

internal sealed class SetupRequestValidator : AbstractValidator<SetupRequest>
{
    public SetupRequestValidator()
    {
        RuleFor(x => x.Email).EmailField();
        RuleFor(x => x.Password).PasswordPolicyFor(x => x.Email);
        RuleFor(x => x.DisplayName).NameField();
        RuleFor(x => x.OrganizationName).NameField()
            .Must(name => Aethera.Domain.Slug.FromName(name).Length > 0)
            .WithErrorCode("pattern").WithMessage("Must contain at least one letter or digit.");
    }
}

internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(AuthRules.MaxEmailLength);
        // Only bounded: the policy is for choosing a password, never a reason to answer differently at sign-in.
        RuleFor(x => x.Password).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(PasswordPolicy.MaxLength);
    }
}

internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(PasswordPolicy.MaxLength);
        // The "not your email" rule needs the account, so the service applies it; length rules are checked here.
        RuleFor(x => x.NewPassword).PasswordPolicyFor<ChangePasswordRequest>(_ => null);
    }
}

/// <summary>Rules of the two-factor requests (ADR 0013). The validators stop at the first failure of a property.</summary>
internal static class TwoFactorRules
{
    public const int MaxChallengeLength = 512;

    public static IRuleBuilderOptions<T, string?> TotpCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty()
            .Must(code => Totp.Normalize(code) is not null).WithErrorCode("pattern").WithMessage(CodeMessage);

    public static IRuleBuilderOptions<T, string?> RecoveryCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty()
            .Must(code => RecoveryCodes.Normalize(code) is not null).WithErrorCode("pattern").WithMessage("Must be a recovery code (xxxx-xxxx-xxxx-xxxx).");

    public static IRuleBuilderOptions<T, string> CurrentPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(PasswordPolicy.MaxLength);

    public const string BothMessage = "Send either code or recoveryCode, not both.";
    public const string CodeMessage = "Must be the 6-digit code from the authenticator app.";
}

internal sealed class TwoFactorLoginRequestValidator : AbstractValidator<TwoFactorLoginRequest>
{
    public TwoFactorLoginRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Challenge).NotEmpty().MaximumLength(TwoFactorRules.MaxChallengeLength);
        RuleFor(x => x.Code).TotpCode().When(x => x.RecoveryCode is null);
        RuleFor(x => x.RecoveryCode).RecoveryCode().When(x => x.Code is null && x.RecoveryCode is not null);
        RuleFor(x => x.RecoveryCode).Null().WithErrorCode("conflict").WithMessage(TwoFactorRules.BothMessage).When(x => x.Code is not null);
    }
}

internal sealed class StartTwoFactorEnrolmentRequestValidator : AbstractValidator<StartTwoFactorEnrolmentRequest>
{
    public StartTwoFactorEnrolmentRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Password).CurrentPassword();
    }
}

internal sealed class ConfirmTwoFactorEnrolmentRequestValidator : AbstractValidator<ConfirmTwoFactorEnrolmentRequest>
{
    public ConfirmTwoFactorEnrolmentRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Code).NotEmpty().Must(code => Totp.Normalize(code) is not null).WithErrorCode("pattern").WithMessage(TwoFactorRules.CodeMessage);
    }
}

internal sealed class DisableTwoFactorRequestValidator : AbstractValidator<DisableTwoFactorRequest>
{
    public DisableTwoFactorRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Password).CurrentPassword();
        // Both may be absent: a pending enrolment is cancelled with the password alone (the service decides).
        RuleFor(x => x.Code).TotpCode().When(x => x.Code is not null);
        RuleFor(x => x.RecoveryCode).RecoveryCode().When(x => x.RecoveryCode is not null && x.Code is null);
        RuleFor(x => x.RecoveryCode).Null().WithErrorCode("conflict").WithMessage(TwoFactorRules.BothMessage).When(x => x.Code is not null);
    }
}

internal sealed class RegenerateRecoveryCodesRequestValidator : AbstractValidator<RegenerateRecoveryCodesRequest>
{
    public RegenerateRecoveryCodesRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Password).CurrentPassword();
        RuleFor(x => x.Code).NotEmpty().Must(code => Totp.Normalize(code) is not null).WithErrorCode("pattern").WithMessage(TwoFactorRules.CodeMessage);
    }
}

internal sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).EmailField();
        RuleFor(x => x.DisplayName).NameField();
        RuleFor(x => x.Password).PasswordPolicyFor(x => x.Email);
        RuleFor(x => x.Role).IsInEnum();
    }
}

internal sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        When(x => x.Email is not null, () => RuleFor(x => x.Email!).EmailField());
        When(x => x.DisplayName is not null, () => RuleFor(x => x.DisplayName!).NameField());
        When(x => x.Password is not null, () => RuleFor(x => x.Password!).PasswordPolicyFor<UpdateUserRequest>(x => x.Email));
    }
}

internal sealed class SetUserRoleRequestValidator : AbstractValidator<SetUserRoleRequest>
{
    public SetUserRoleRequestValidator() => RuleFor(x => x.Role).IsInEnum();
}

internal sealed class CreateApiTokenRequestValidator : AbstractValidator<CreateApiTokenRequest>
{
    public const int MaxScopes = 16;

    public CreateApiTokenRequestValidator()
    {
        RuleFor(x => x.Name).NameField();
        RuleFor(x => x.Scopes).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Grant at least one scope.")
            .Must(s => s.Count <= MaxScopes).WithErrorCode("too_long").WithMessage($"At most {MaxScopes} scopes.");
        RuleForEach(x => x.Scopes)
            .Must(s => s is not null && Scopes.IsValid(s))
            .WithErrorCode("invalid_scope")
            .WithMessage($"Unknown scope. Valid scopes: {string.Join(", ", [.. Scopes.Known, Scopes.All])}.");
    }
}
