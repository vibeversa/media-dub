using DubbingPlatform.Api.Models;
using DubbingPlatform.Api.Validation;
using DubbingPlatform.Application.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DubbingPlatform.IntegrationTests.Validation;

/// <summary>
/// Guard for the validator auto-registration exclusion (Task 040A).
///
/// <para>
/// <c>ProjectProcessingSettingsValidator</c> is an
/// <c>AbstractValidator&lt;string&gt;</c>, and <c>AddValidatorsFromAssembly</c>
/// registers validators by the type they validate. MVC auto-validation resolves
/// a validator per action parameter, so this one was applied to every
/// <c>string</c> parameter in the API — route values and headers. Every
/// project-scoped endpoint then answered <c>400 "Processing settings must be
/// valid JSON."</c> on its own <c>projectId</c> route value, which is how the
/// cross-layer rig found it: the seam under test was unreachable over HTTP.
/// </para>
/// <para>
/// No Docker, no database and no HTTP host are needed here; this is a pure
/// service-registration assertion, so unlike the rest of this assembly it never
/// skips. It deliberately does not derive from <c>TestFixtureBase</c>.
/// </para>
/// </summary>
public sealed class ValidationRegistrationTests
{
    [Fact]
    public void Application_Assembly_Really_Does_Contain_A_String_Validator()
    {
        // Guards the premise of the exclusion: if FluentValidation ever stops
        // discovering it, the filter below becomes dead code and the next
        // `AbstractValidator<string>` would silently re-break every string
        // parameter.
        var discovered = typeof(DubbingPlatform.Application.Placeholder).Assembly
            .GetTypes()
            .Where(type => typeof(IValidator<string>).IsAssignableFrom(type) && !type.IsAbstract)
            .ToArray();

        Assert.Contains(typeof(ProjectProcessingSettingsValidator), discovered);
    }

    [Fact]
    public void Auto_Validation_Does_Not_Resolve_A_Validator_For_String()
    {
        using var provider = new ServiceCollection()
            .AddDubbingValidators()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<IValidator<string>>());
    }

    [Fact]
    public void Auto_Validation_Still_Resolves_The_Dto_Validators()
    {
        using var provider = new ServiceCollection()
            .AddDubbingValidators()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IValidator<CreateProjectRequest>>());
    }

    [Fact]
    public void Excluded_List_Names_A_Validator_That_Would_Otherwise_Auto_Register()
    {
        Assert.NotEmpty(ValidationRegistration.ExcludedFromAutoValidation);
        Assert.All(ValidationRegistration.ExcludedFromAutoValidation, type =>
        {
            Assert.False(type.IsAbstract);
            Assert.True(
                typeof(IValidator).IsAssignableFrom(type),
                $"{type.Name} is listed as excluded from auto-validation but is not a FluentValidation validator.");
        });
    }
}
