using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DubbingPlatform.Infrastructure.Health;

/// <summary>
/// FFmpeg readiness probe for media workers. Runs <c>ffmpeg -version</c> via
/// <see cref="ProcessRunner"/> (ArgumentList, no shell). Unhealthy with a message
/// when FFmpeg is missing or exits non-zero; liveness stays healthy. Tagged
/// <c>ready</c> only and registered only when
/// <c>DOTNET_WORKER_ROLE</c> is a media role.
/// </summary>
public sealed class FfmpegVersionCheck : IHealthCheck
{
    public const string Name = "ffmpeg";

    private readonly ProcessRunner _runner;

    public FfmpegVersionCheck(ProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string workingDir;
        try
        {
            workingDir = ProcessRunner.CreateTempWorkingDir();
        }
        catch (IOException exception)
        {
            return HealthCheckResult.Unhealthy($"FFmpeg check cannot create temp dir: {exception.Message}");
        }

        try
        {
            var result = await _runner.RunAsync(
                "ffmpeg",
                ["-version"],
                workingDir,
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                return HealthCheckResult.Unhealthy("FFmpeg version check timed out.");
            }

            if (result.ExitCode != 0)
            {
                return HealthCheckResult.Unhealthy($"FFmpeg is missing or failed (exit {result.ExitCode}).");
            }

            return HealthCheckResult.Healthy("FFmpeg is available.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy($"FFmpeg is missing: {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                Directory.Delete(workingDir, recursive: true);
            }
            catch (Exception)
            {
                // Best effort cleanup.
            }
        }
    }
}
