using System.Diagnostics;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Providers.Mock;
using Xunit.Abstractions;

namespace DubbingPlatform.IntegrationTests.Load;

/// <summary>
/// Task 39: soak tier. Excluded from the CI gate
/// (<c>dotnet test --filter Category!=Soak</c>); run explicitly with
/// <c>RUN_SOAK=1</c> for the full 30-minute long-job simulation, or
/// <c>SOAK_ITERATIONS=n</c> for a bounded local check. Verifies sustained
/// throughput with zero errors and stable voice/byte determinism throughout.
/// </summary>
[Trait("Category", "Soak")]
public sealed class SoakTests
{
    private readonly ITestOutputHelper _output;

    public SoakTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task LongJob_Sustained_Throughput_Zero_Errors()
    {
        var runSoak = string.Equals(Environment.GetEnvironmentVariable("RUN_SOAK"), "1", StringComparison.Ordinal);
        var iterationsEnv = Environment.GetEnvironmentVariable("SOAK_ITERATIONS");
        var bounded = int.TryParse(iterationsEnv, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0;
        Skip.If(!runSoak && !bounded, "Soak excluded from CI gate. Set RUN_SOAK=1 (30min) or SOAK_ITERATIONS=n for a bounded check.");

        var budget = runSoak ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(1);
        var maxIterations = bounded ? parsed : int.MaxValue;

        var options = Microsoft.Extensions.Options.Options.Create(new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success });
        var transcription = new MockTranscriptionProvider(options);
        var tts = new MockTtsProvider(options);
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var projectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var runId = Guid.Parse("33333333-3333-3333-333333333333");

        var timer = Stopwatch.StartNew();
        var completed = 0;
        var failed = 0;
        var iteration = 0;
        while (timer.Elapsed < budget && iteration < maxIterations)
        {
            try
            {
                var artifactId = string.Concat("soak-seg-", (iteration % 500).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var transcript = await transcription.TranscribeAsync(
                    new TranscriptionRequest(tenantId, projectId, runId, artifactId, "en", 1024, 2000, "wav", true, false), CancellationToken.None).ConfigureAwait(true);
                var synth = await tts.SynthesizeAsync(
                    new TtsRequest(tenantId, projectId, runId, transcript.Text, "es", "voice-a", 2000, false), CancellationToken.None).ConfigureAwait(true);
                var first = MockTtsProvider.GenerateAudioBytes(transcript.Text, "es", "voice-a");
                var second = MockTtsProvider.GenerateAudioBytes(transcript.Text, "es", "voice-a");
                if (!first.SequenceEqual(second) || !string.Equals(synth.VoiceId, "voice-a", StringComparison.Ordinal))
                {
                    failed++;
                }
                else
                {
                    completed++;
                }
            }
#pragma warning disable CA1031 // Soak must record (not throw on) per-iteration failures.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed++;
                _output.WriteLine($"Soak iteration {iteration} failed: {ex.Message}");
            }

            iteration++;
            if (iteration % 1000 == 0)
            {
                _output.WriteLine($"Soak progress: {iteration} iterations, {completed} ok, {failed} failed, {timer.Elapsed.TotalMinutes:F1}min elapsed.");
            }
        }

        timer.Stop();
        _output.WriteLine($"Soak finished: {completed} ok, {failed} failed in {timer.Elapsed}.");
        Assert.Equal(0, failed);
        Assert.True(completed > 0, "Soak must complete at least one iteration.");
    }
}
