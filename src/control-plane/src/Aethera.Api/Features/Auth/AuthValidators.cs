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
