// GAP-012: long-running job (batch) support is an explicit, compiler-checked
// capability. Azure/OpenAI STT implement the batch contract against
// ProviderJobPoller.JobStatus; Google and LocalInference stay sync-only, and
// the descriptor's AsyncJob flag must agree with the implemented contract.
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Providers;
using DubbingPlatform.Infrastructure.Providers.Azure;
using DubbingPlatform.Infrastructure.Providers.Google;
using DubbingPlatform.Infrastructure.Providers.LocalInference;
using DubbingPlatform.Infrastructure.Providers.OpenAI;
using DubbingPlatform.Application.Providers;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DubbingPlatform.UnitTests.Providers;

public sealed class BatchProviderContractTests
{
    [Theory]
    [InlineData(typeof(AzureSttProvider))]
    [InlineData(typeof(OpenAiSttProvider))]
    public void Batch_Capable_Stt_Providers_Implement_The_Contract(Type providerType)
    {
        Assert.True(typeof(IBatchTranscriptionProvider).IsAssignableFrom(providerType));
    }

    [Fact]
    public void Google_And_Local_Inference_Are_Scoped_Sync_Only()
    {
        // GAP-012 scope decision: only Azure/OpenAI STT expose a long-running
        // job API. Google STT and the local sidecar are synchronous; they must
        // not claim batch support through duck typing.
        Assert.False(typeof(IBatchTranscriptionProvider).IsAssignableFrom(typeof(GoogleSttProvider)));
        Assert.False(typeof(IBatchTranscriptionProvider).IsAssignableFrom(typeof(LocalInferenceProvider)));
        Assert.False(typeof(IBatchTranscriptionProvider).IsAssignableFrom(typeof(GoogleTranslationProvider)));
    }

    [Fact]
    public void Contract_Declares_Start_And_Status_Members_Using_The_Poller_Status()
    {
        var start = typeof(IBatchTranscriptionProvider).GetMethod(nameof(IBatchTranscriptionProvider.StartBatchAsync));
        Assert.NotNull(start);
        Assert.Equal(typeof(Task<string>), start!.ReturnType);

        var status = typeof(IBatchTranscriptionProvider).GetMethod(nameof(IBatchTranscriptionProvider.GetBatchStatusAsync));
        Assert.NotNull(status);
        Assert.Equal(typeof(Task<ProviderJobPoller.JobStatus>), status!.ReturnType);
    }

    [Fact]
    public void Batch_Contract_Extends_Transcription_Contract()
    {
        Assert.True(typeof(IBatchTranscriptionProvider).IsAssignableTo(typeof(ITranscriptionProvider)));
    }

    [Fact]
    public void ProviderJobPoller_Parses_Status_For_Batch_Providers()
    {
        var running = ProviderJobPoller.FromStatusString("Running");
        Assert.False(running.Completed);
        Assert.False(running.Failed);

        var done = ProviderJobPoller.FromStatusString("Succeeded");
        Assert.True(done.Completed);

        var expired = ProviderJobPoller.FromStatusString("Expired", "expired");
        Assert.True(expired.Failed);
        Assert.Equal("expired", expired.Reason);
    }

    [Fact]
    public void Descriptor_AsyncJob_Flag_Must_Agree_With_The_Implemented_Contract()
    {
        var batchOption = new ProviderDescriptorOption
        {
            Provider = "azure",
            Capability = "Transcription",
            AsyncJob = true,
        };
        Assert.True(batchOption.AsyncJob);
        Assert.True(BatchProviderContract.SupportsBatch(typeof(AzureSttProvider)));

        var googleOption = new ProviderDescriptorOption
        {
            Provider = "google",
            Capability = "Transcription",
            AsyncJob = false,
        };
        Assert.False(googleOption.AsyncJob);
        Assert.False(BatchProviderContract.SupportsBatch(typeof(GoogleSttProvider)));
    }

