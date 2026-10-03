using FluentValidation;

namespace Aethera.Api.Features.Resources;

public static class EnumText
{
    /// <summary>Parses a camelCase (or any case) enum name; null when absent or not a defined value.</summary>
    public static TEnum? Parse<TEnum>(string? text) where TEnum : struct, Enum =>
        text is not null && Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value) && !long.TryParse(text, out _)
            ? value
            : null;

    public static bool IsDefined<TEnum>(string? text) where TEnum : struct, Enum => Parse<TEnum>(text) is not null;

    public static string Names<TEnum>() where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]));
}

public static class ValidatorExtensions
{
    /// <summary>The value must be one of the (camelCase) names of <typeparamref name="TEnum"/>; code <c>invalid_enum</c>.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeEnum<T, TEnum>(this IRuleBuilder<T, string?> rule) where TEnum : struct, Enum =>
        rule.Must(v => v is null || EnumText.IsDefined<TEnum>(v)).WithErrorCode("invalid_enum")
            .WithMessage($"Must be one of: {EnumText.Names<TEnum>()}.");
}
