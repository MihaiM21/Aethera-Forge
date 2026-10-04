using FluentValidation;

namespace Aethera.Api.Features.Agents.Ssh;

/// <summary>The only field is a boolean; the validator exists because the patch endpoint filter resolves one for every body DTO.</summary>
public sealed class UpdateSshValidator : AbstractValidator<UpdateSshRequest>
{
}
