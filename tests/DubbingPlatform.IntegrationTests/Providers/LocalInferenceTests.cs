using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Providers.Common;
using DubbingPlatform.Infrastructure.Providers.LocalInference;
using DubbingPlatform.Infrastructure.Providers.Mock;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DubbingPlatform.IntegrationTests.Providers;

/// <summary>
/// Task 043: optional local/GPU inference boundary (disabled by default).
/// Hermetic: deterministic mocks + WireMock, no Docker, no GPU, no FFmpeg.
/// R1 disabled → core unaffected (mock + manifest always run);
/// R2 mock passes + GPU manifest correct; R3 local-only policy routes local;
/// R4 hash + device recorded; R5 exhaustion handled safely (delayed retry).
/// Gated tests skip unless <c>Features__LocalInferenceEnabled=true</c>;
/// mock + manifest asserts always run.
/// </summary>
public sealed class LocalInferenceTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string TestHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static bool IsEnabled()
    {
        var direct = Environment.GetEnvironmentVariable("Features__LocalInferenceEnabled")
            ?? Environment.GetEnvironmentVariable("Features:LocalInferenceEnabled")
            ?? Environment.GetEnvironmentVariable("LocalInferenceEnabled");
        return string.Equals(direct?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void SkipUnlessEnabled()
    {
        Skip.If(!IsEnabled(), "LocalInference disabled (Features__LocalInferenceEnabled != true).");
    }

    [Fact]
    public async Task Mock_Passes()
    {
        var features = new FeatureOptions();
        Assert.False(features.LocalInferenceEnabled);

        var options = Microsoft.Extensions.Options.Options.Create(
            new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success });
        var mock = new MockLocalInferenceProvider(options);
        var response = await mock.InferAsync(
            new DubbingPlatform.Application.Abstractions.Providers.Dtos.LocalInferenceRequest(
                TenantId, ProjectId, RunId, "model-1", """{"x":1}""", "en", 128, 1000),
            CancellationToken.None);
        Assert.Equal("""{"x":1}""", response.OutputJson);
        Assert.Equal(0.99, response.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(response.Model));
    }

    [Fact]
    public void Gpu_Schedules_Only_Gpu_Nodes()
    {
        var workersGpu = ReadManifest("workers-gpu.yaml");
        Assert.Contains("nvidia.com/gpu.present", workersGpu, StringComparison.Ordinal);
        Assert.Contains("nvidia.com/gpu", workersGpu, StringComparison.Ordinal);
        Assert.Contains("NoSchedule", workersGpu, StringComparison.Ordinal);
        Assert.Contains("nvidia.com/gpu", workersGpu, StringComparison.Ordinal);
        Assert.Contains("DOTNET_WORKER_ROLE", workersGpu, StringComparison.Ordinal);
        Assert.Contains("gpu", workersGpu, StringComparison.Ordinal);
        Assert.Contains("LocalInference__Endpoint", workersGpu, StringComparison.Ordinal);
        Assert.Contains("http://local-inference:8000", workersGpu, StringComparison.Ordinal);
        Assert.Contains("LocalInference__MaxConcurrency", workersGpu, StringComparison.Ordinal);
        Assert.Contains("replicas: 0", workersGpu, StringComparison.Ordinal);

        var gpuWorker = ReadManifest("gpu-worker.yaml");
        Assert.Contains("worker-gpu", gpuWorker, StringComparison.Ordinal);
        Assert.Contains("nvidia.com/gpu.present", gpuWorker, StringComparison.Ordinal);
        Assert.Contains("nvidia.com/gpu", gpuWorker, StringComparison.Ordinal);
        Assert.Contains("http://local-inference:8000", gpuWorker, StringComparison.Ordinal);

        var keda = ReadManifest("keda-scalers.yaml");
        Assert.Contains("ai.gpu", keda, StringComparison.Ordinal);
        Assert.Contains("dcgm_gpu_utilization", keda, StringComparison.Ordinal);
        Assert.Contains("maxReplicaCount: 4", keda, StringComparison.Ordinal);
        Assert.Contains("minReplicaCount: 0", keda, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task LocalOnly_Policy_Routes_Local()
    {
        SkipUnlessEnabled();

        var tenant = Guid.NewGuid();
        var local = MakeDescriptor(tenant, ProviderType.LocalInference, ProviderCapability.Transcription);
        var azure = MakeDescriptor(tenant, ProviderType.Azure, ProviderCapability.Transcription);
        var stores = new Mock<IDescriptorStore>(MockBehavior.Strict);
        stores.Setup(s => s.GetCandidatesAsync(ProviderCapability.Transcription, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderCapabilityDescriptor> { azure, local });
        stores.Setup(s => s.IsCompatible(It.IsAny<ProviderCapabilityDescriptor>(), It.IsAny<ProviderRoutingRequest>()))
            .Returns(true);

        var now = DateTimeOffset.UtcNow;
        var policy = new ProcessingPolicy(
            Guid.NewGuid(), tenant, false, ["local"], null, "allow", "allow", true, null, now, now);
        var policies = new Mock<IProcessingPolicyProvider>(MockBehavior.Strict);
        policies.Setup(p => p.GetAsync(tenant, It.IsAny<CancellationToken>())).ReturnsAsync(policy);

        var resolver = CreateResolver(stores.Object, policies.Object);
        var (provider, _) = await resolver.ResolveAsync(
            ProviderCapability.Transcription, tenant, "en", 100, 1000);
        Assert.Equal(ProviderType.LocalInference, provider);

        Assert.True(PolicyChecker.CanUseProvider(policy, ProviderType.LocalInference, "global"));
        Assert.False(PolicyChecker.CanUseProvider(policy, ProviderType.Azure, "global"));
    }

    [SkippableFact]
    public async Task ModelHash_Recorded()
    {
        SkipUnlessEnabled();

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/warmup").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"status":"warmed"}""")
                .WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath("/health").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"status":"ok"}""")
                .WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath("/infer").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"output":"hello","confidence":0.92,"modelHash":"h","device":"cuda:0"}""")
                .WithHeader("Content-Type", "application/json"));

        var options = Microsoft.Extensions.Options.Options.Create(new LocalInferenceOptions
        {
            Endpoint = server.Url!.TrimEnd('/'),
            Protocol = "http",
            ModelName = "local-small",
            ModelVersion = "1",
            ModelHash = TestHash,
            Device = "cuda:0",
            MaxConcurrency = 1,
            Models =
            [
                new LocalInferenceModelOptions
                {
                    Id = "local-small",
                    Version = "1",
                    ArtifactHash = TestHash,
                    DeviceProfile = "cuda:0",
                    Capability = "LocalInference",
                    RuntimeRequirements = string.Empty,
                },
            ],
        });

        var registry = new ModelRegistry(options);
        var resolved = registry.ResolveModel("LocalInference");
        Assert.Equal(TestHash, resolved.ArtifactHash);
        Assert.Equal("cuda:0", resolved.DeviceProfile);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var provider = new LocalInferenceProvider(http, options, registry);
        await provider.WarmupAsync(CancellationToken.None);
        Assert.True(provider.IsWarmed);

        var response = await provider.InferAsync(
            new DubbingPlatform.Application.Abstractions.Providers.Dtos.LocalInferenceRequest(
                TenantId, ProjectId, RunId, "model-1", """{"x":1}""", "en", 128, 1000),
            CancellationToken.None);
        Assert.Equal("hello", response.OutputJson);
        Assert.NotNull(response.RawMetadata);
        Assert.Equal(TestHash, response.RawMetadata["model.hash"]);
        Assert.Equal("cuda:0", response.RawMetadata["device"]);
        Assert.Equal("true", response.RawMetadata["warmed"]);

        var badOptions = Microsoft.Extensions.Options.Options.Create(new LocalInferenceOptions
        {
            Endpoint = server.Url!.TrimEnd('/'),
            ModelVersion = string.Empty,
        });
        using var badHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var bad = new LocalInferenceProvider(badHttp, badOptions);
        var ex = Assert.Throws<ErrorCodeException>(() => bad.ValidateConfig());
        Assert.Equal(ErrorCodes.ProviderConfigurationError, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Gpu_Exhaustion_Safe()
    {
        SkipUnlessEnabled();

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/warmup").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"status":"warmed"}""")
                .WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath("/health").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"status":"ok"}""")
                .WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath("/infer").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503)
                .WithBody("""{"error":"busy","exhausted":true}""")
                .WithHeader("Content-Type", "application/json"));

        var options = Microsoft.Extensions.Options.Options.Create(new LocalInferenceOptions
        {
            Endpoint = server.Url!.TrimEnd('/'),
            Protocol = "http",
            ModelName = "local-small",
            ModelVersion = "1",
            ModelHash = TestHash,
            Device = "cuda:0",
            MaxConcurrency = 1,
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var provider = new LocalInferenceProvider(http, options);
        await provider.WarmupAsync(CancellationToken.None);

        var first = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.InferAsync(
            new DubbingPlatform.Application.Abstractions.Providers.Dtos.LocalInferenceRequest(
                TenantId, ProjectId, RunId, "model-1", """{"x":1}""", "en", 128, 1000),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ProviderRateLimited, first.ErrorCode);
        var decision = OutcomePolicy.Decide(ProviderHttpHelper.ToOutcome(first.ErrorCode));
        Assert.True(decision.AllowRetry);
        Assert.True(decision.AllowFallback);

        var second = await Assert.ThrowsAsync<ErrorCodeException>(() => provider.InferAsync(
            new DubbingPlatform.Application.Abstractions.Providers.Dtos.LocalInferenceRequest(
                TenantId, ProjectId, RunId, "model-1", """{"x":1}""", "en", 128, 1000),
            CancellationToken.None));
        Assert.Equal(ErrorCodes.ProviderRateLimited, second.ErrorCode);
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

    private static ProviderResolver CreateResolver(IDescriptorStore stores, IProcessingPolicyProvider policies)
    {
        var providerOptions = new ProviderOptions
        {
            DefaultProvider = "mock",
            RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["transcription"] = ["local", "azure"],
            },
            Enabled = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["mock"] = true,
                ["azure"] = true,
                ["local"] = true,
            },
        };
        var privacy = new PrivacyOptions
        {
            ExternalProvidersAllowed = true,
            AllowedProviders = ["mock", "azure", "local"],
        };
        var retry = new RetryOptions { FallbackMaxAttempts = 2 };
        var health = Mock.Of<IProviderHealthTracker>(h => h.IsHealthy(It.IsAny<ProviderType>()) == true);
        var cost = Mock.Of<IProviderCostGate>(c =>
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

    private static string ReadManifest(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "k8s", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            var sln = Path.Combine(dir.FullName, "DubbingPlatform.sln");
            if (File.Exists(sln))
            {
                var rooted = Path.Combine(dir.FullName, "deploy", "k8s", fileName);
                if (File.Exists(rooted))
                {
                    return File.ReadAllText(rooted);
                }
            }
        }

        throw new InvalidOperationException($"deploy/k8s/{fileName} was not found above {AppContext.BaseDirectory}");
    }
}
