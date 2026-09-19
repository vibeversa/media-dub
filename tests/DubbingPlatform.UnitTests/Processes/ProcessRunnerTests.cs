using DubbingPlatform.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DubbingPlatform.UnitTests.Processes;

/// <summary>
/// Verifies argument-safe process execution: no shell interpolation, timeout kill,
/// and stdout/stderr capture with truncation flags.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public void Rejects_Shell_Concatenation()
    {
        // Exe paths must not contain shell metacharacters.
        Assert.Throws<ArgumentException>(() => ProcessRunner.RejectIfShellChars("ffmpeg; rm -rf /"));
        Assert.Throws<ArgumentException>(() => ProcessRunner.RejectIfShellChars("ffmpeg|cat"));
        Assert.Throws<ArgumentException>(() => ProcessRunner.RejectIfShellChars("ffmpeg && evil"));

        // Plain exe names pass.
        ProcessRunner.RejectIfShellChars("ffmpeg");
        ProcessRunner.RejectIfShellChars("dotnet");
    }

    [Fact]
    public async Task Shell_Metachars_In_Args_Stay_Literal()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workingDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            // "; rm" is passed via ArgumentList as a literal dotnet arg, never executed.
            // dotnet --version ignores extra args? Use --info with a literal metachar arg:
            // the process must complete without shell interpretation (no exception from RejectIfShellChars).
            var result = await runner.RunAsync(
                "dotnet",
                ["--version", "; rm -rf /"],
                workingDir,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            // Either dotnet ignores the extra arg (exit 0) or rejects it (non-zero),
            // but it must not have executed a shell: stdout must not contain rm output,
            // and the run must not time out.
            Assert.False(result.TimedOut);
            Assert.DoesNotContain("rm:", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }
    }

    [Fact]
    public async Task Captures_Stdout_Stderr()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workingDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            ProcessResult result;
            if (OperatingSystem.IsWindows())
            {
                result = await runner.RunAsync(
                    "cmd.exe",
                    ["/c", "echo out-marker & echo err-marker 1>&2"],
                    workingDir,
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            }
            else
            {
                result = await runner.RunAsync(
                    "/bin/sh",
                    ["-c", "echo out-marker; echo err-marker 1>&2"],
                    workingDir,
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            }

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("out-marker", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("err-marker", result.StdErr, StringComparison.Ordinal);
            Assert.False(result.StdOutTruncated);
            Assert.False(result.StdErrTruncated);
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }
    }

    [Fact]
    public async Task Captures_Stdout_From_Dotnet()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workingDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            var result = await runner.RunAsync(
                "dotnet",
                ["--version"],
                workingDir,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Matches(@"\d+\.\d+", result.StdOut);
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }
    }

    [Fact]
    public async Task Timeout_Kills_Process()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var workingDir = ProcessRunner.CreateTempWorkingDir();
        try
        {
            string exe;
            IReadOnlyList<string> args;
            if (OperatingSystem.IsWindows())
            {
                exe = "ping";
                args = ["-n", "10", "127.0.0.1"];
            }
            else
            {
                exe = "sleep";
                args = ["10"];
            }

            var started = DateTimeOffset.UtcNow;
            var result = await runner.RunAsync(
                exe,
                args,
                workingDir,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            var elapsed = DateTimeOffset.UtcNow - started;

            Assert.True(result.TimedOut);
            Assert.True(elapsed < TimeSpan.FromSeconds(8), $"Kill took too long: {elapsed}");
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }
    }
}
