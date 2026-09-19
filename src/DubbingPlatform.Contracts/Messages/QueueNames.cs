namespace DubbingPlatform.Contracts.Messages;

/// <summary>
/// Frozen queue taxonomy. Queues are partitioned by workload class, not by
/// micro-stage. Unsupported schema versions are routed to
/// <see cref="Skipped"/>; poison messages go to <see cref="Error"/>.
/// These names are frozen: renaming a queue is a breaking change.
/// </summary>
public static class QueueNames
{
    /// <summary>Orchestration and saga control messages.</summary>
    public const string ControlOrchestration = "control.orchestration";

    /// <summary>CPU media preparation work.</summary>
    public const string MediaPreparation = "media.preparation";

    /// <summary>Media rendering work.</summary>
    public const string MediaRender = "media.render";

    /// <summary>General AI provider work.</summary>
    public const string AiProvider = "ai.provider";

    /// <summary>GPU-backed AI provider work.</summary>
    public const string AiGpu = "ai.gpu";

    /// <summary>On-demand export jobs.</summary>
    public const string Export = "export";

    /// <summary>Retention, deletion, and reconciliation work.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>Messages skipped due to unsupported schema versions.</summary>
    public const string Skipped = "_skipped";

    /// <summary>Poison messages exhausted by retries.</summary>
    public const string Error = "_error";
}
