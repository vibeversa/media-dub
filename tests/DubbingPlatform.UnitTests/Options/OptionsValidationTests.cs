using System.ComponentModel.DataAnnotations;
using DubbingPlatform.Application.Options;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.UnitTests.Options;

/// <summary>
/// Verifies options defaults pass validation and invalid values fail fast.
/// </summary>
public sealed class OptionsValidationTests
{
    [Fact]
    public void All_Options_Defaults_Pass()
    {
        Assert.False(new ObservabilityOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new ObservabilityOptions()).Failed);
        Assert.False(new MediaOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions()).Failed);
        Assert.False(new RetryOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new RetryOptions()).Failed);
        Assert.False(new TimingOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new TimingOptions()).Failed);
        Assert.False(new StorageOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new StorageOptions()).Failed);
        Assert.False(new ProviderOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new ProviderOptions()).Failed);
        Assert.False(new QuotaOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new QuotaOptions()).Failed);
        Assert.False(new RateLimitOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new RateLimitOptions()).Failed);
        Assert.False(new PrivacyOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new PrivacyOptions()).Failed);
        Assert.False(new FeatureOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new FeatureOptions()).Failed);
        Assert.False(new DeploymentOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new DeploymentOptions()).Failed);
        Assert.False(new AuthOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new AuthOptions()).Failed);
        Assert.False(new RetentionOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new RetentionOptions()).Failed);
        Assert.False(new TransportOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new TransportOptions()).Failed);
        Assert.False(new AuthRateLimitOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, new AuthRateLimitOptions()).Failed);
    }

    [Fact]
    public void Invalid_MediaOptions_Fails()
    {
        var validator = new MediaOptionsValidator();

        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { MaxUploadBytes = 0 }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { MaxDurationMs = -1 }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { AllowedContainers = [] }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { AllowedContainers = ["mp4", ""] }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { FfmpegTimeoutSec = 0 }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { MaxConcurrentMediaJobs = 0 }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new MediaOptions { CpuThreads = 0 }).Failed);
    }

    [Fact]
    public void Invalid_MediaOptions_Fails_DataAnnotations()
    {
        var options = new MediaOptions { MaxUploadBytes = -1, AllowedContainers = [] };
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.False(valid);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Invalid_RetryOptions_Fails()
    {
        var validator = new RetryOptionsValidator();

        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new RetryOptions { ProviderRequestMaxAttempts = 0 }).Failed);
        Assert.True(validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, new RetryOptions { RateLimitDelaySec = -1 }).Failed);
        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new RetryOptions { PerStageMaxAttempts = new Dictionary<string, int>(StringComparer.Ordinal) { ["transcription"] = 99 } }).Failed);
    }

    [Fact]
    public void Invalid_TimingOptions_Fails_When_Max_Below_Preferred()
    {
        var validator = new TimingOptionsValidator();

        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new TimingOptions { PreferredToleranceMs = 100, MaxToleranceMs = 50 }).Failed);
        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new TimingOptions { MaxStretchFactor = 0.5 }).Failed);
    }

    [Fact]
    public void Invalid_ProviderOptions_Fails()
    {
        var validator = new ProviderOptionsValidator();

        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new ProviderOptions { DefaultProvider = " " }).Failed);
        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new ProviderOptions { RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) }).Failed);
    }

    [Fact]
    public void Invalid_RetentionOptions_Fails_When_Windows_Inverted()
    {
        var validator = new RetentionOptionsValidator();

        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new RetentionOptions { IntermediateDays = 90, FinalDays = 30, AuditDays = 365 }).Failed);
        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new RetentionOptions { IntermediateDays = 30, FinalDays = 90, AuditDays = 30 }).Failed);
    }

    [Fact]
    public void Invalid_TransportOptions_Fails_For_Unknown_Provider()
    {
        var validator = new TransportOptionsValidator();

        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new TransportOptions { Provider = "Kafka" }).Failed);
        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new TransportOptions { Provider = " " }).Failed);
        Assert.False(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new TransportOptions { Provider = "InMemory" }).Failed);
        Assert.False(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new TransportOptions { Provider = "RabbitMq" }).Failed);
    }

    [Fact]
    public void Invalid_QuotaOptions_Fails_When_Segment_Cost_Exceeds_Project_Cost()
    {
        var validator = new QuotaOptionsValidator();

        Assert.True(validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new QuotaOptions { MaxCostPerProject = 1.0, MaxCostPerSegment = 2.0 }).Failed);
    }
}
