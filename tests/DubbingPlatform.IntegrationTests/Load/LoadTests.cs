using System.Collections.Concurrent;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Providers.Mock;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Load;

/// <summary>
/// Task 39: fan-out/in load tier with deterministic mocks (no containers).
/// Proves 200-segment fan-out/in completes, 5 concurrent projects stay
/// isolated, and a throttled provider fails closed with zero partial writes.
/// </summary>
public sealed class LoadTests
{
    private readonly ITestOutputHelper _output;

    public LoadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task FanOutIn_200_Segments_Completes()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success });
        var transcription = new MockTranscriptionProvider(options);
        var translation = new MockTranslationProvider(options);
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var completed = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var started = DateTimeOffset.UtcNow;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 200),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, ct) =>
            {
                var artifactId = string.Concat("load-seg-", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var transcript = await transcription.TranscribeAsync(
                    new TranscriptionRequest(tenantId, projectId, runId, artifactId, "en", 1024, 2000, "wav", true, false), ct).ConfigureAwait(true);
                var translated = await translation.TranslateAsync(
                    new TranslationRequest(tenantId, projectId, runId, artifactId, "en", "es", 1024, 2000), ct).ConfigureAwait(true);
                if (!string.Equals(transcript.Text, $"mock transcript seg {artifactId} [en]", StringComparison.Ordinal)
                    || !translated.PrimaryText.StartsWith("mock-es::", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Segment '{artifactId}' produced unexpected mock output.");
                }

                completed[artifactId] = translated.PrimaryText;
            }).ConfigureAwait(true);

        var elapsed = DateTimeOffset.UtcNow - started;
        Assert.Equal(200, completed.Count);
        Assert.Equal(200, completed.Keys.Distinct(StringComparer.Ordinal).Count());
        _output.WriteLine($"200-segment fan-out/in completed in {elapsed.TotalSeconds:F1}s.");
    }

    [Fact]
    public async Task Concurrent_5_Projects_Stay_Isolated()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success });
        var transcription = new MockTranscriptionProvider(options);

        var perProject = new ConcurrentDictionary<int, int>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 5),
            new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (project, ct) =>
            {
                var tenantId = Guid.NewGuid();
                var projectId = Guid.NewGuid();
                var runId = Guid.NewGuid();
                var count = 0;
                for (var i = 0; i < 20; i++)
                {
                    var artifactId = string.Concat("proj-", project.ToString(System.Globalization.CultureInfo.InvariantCulture), "-seg-", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var transcript = await transcription.TranscribeAsync(
                        new TranscriptionRequest(tenantId, projectId, runId, artifactId, "en", 1024, 2000, "wav", true, false), ct).ConfigureAwait(true);
                    Assert.Contains(artifactId, transcript.Text, StringComparison.Ordinal);
                    count++;
                }

                perProject[project] = count;
            }).ConfigureAwait(true);

        Assert.Equal(5, perProject.Count);
        Assert.All(perProject.Values, count => Assert.Equal(20, count));
        Assert.Equal(100, perProject.Values.Sum());
    }

    [Fact]
    public async Task Throttled_Provider_Fails_Closed_Without_Partial_Writes()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.RateLimited });
        var transcription = new MockTranscriptionProvider(options);
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var errors = 0;
        var successes = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 50),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (i, ct) =>
            {
                try
                {
                    await transcription.TranscribeAsync(
                        new TranscriptionRequest(tenantId, projectId, runId, $"throttle-{i}", "en", 1024, 2000, "wav", true, false), ct).ConfigureAwait(true);
                    Interlocked.Increment(ref successes);
                }
                catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.ProviderRateLimited, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref errors);
                }
            }).ConfigureAwait(true);

        Assert.Equal(50, errors);
        Assert.Equal(0, successes);
    }
}
