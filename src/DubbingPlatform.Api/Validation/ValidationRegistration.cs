using DubbingPlatform.Application.Validation;
using FluentValidation;
using FluentValidation.AspNetCore;

namespace DubbingPlatform.Api.Validation;

/// <summary>
/// Registers the validators that participate in MVC auto-validation, and records
/// the ones that deliberately do not.
/// </summary>
public static class ValidationRegistration
{
    /// <summary>
    /// Types that must never be auto-registered, with the reason. Kept as data
    /// (rather than a comment inside a filter lambda) so the exclusion is
    /// testable: <c>ValidationRegistrationTests</c> asserts the assembly really
    /// does contain an <c>IValidator&lt;string&gt;</c> and that it is absent from
    /// the container afterwards.
    /// </summary>
    public static IReadOnlyList<Type> ExcludedFromAutoValidation { get; } =
    [
        typeof(ProjectProcessingSettingsValidator),
    ];

    public static IServiceCollection AddDubbingValidators(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssembly(
            typeof(DubbingPlatform.Application.Placeholder).Assembly,
            filter: result => !ExcludedFromAutoValidation.Contains(result.ValidatorType));
        services.AddValidatorsFromAssembly(
            typeof(ValidationRegistration).Assembly,
            filter: result => !ExcludedFromAutoValidation.Contains(result.ValidatorType));
        return services;
    }
}
