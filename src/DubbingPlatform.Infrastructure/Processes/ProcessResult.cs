namespace DubbingPlatform.Infrastructure.Processes;

/// <summary>
/// Result of a <see cref="ProcessRunner"/> execution.
/// StdOut/StdErr are captured up to 1 MB each; when truncated the corresponding
/// <c>StdOutTruncated</c>/<c>StdErrTruncated</c> flag is set and the captured
/// text ends with a <c>[TRUNCATED]</c> marker.
/// </summary>
public sealed record ProcessResult
{
    /// <summary>Process exit code, or -1 when the process never started.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Captured standard output (truncated at 1 MB).</summary>
    public required string StdOut { get; init; }

    /// <summary>Captured standard error (truncated at 1 MB).</summary>
    public required string StdErr { get; init; }

    /// <summary>True when the run hit <c>timeout</c> and was killed.</summary>
    public required bool TimedOut { get; init; }

    /// <summary>Wall-clock duration of the run.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Executable that was started (never shell-interpolated).</summary>
    public required string Exe { get; init; }

    /// <summary>Arguments passed via <c>ArgumentList</c> (literals, never shell).</summary>
    public required IReadOnlyList<string> Args { get; init; }

    /// <summary>True when StdOut was truncated at the 1 MB cap.</summary>
    public required bool StdOutTruncated { get; init; }

    /// <summary>True when StdErr was truncated at the 1 MB cap.</summary>
    public required bool StdErrTruncated { get; init; }
}

/// <summary>
/// Resource metadata recorded with process-execution logs.
/// </summary>
public sealed record ResourceLimits
{
    /// <summary>Logical CPU threads available to the host.</summary>
    public required int CpuThreads { get; init; }

    /// <summary>Total available memory in bytes (GC view, -1 when unknown).</summary>
    public required long MemoryBytes { get; init; }
}
