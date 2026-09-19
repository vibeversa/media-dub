using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using Moq;

namespace DubbingPlatform.UnitTests.Providers;

/// <summary>
/// Resolver ordering (capability → policy → priority → health → cost),
/// privacy blocks, fallback budgets, and quality/transport separation.
/// </summary>
public sealed class ResolverTests
{
    [Fact]
    public async Task Capability_Mismatch_Blocks()
    {
        var tenant = Guid.NewGuid();
        var stores = new Mock<IDescriptorStore>(MockBehavior.Strict);
        stores.Setup(s => s.GetCandidatesAsync(ProviderCapability.Transcription, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeDescriptor(tenant, ProviderType.Azure, ProviderCapability.Transcription)]);
        stores.Setup(s => s.IsCompatible(It.IsAny<ProviderCapabilityDescriptor>(), It.IsAny<ProviderRoutingRequest>()))
            .Returns(false);
        var resolver = CreateResolver(stores.Object, FallbackMaxAttempts: 2);

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => resolver.ResolveAsync(
            ProviderCapability.Transcription, tenant, "en", 100, 1000));
        Assert.Equal(ErrorCodes.ProviderConfigurationError, ex.ErrorCode);
    }

    [Fact]
    public async Task Privacy_Blocks_Disallowed()
    {
        var tenant = Guid.NewGuid();
        var descriptor = MakeDescriptor(tenant, ProviderType.Azure, ProviderCapability.Transcription);
        var stores = new Mock<IDescriptorStore>(MockBehavior.Strict);
        stores.Setup(s => s.GetCandidatesAsync(ProviderCapability.Transcription, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([descriptor]);
        stores.Setup(s => s.IsCompatible(It.IsAny<ProviderCapabilityDescriptor>(), It.IsAny<ProviderRoutingRequest>()))
            .Returns(true);
        var now = DateTimeOffset.UtcNow;
        var policy = new ProcessingPolicy(
            Guid.NewGuid(), tenant, false, ["mock"], null, "allow", "allow", true, null, now, now);
        var policies = new Mock<IProcessingPolicyProvider>(MockBehavior.Strict);
        policies.Setup(p => p.GetAsync(tenant, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var resolver = CreateResolver(stores.Object, policies.Object);

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => resolver.ResolveAsync(
            ProviderCapability.Transcription, tenant, "en", 100, 1000));
        Assert.Equal(ErrorCodes.PolicyDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task Unhealthy_Route_Skipped()
    {
        var tenant = Guid.NewGuid();
        var azure = MakeDescriptor(tenant, ProviderType.Azure, ProviderCapability.Transcription);
        var mock = MakeDescriptor(tenant, ProviderType.Mock, ProviderCapability.Transcription);
        var stores = new Mock<IDescriptorStore>(MockBehavior.Strict);
        stores.Setup(s => s.GetCandidatesAsync(ProviderCapability.Transcription, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([azure, mock]);
        stores.Setup(s => s.IsCompatible(It.IsAny<ProviderCapabilityDescriptor>(), It.IsAny<ProviderRoutingRequest>()))
            .Returns(true);
        var health = new Mock<IProviderHealthTracker>(MockBehavior.Strict);
        health.Setup(h => h.IsHealthy(ProviderType.Azure)).Returns(false);
        health.Setup(h => h.IsHealthy(ProviderType.Mock)).Returns(true);
        var resolver = CreateResolver(
            stores.Object,
            health.Object,
            priority: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["transcription"] = ["azure", "mock"],
            });

        var (provider, _) = await resolver.ResolveAsync(
            ProviderCapability.Transcription, tenant, "en", 100, 1000);
        Assert.Equal(ProviderType.Mock, provider);
    }

    [Fact]
    public async Task No_Hardcoded_Precedence()
    {
        var tenant = Guid.NewGuid();
        var azure = MakeDescriptor(tenant, ProviderType.Azure, ProviderCapability.Transcription);
        var openai = MakeDescriptor(tenant, ProviderType.OpenAI, ProviderCapability.Transcription);
        var stores = new Mock<IDescriptorStore>(MockBehavior.Strict);
        stores.Setup(s => s.GetCandidatesAsync(ProviderCapability.Transcription, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([azure, openai]);
        stores.Setup(s => s.IsCompatible(It.IsAny<ProviderCapabilityDescriptor>(), It.IsAny<ProviderRoutingRequest>()))
            .Returns(true);
        var resolver = CreateResolver(
            stores.Object,
            priority: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["transcription"] = ["openai", "azure"],
            });

        var (provider, _) = await resolver.ResolveAsync(
            ProviderCapability.Transcription, tenant, "en", 100, 1000);
        Assert.Equal(ProviderType.OpenAI, provider);
    }

    [Fact]
    public async Task Fallback_Budget_Respected()
    {
        var tenant = Guid.NewGuid();
        var azure = MakeDescriptor(tenant, ProviderType.Azure, ProviderCapability.Transcription);
        var mock = MakeDescriptor(tenant, ProviderType.Mock, ProviderCapability.Transcription);
        var stores = new Mock<IDescriptorStore>(MockBehavior.Strict);
        stores.Setup(s => s.GetCandidatesAsync(ProviderCapability.Transcription, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([azure, mock]);
        stores.Setup(s => s.IsCompatible(It.IsAny<ProviderCapabilityDescriptor>(), It.IsAny<ProviderRoutingRequest>()))
            .Returns(true);
        var health = new Mock<IProviderHealthTracker>(MockBehavior.Strict);
        health.Setup(h => h.IsHealthy(ProviderType.Azure)).Returns(false);
        health.Setup(h => h.IsHealthy(ProviderType.Mock)).Returns(true);
        var resolver = CreateResolver(
            stores.Object,
            health.Object,
            fallbackMaxAttempts: 0,
            priority: new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["transcription"] = ["azure", "mock"],
            });

        var ex = await Assert.ThrowsAsync<ErrorCodeException>(() => resolver.ResolveAsync(
            ProviderCapability.Transcription, tenant, "en", 100, 1000));
        Assert.Equal(ErrorCodes.ProviderFailed, ex.ErrorCode);
    }

    [Fact]
    public void Quality_Not_As_Transport()
    {
        var quality = OutcomePolicy.Decide(OutcomeClass.QualityBelowThreshold);
        Assert.False(quality.AllowRetry);
        Assert.True(quality.AllowReview);
        Assert.False(quality.AllowFallback);

        var rateLimited = OutcomePolicy.Decide(OutcomeClass.ProviderRateLimited);
        Assert.True(rateLimited.AllowRetry);
        Assert.True(rateLimited.AllowFallback);
    }

    private static ProviderCapabilityDescriptor MakeDescriptor(Guid tenant, ProviderType provider, ProviderCapability capability)
    {
        return new ProviderCapabilityDescriptor(
            Guid.NewGuid(),
            tenant,
            provider,
            capability,
            [],
            [],
            0,
            0,
            false,
            false,
            true,
            true,
            [],
            true,
            [],
            "model",
            "{}",
            "{}",
            "standard",
            "global",
            1,
            DateTimeOffset.UtcNow);
    }

    private static ProviderResolver CreateResolver(
        IDescriptorStore stores,
        IProcessingPolicyProvider? policies = null,
        IProviderHealthTracker? health = null,
        IProviderCostGate? cost = null,
        Dictionary<string, string[]>? priority = null,
        int FallbackMaxAttempts = 2)
    {
        var providerOptions = new ProviderOptions
        {
            DefaultProvider = "mock",
            RoutePriority = priority ?? new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["transcription"] = ["mock"],
            },
            Enabled = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["mock"] = true,
                ["azure"] = true,
                ["openai"] = true,
                ["google"] = true,
                ["local"] = true,
            },
        };
        var privacy = new PrivacyOptions
        {
            ExternalProvidersAllowed = true,
            AllowedProviders = ["mock", "azure", "openai", "google", "local"],
        };
        var retry = new RetryOptions { FallbackMaxAttempts = FallbackMaxAttempts };

        policies ??= Mock.Of<IProcessingPolicyProvider>(p =>
            p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()) == Task.FromResult<ProcessingPolicy?>(null));
        health ??= Mock.Of<IProviderHealthTracker>(h => h.IsHealthy(It.IsAny<ProviderType>()) == true);
        cost ??= Mock.Of<IProviderCostGate>(c =>
            c.CanProceedAsync(It.IsAny<Guid>(), It.IsAny<ProviderCapability>(), It.IsAny<CancellationToken>()) == Task.FromResult(true));

        return new ProviderResolver(
            Microsoft.Extensions.Options.Options.Create(providerOptions),
            Microsoft.Extensions.Options.Options.Create(privacy),
            Microsoft.Extensions.Options.Options.Create(retry),
            stores,
            health,
            cost,
            policies);
    }

    private static ProviderResolver CreateResolver(
        IDescriptorStore stores,
        IProviderHealthTracker health,
        Dictionary<string, string[]>? priority = null,
        int fallbackMaxAttempts = 2)
    {
        return CreateResolver(stores, null, health, null, priority, fallbackMaxAttempts);
    }
}