    [Fact]
    public void Batch_Provider_Contract_Rejects_AsyncJob_Flag_Without_Implementation()
    {
        // Fail-fast: declaring AsyncJob for a provider that does not implement
        // IBatchTranscriptionProvider would advertise a capability the code
        // cannot honor.
        var ex = BatchProviderContract.ValidateAsyncJobFlag(
            ProviderType.Google,
            ProviderCapability.Transcription,
            typeof(GoogleSttProvider),
            asyncJob: true);

        Assert.NotNull(ex);
        Assert.Contains("AsyncJob", ex!, StringComparison.Ordinal);
        Assert.Contains("IBatchTranscriptionProvider", ex!, StringComparison.Ordinal);

        var ok = BatchProviderContract.ValidateAsyncJobFlag(
            ProviderType.Azure,
            ProviderCapability.Transcription,
            typeof(AzureSttProvider),
            asyncJob: true);
        Assert.Null(ok);

        var syncOnly = BatchProviderContract.ValidateAsyncJobFlag(
            ProviderType.Google,
            ProviderCapability.Transcription,
            typeof(GoogleSttProvider),
            asyncJob: false);
        Assert.Null(syncOnly);
    }

    [Fact]
    public async Task Descriptor_Store_Surfaces_AsyncJob_From_Options()
    {
        var options = new ProviderOptions
        {
            Descriptors =
            [
                new ProviderDescriptorOption
                {
                    Provider = "azure",
                    Capability = "Transcription",
                    Model = "azure-stt",
                    AsyncJob = true,
                },
            ],
        };

        var candidates = await CreateStore(options).GetCandidatesAsync(
            ProviderCapability.Transcription, Guid.NewGuid());

        var azure = Assert.Single(candidates, c => c.Provider == ProviderType.Azure);
        Assert.True(azure.AsyncJob);
    }

    [Fact]
    public async Task Descriptor_Store_Defaults_AsyncJob_To_False()
    {
        var options = new ProviderOptions
        {
            Descriptors =
            [
                new ProviderDescriptorOption { Provider = "google", Capability = "Transcription" },
            ],
        };

        var candidates = await CreateStore(options).GetCandidatesAsync(
            ProviderCapability.Transcription, Guid.NewGuid());

        var google = Assert.Single(candidates, c => c.Provider == ProviderType.Google);
        Assert.False(google.AsyncJob);
    }

    [Fact]
    public async Task Startup_Validator_Fails_Fast_On_Dishonest_AsyncJob_Flag()
    {
        var options = new ProviderOptions
        {
            Descriptors =
            [
                new ProviderDescriptorOption
                {
                    Provider = "google",
                    Capability = "Transcription",
                    AsyncJob = true,
                },
            ],
        };

        var validator = new ProviderStartupValidator(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new AzureProviderOptions()),
            Microsoft.Extensions.Options.Options.Create(new OpenAiProviderOptions()),
            Microsoft.Extensions.Options.Options.Create(new GoogleProviderOptions()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
        Assert.Contains(nameof(IBatchTranscriptionProvider), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_Validator_Accepts_Honest_AsyncJob_Flag()
    {
        var options = new ProviderOptions
        {
            Descriptors =
            [
                new ProviderDescriptorOption
                {
                    Provider = "azure",
                    Capability = "Transcription",
                    AsyncJob = true,
                },
            ],
        };

        var validator = new ProviderStartupValidator(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new AzureProviderOptions()),
            Microsoft.Extensions.Options.Options.Create(new OpenAiProviderOptions()),
            Microsoft.Extensions.Options.Options.Create(new GoogleProviderOptions()));

        await validator.StartAsync(CancellationToken.None);
    }

    private static DescriptorStore CreateStore(ProviderOptions options)
    {
        // No DI scope factory: descriptor loading degrades to config descriptors.
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopes.Setup(s => s.CreateScope()).Throws(new InvalidOperationException("no scope"));
        return new DescriptorStore(Microsoft.Extensions.Options.Options.Create(options), scopes.Object);
    }
}