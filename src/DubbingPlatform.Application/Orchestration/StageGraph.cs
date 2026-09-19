using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Application.Orchestration;

/// <summary>
/// Static metadata for one pipeline stage: its identity, scope, fan-out, and policies.
/// Prerequisites name upstream stages via <see cref="StageType"/> names
/// (e.g. <c>"Diarization"</c>); see <see cref="StageGraph"/> for the full DAG.
/// </summary>
public sealed record StageNode(
    StageType StageType,
    ScopeType Scope,
    string[] Prerequisites,
    string FanOutRule,
    string CompletionCriterion,
    string FailureAggregation,
    string SkipPolicy,
    string ReviewPolicy,
    string RetryPolicy,
    bool IsOnDemand);

/// <summary>
/// The scoped processing DAG. Linear backbone
/// MediaValidation→MediaAnalysis→AudioPreparation→SourceSeparation→Vad→SegmentBuild→Diarization→Transcription→ContextBuild→Translation,
/// with VoiceAssignment branching after Diarization, VoiceGeneration joining
/// Translation+VoiceAssignment, then TimingOptimization→TimelineAssembly→AudioMixing→QualityControl→Render.
/// A stage is schedulable only when every prerequisite is barrier-complete
/// (completed-or-skipped units covering all expected units).
/// Scopes: project-level stages run once per run; VoiceAssignment fans out
/// per speaker; ContextBuild per window; Transcription, Translation,
/// VoiceGeneration, and TimingOptimization per segment. Segment-level quality
/// gates are recorded as QualityControl units with segment scope ids.
/// Eligible unit states are Completed/Skipped; terminal states are
/// Completed/Skipped/Failed/ManualReviewRequired/Cancelled.
/// </summary>
public static class StageGraph
{
    public static readonly IReadOnlyList<StageNode> Nodes =
    [
        new(StageType.MediaValidation, ScopeType.Project, [], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.MediaAnalysis, ScopeType.Project, ["MediaValidation"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.AudioPreparation, ScopeType.Project, ["MediaAnalysis"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.SourceSeparation, ScopeType.Project, ["AudioPreparation"], "single", "one-unit-or-skipped", "fail-fast", "skippable-when-disabled", "no-review", "stage-budget", false),
        new(StageType.Vad, ScopeType.Project, ["SourceSeparation"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.SegmentBuild, ScopeType.Project, ["Vad"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.Diarization, ScopeType.Project, ["SegmentBuild"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.Transcription, ScopeType.Segment, ["Diarization"], "per-segment", "all-units-or-review", "fail-fast", "never-skip", "review-on-flag", "stage-budget", false),
        new(StageType.ContextBuild, ScopeType.Window, ["Transcription"], "per-window", "all-units", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.Translation, ScopeType.Segment, ["ContextBuild"], "per-segment", "all-units-or-review", "fail-fast", "never-skip", "review-on-flag", "stage-budget", false),
        new(StageType.VoiceAssignment, ScopeType.Speaker, ["Diarization"], "per-speaker", "all-units", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.VoiceGeneration, ScopeType.Segment, ["Translation", "VoiceAssignment"], "per-segment", "all-units-or-review", "fail-fast", "never-skip", "review-on-flag", "stage-budget", false),
        new(StageType.TimingOptimization, ScopeType.Segment, ["VoiceGeneration"], "per-segment", "all-units", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.TimelineAssembly, ScopeType.Project, ["TimingOptimization"], "single", "all-units", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.AudioMixing, ScopeType.Project, ["TimelineAssembly"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
        new(StageType.QualityControl, ScopeType.Project, ["AudioMixing"], "single", "all-units-or-review", "fail-fast", "never-skip", "review-on-flag", "stage-budget", false),
        new(StageType.Render, ScopeType.Project, ["QualityControl"], "single", "one-unit", "fail-fast", "never-skip", "no-review", "stage-budget", false),
    ];

    private static readonly Dictionary<StageType, StageNode> ByStage =
        Nodes.ToDictionary(n => n.StageType);

    /// <summary>
    /// Gets the node for a stage. Throws <see cref="DomainException"/> for unknown stages.
    /// </summary>
    public static StageNode NodeOf(StageType stage)
    {
        if (!ByStage.TryGetValue(stage, out var node))
        {
            throw new DomainException($"Unknown stage '{stage}'.");
        }

        return node;
    }

    /// <summary>
    /// Gets the stages that directly depend on <paramref name="stage"/>, in pipeline order.
    /// </summary>
    public static IReadOnlyList<StageNode> GetSuccessors(StageType stage)
    {
        var name = stage.ToString();
        return Nodes
            .Where(n => n.Prerequisites.Contains(name, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Gets the prerequisite stage types for <paramref name="stage"/>.
    /// </summary>
    public static IReadOnlyList<StageType> GetPrerequisites(StageType stage)
    {
        return NodeOf(stage).Prerequisites
            .Select(ParseStageName)
            .ToList();
    }

    /// <summary>
    /// Gets the root stages (no prerequisites). Currently only MediaValidation.
    /// </summary>
    public static IReadOnlyList<StageNode> Roots()
    {
        return Nodes.Where(n => n.Prerequisites.Length == 0).ToList();
    }

    /// <summary>
    /// Whether the stage may record Skipped units (SourceSeparation when disabled).
    /// </summary>
    public static bool IsSkippable(StageNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return !string.Equals(node.SkipPolicy, "never-skip", StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates the DAG: every <see cref="StageType"/> appears exactly once, every
    /// prerequisite resolves, and the graph is acyclic. Throws
    /// <see cref="DomainException"/> on violation.
    /// </summary>
    public static void ValidateDag()
    {
        var expected = Enum.GetValues<StageType>();
        if (Nodes.Count != expected.Length)
        {
            throw new DomainException($"StageGraph must define exactly {expected.Length} nodes, found {Nodes.Count}.");
        }

        var seen = new HashSet<StageType>();
        foreach (var node in Nodes)
        {
            if (!seen.Add(node.StageType))
            {
                throw new DomainException($"StageGraph defines stage '{node.StageType}' more than once.");
            }

            foreach (var prerequisite in node.Prerequisites)
            {
                ParseStageName(prerequisite);
            }
        }

        var visiting = new HashSet<StageType>();
        var visited = new HashSet<StageType>();
        foreach (var node in Nodes)
        {
            Visit(node.StageType, visiting, visited);
        }
    }

    private static StageType ParseStageName(string name)
    {
        if (!Enum.TryParse<StageType>(name, ignoreCase: false, out var stage) ||
            !string.Equals(name, stage.ToString(), StringComparison.Ordinal))
        {
            throw new DomainException($"StageGraph references unknown stage '{name}'.");
        }

        return stage;
    }

    private static void Visit(StageType stage, HashSet<StageType> visiting, HashSet<StageType> visited)
    {
        if (visited.Contains(stage))
        {
            return;
        }

        if (!visiting.Add(stage))
        {
            throw new DomainException($"StageGraph contains a cycle at stage '{stage}'.");
        }

        foreach (var prerequisite in GetPrerequisites(stage))
        {
            Visit(prerequisite, visiting, visited);
        }

        visiting.Remove(stage);
        visited.Add(stage);
    }
}
