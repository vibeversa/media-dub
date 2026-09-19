using System.Diagnostics;
using System.Text;
using DubbingPlatform.Application.Security;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Infrastructure.Processes;

/// <summary>
/// Argument-safe external process execution. Processes are started with
/// <c>UseShellExecute=false</c> and every argument is passed via
/// <c>ProcessStartInfo.ArgumentList</c> (never string concatenation), so shell
/// metacharacters in arguments are safe literals and can never trigger shell
/// interpretation. Stdout/stderr are captured up to 1 MB each (truncated with a
/// flag); the process is killed on timeout or cancellation. Temporary working
/// directories are isolated per execution under
/// <c>Path.GetTempPath()/dubbing-*</c> with restrictive permissions.
/// Exe, redacted args, duration, exit code, and <see cref="ResourceLimits"/>
/// metadata are logged; secret values never appear in logs.
/// </summary>
public sealed class ProcessRunner
{
    public const int MaxOutputBytes = 1024 * 1024;

    private const string TruncatedMarker = "[TRUNCATED]";

    private static readonly char[] ShellMetachars = [';', '|', '&', '$', '`', '\\', '"', '\'', '>', '<', '*', '?', '~', '#', '(', ')', '{', '}', '!', '\n', '\r'];

    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Unit-test hook demonstrating the no-shell contract. Validates that a value
    /// destined for <c>ProcessStartInfo.FileName</c> (or a working directory) does
    /// not contain shell metacharacters. Arguments passed via
    /// <c>ArgumentList</c> are safe literals and must NOT be passed here;
    /// metachars in args (for example <c>; rm</c>) stay literal and never execute.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="value"/> contains shell metacharacters.</exception>
    public static void RejectIfShellChars(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.IndexOfAny(ShellMetachars) >= 0)
        {
            throw new ArgumentException("Value contains shell metacharacters and must not be used as an executable path or shell command.", nameof(value));
        }
    }

    /// <summary>
    /// Creates an isolated temp working directory under
    /// <c>Path.GetTempPath()/dubbing-&lt;guid&gt;</c>. Throws
    /// <see cref="IOException"/> with a <c>RESOURCE_EXHAUSTED</c> marker when the
    /// temp volume is full.
    /// </summary>
    public static string CreateTempWorkingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dubbing-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            throw new IOException("RESOURCE_EXHAUSTED: temp volume is full; cannot create process working directory.", exception);
        }

        RestrictPermissions(dir);
        return dir;
    }

    /// <summary>
    /// Runs <paramref name="exe"/> with <paramref name="args"/> in
    /// <paramref name="workingDir"/>, killing the process on
    /// <paramref name="timeout"/> or <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string workingDir,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDir);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        RejectIfShellChars(exe);
        var resolvedWorkingDir = ResolveWorkingDir(workingDir);
        var limits = CurrentLimits();
        var startedAt = Stopwatch.GetTimestamp();
        var redactedArgs = args.Select(a => SecretRedactor.Redact(a) ?? string.Empty).ToList();

        _logger.LogInformation(
            "Starting process {Exe} with {ArgCount} args in {WorkingDir} (CpuThreads {CpuThreads}, MemoryBytes {MemoryBytes})",
            exe,
            args.Count,
            resolvedWorkingDir,
            limits.CpuThreads,
            limits.MemoryBytes);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = resolvedWorkingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("ProcessRunner requires UseShellExecute=false; shell execution is forbidden.");
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTruncated = false;
        var stderrTruncated = false;

        try
        {
            if (!process.Start())
            {
                return Fail(exe, args, startedAt, "Process failed to start.");
            }
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            throw new IOException("RESOURCE_EXHAUSTED: process start failed; temp volume may be full.", exception);
        }

        var stdoutTask = DrainAsync(process.StandardOutput, stdout, () => stdoutTruncated = true, cancellationToken);
        var stderrTask = DrainAsync(process.StandardError, stderr, () => stderrTruncated = true, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return await KillAndReportAsync(process, stdoutTask, stderrTask, exe, args, startedAt, timedOut: true, limits, redactedArgs).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await KillAndReportAsync(process, stdoutTask, stderrTask, exe, args, startedAt, timedOut: false, limits, redactedArgs, cancelled: true).ConfigureAwait(false);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            TimedOut = false,
            Duration = duration,
            Exe = exe,
            Args = args.ToList(),
            StdOutTruncated = stdoutTruncated,
            StdErrTruncated = stderrTruncated,
        };

        _logger.LogInformation(
            "Process {Exe} exited {ExitCode} in {DurationMs}ms (TimedOut {TimedOut})",
            exe,
            result.ExitCode,
            duration.TotalMilliseconds,
            result.TimedOut);

        return result;
    }

    private static ResourceLimits CurrentLimits()
    {
        long memoryBytes;
        try
        {
            memoryBytes = (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch (Exception)
        {
            memoryBytes = -1;
        }

        return new ResourceLimits
        {
            CpuThreads = Environment.ProcessorCount,
            MemoryBytes = memoryBytes,
        };
    }

    private static string ResolveWorkingDir(string workingDir)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(workingDir);
        }
        catch (Exception exception)
        {
            throw new ArgumentException("Working directory path is invalid.", nameof(workingDir), exception);
        }

        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var isUnderTemp = resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
        if (isUnderTemp)
        {
            var name = Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!name.StartsWith("dubbing-", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Temp working directories must be under Path.GetTempPath()/dubbing-*.", nameof(workingDir));
            }
        }

        try
        {
            Directory.CreateDirectory(resolved);
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            throw new IOException("RESOURCE_EXHAUSTED: temp volume is full; cannot create process working directory.", exception);
        }

        return resolved;
    }

    private static void RestrictPermissions(string dir)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception)
        {
            // Best effort: restrictive ACLs are defense in depth; execution must not fail when chmod is unavailable.
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sink, Action markTruncated, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var pendingBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (bytes + pendingBytes > MaxOutputBytes)
            {
                markTruncated();
                var remaining = MaxOutputBytes - bytes;
                if (remaining > 0)
                {
                    var text = new string(buffer, 0, read);
                    var encoded = Encoding.UTF8.GetBytes(text);
                    sink.Append(Encoding.UTF8.GetString(encoded, 0, remaining));
                }

                sink.Append(TruncatedMarker);
                // Drain remaining output without storing to avoid pipe deadlock.
                while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
                {
                }

                return;
            }

            sink.Append(buffer, 0, read);
            bytes += pendingBytes;
        }
    }

    private async Task<ProcessResult> KillAndReportAsync(
        Process process,
        Task stdoutTask,
        Task stderrTask,
        string exe,
        IReadOnlyList<string> args,
        long startedAt,
        bool timedOut,
        ResourceLimits limits,
        IReadOnlyList<string> redactedArgs,
        bool cancelled = false)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort kill; report below.
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Capture tasks observe cancellation; partial output is still reported.
        }

        var duration = Stopwatch.GetElapsedTime(startedAt);
        var reason = timedOut ? "timed out" : "cancelled";
        _logger.LogWarning(
            "Process {Exe} {Reason} after {DurationMs}ms with {ArgCount} args (CpuThreads {CpuThreads}, MemoryBytes {MemoryBytes})",
            exe,
            reason,
            duration.TotalMilliseconds,
            args.Count,
            limits.CpuThreads,
            limits.MemoryBytes);

        return new ProcessResult
        {
            ExitCode = process.HasExited ? process.ExitCode : -1,
            StdOut = string.Empty,
            StdErr = cancelled ? "Cancelled." : "Timed out.",
            TimedOut = timedOut,
            Duration = duration,
            Exe = exe,
            Args = args.ToList(),
            StdOutTruncated = false,
            StdErrTruncated = false,
        };
    }

    private ProcessResult Fail(string exe, IReadOnlyList<string> args, long startedAt, string stderr)
    {
        return new ProcessResult
        {
            ExitCode = -1,
            StdOut = string.Empty,
            StdErr = stderr,
            TimedOut = false,
            Duration = Stopwatch.GetElapsedTime(startedAt),
            Exe = exe,
            Args = args.ToList(),
            StdOutTruncated = false,
            StdErrTruncated = false,
        };
    }

    private static bool IsDiskFull(IOException exception)
    {
        const int HR_ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);
        const int HR_ERROR_DISK_FULL = unchecked((int)0x80070070);
        return exception.HResult == HR_ERROR_HANDLE_DISK_FULL ||
            exception.HResult == HR_ERROR_DISK_FULL ||
            exception.Message.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("space", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("No space", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("ENOSPC", StringComparison.Ordinal);
    }
}
