using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DubbingPlatform.Application.Abstractions;
using DubbingPlatform.Application.Abstractions.Providers;
using DubbingPlatform.Application.Abstractions.Providers.Dtos;
using DubbingPlatform.Application.Configuration;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Providers;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// One transcribed segment eligible for context windowing.
/// </summary>
public sealed record ContextSegmentInput(
    Guid SegmentId,
    int Sequence,
    string SpeakerDisplay,
    string Text,
    Guid TranscriptArtifactId,
    int DurationMs);

/// <summary>
/// One deterministic non-overlapping window partition. <see cref="Split"/> is
/// true when the boundary before this window was caused by token overflow
/// (count-bound breaks leave it false); <see cref="Overflow"/> is true when a
/// single segment plus header already exceeds the token budget (emitted whole,
/// never truncated).
/// </summary>
public sealed record PlannedWindow(
    int Sequence,
    IReadOnlyList<ContextSegmentInput> Members,
    string ContextText,
    string ContextHash,
    int TokenCount,
    bool Split,
    bool Overflow);

/// <summary>
/// One persisted window: its stable row plus its reusable artifact.
/// <see cref="ReusedArtifact"/> is true when an identical artifact
/// (same sequence and hash) already existed and no new bytes were published.
/// </summary>
public sealed record BuiltWindow(
    Guid WindowId,
    int Sequence,
    Guid ArtifactId,
    string ContextHash,
    int TokenCount,
    int SegmentCount,
    bool ReusedArtifact);

/// <summary>
/// Outcome of a full window build. <see cref="SkippedSegments"/> counts run
/// segments without a selected (non-empty) transcript; they receive no window
/// assignment (translation for those segments is the translation stage's
/// concern).
/// </summary>
public sealed record ContextBuildResult(
    IReadOnlyList<BuiltWindow> Windows,
    int SkippedSegments);

/// <summary>
/// Deterministic LLM summary prompt for one window. Hashes cover only the
/// template text plus window context (never credentials); the template
/// identity is recorded on the <c>ProviderExecution</c> row.
/// </summary>
public sealed record SummaryPrompt(
    string TemplateId,
    string TemplateVersion,
    string SystemText,
    string PromptText,
    string PromptHash,
    string SystemHash);

/// <summary>
/// Project-settings subset for context building. Only the allow-listed keys
/// <c>glossary</c> (<c>{term: translation}</c> object), <c>style</c> (string),
/// and <c>targetLanguage</c> (string override) are read, so secrets stored
/// under any other settings key never enter context text or hashes. Invalid
/// JSON fails closed to all defaults.
/// </summary>
public sealed record ContextSettings(
    SortedDictionary<string, string> Glossary,
    string Style,
    string? TargetOverride);

/// <summary>
/// Reusable deterministic conversation context windows at window granularity.
/// Default path (no LLM) is pure concatenation: segments ordered by
/// <c>Sequence</c> are packed greedily into non-overlapping partitions bounded
/// by <c>Context:MaxSegmentsPerWindow</c> and <c>Context:MaxTokens</c> (token
/// estimate is <c>chars/4</c> on the exact persisted text, so the bound holds
/// without drift). Window text is the glossary/style header plus
/// <c>[seq speaker: text]</c> lines joined with invariant <c>\n</c> (no
/// timestamps in the hash input); <c>ContextHash</c> is SHA-256 hex, so the
/// same transcript versions plus config always yield the same hash. One
/// <c>ContextWindow</c> artifact (schema <c>v1</c>, parents are the member
/// transcript artifacts) is persisted per window and shared by all its member
/// segments via <c>SegmentContextAssignment</c> (many-to-one; overlapping
/// windows are intentionally not built — non-overlapping partitions keep unit
/// identity, barrier accounting, and translation lookups deterministic).
/// Token overflow splits windows (recorded per window as <c>split</c>) and a
/// single over-budget segment is emitted whole with <c>overflow</c> (never
/// truncated, silently or otherwise). Rebuilds are idempotent on the
/// <c>(tenant, run, sequence)</c> unique index with deterministic window and
/// assignment ids: same hash reuses rows and artifacts, changed hashes update
/// rows in place (stable ids keep barrier scope ids valid) and publish a new
/// artifact row while old artifact rows remain as immutable history; stale
/// assignments are reconciled, windows and artifacts are never deleted. Empty
/// input (no selected transcripts) yields zero windows without failing.
/// When <c>Context:UseLlmSummary</c> is true, each window core is summarized
/// via the translation provider (capability <c>Translation</c>, Mock adapter
/// only) and the summary is appended as a <c># summary:</c> section; the
/// template id/version plus prompt and system hashes are recorded on the
/// <c>ProviderExecution</c> row and in artifact metadata (there is no
/// <c>SystemHash</c> column on that row, so the system hash lives in artifact
/// metadata and in the request-hash input). LLM failures propagate (fail fast)
/// — operators opting into summarization accept its failure domain.
/// </summary>
public sealed class ContextBuilderService
{
    /// <summary>Artifact/row schema version for every context window payload.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Speaker display used when a segment has no mapped speaker.</summary>
    public const string UnknownSpeaker = "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ArtifactService _artifacts;
    private readonly BarrierService _barrier;
    private readonly ITranslationProvider _translation;
    private readonly ProviderResolver _resolver;
    private readonly ProviderExecutionRecorder _recorder;
    private readonly IProviderCostGate _costGate;
    private readonly ContextOptions _options;
    private readonly ILogger<ContextBuilderService> _logger;

    public ContextBuilderService(
        IStageExecutionContextFactory contextFactory,
        ArtifactService artifacts,
        BarrierService barrier,
        ITranslationProvider translation,
        ProviderResolver resolver,
        ProviderExecutionRecorder recorder,
        IProviderCostGate costGate,
        IOptions<ContextOptions> contextOptions,
        ILogger<ContextBuilderService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(barrier);
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(costGate);
        ArgumentNullException.ThrowIfNull(contextOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _artifacts = artifacts;
        _barrier = barrier;
        _translation = translation;
        _resolver = resolver;
        _recorder = recorder;
        _costGate = costGate;
        _options = contextOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Estimates tokens as <c>chars/4</c> (integer floor). Pure.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return text.Length / 4;
    }

    /// <summary>
    /// SHA-256 hex (lowercase) of the UTF-8 bytes. Pure; same input always
    /// yields the same hash on any machine.
    /// </summary>
    public static string ComputeHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Deterministic window id for one sequence within a run. Never passes
    /// secrets (run id and sequence only).
    /// </summary>
    public static Guid WindowIdFor(Guid runId, int sequence)
    {
        if (runId == Guid.Empty)
        {
            throw new DomainException("RunId must not be empty.");
        }

        if (sequence < 0)
        {
            throw new DomainException("Sequence must be >= 0.");
        }

        return GuidUtility.From(string.Concat(
            runId.ToString("N"), ":contextwindow:",
            sequence.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Deterministic assignment id for one segment-to-window link.
    /// </summary>
    public static Guid AssignmentIdFor(Guid segmentId, Guid windowId)
    {
        if (segmentId == Guid.Empty)
        {
            throw new DomainException("SegmentId must not be empty.");
        }

        if (windowId == Guid.Empty)
        {
            throw new DomainException("WindowId must not be empty.");
        }

        return GuidUtility.From(string.Concat(
            "ctxassign:", segmentId.ToString("N"), ":",
            windowId.ToString("N")));
    }

    /// <summary>
    /// Builds one <c>[seq speaker: text]</c> line. Pure; text is embedded
    /// verbatim (no escaping) so the transform is exactly reversible.
    /// </summary>
    public static string BuildSegmentLine(int sequence, string speakerDisplay, string text)
    {
        if (sequence < 0)
        {
            throw new DomainException("Sequence must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(speakerDisplay))
        {
            throw new DomainException("SpeakerDisplay must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(text);

        return string.Concat(
            "[", sequence.ToString(CultureInfo.InvariantCulture), " ",
            speakerDisplay.Trim(), ": ", text, "]");
    }

    /// <summary>
    /// Builds the deterministic header (<c># target</c>, optional
    /// <c># style</c>, optional ordinally-sorted <c># glossary</c>). Pure.
    /// </summary>
    public static string BuildHeader(
        string targetLanguage,
        SortedDictionary<string, string> glossary,
        string style)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new DomainException("TargetLanguage must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(glossary);

        var builder = new StringBuilder();
        builder.Append("# target: ").Append(targetLanguage.Trim());
        if (!string.IsNullOrWhiteSpace(style))
        {
            builder.Append('\n').Append("# style: ").Append(style.Trim());
        }

        if (glossary.Count > 0)
        {
            builder.Append('\n').Append("# glossary:");
            foreach (var pair in glossary)
            {
                builder.Append('\n').Append("# - ").Append(pair.Key).Append(" => ").Append(pair.Value);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses the allow-listed context keys from project settings JSON.
    /// Pure; invalid JSON or shapes fail closed to defaults.
    /// </summary>
    public static ContextSettings ParseSettings(string? settingsJson)
    {
        var empty = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new ContextSettings(empty, string.Empty, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return new ContextSettings(empty, string.Empty, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new ContextSettings(empty, string.Empty, null);
            }

            var glossary = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "glossary", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var term in property.Value.EnumerateObject())
                    {
                        var key = term.Name.Trim();
                        if (key.Length == 0
                            || term.Value.ValueKind is not JsonValueKind.String
                            || string.IsNullOrWhiteSpace(term.Value.GetString()))
                        {
                            continue;
                        }

                        glossary[key] = term.Value.GetString()!.Trim();
                    }
                }
            }

            return new ContextSettings(
                glossary,
                GetString(document.RootElement, "style")?.Trim() ?? string.Empty,
                GetString(document.RootElement, "targetLanguage")?.Trim() is { Length: > 0 } target
                    ? target
                    : null);
        }
    }

    /// <summary>
    /// Pure deterministic partitioning: sorts by <c>Sequence</c>, packs
    /// greedily into non-overlapping windows bounded by
    /// <paramref name="maxSegmentsPerWindow"/> and <paramref name="maxTokens"/>
    /// (exact whole-text estimate, no flooring drift). Segments with empty
    /// text are skipped deterministically. Empty input yields zero windows.
    /// </summary>
    public static IReadOnlyList<PlannedWindow> Plan(
        IReadOnlyList<ContextSegmentInput> inputs,
        string targetLanguage,
        SortedDictionary<string, string> glossary,
        string style,
        int maxTokens,
        int maxSegmentsPerWindow)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new DomainException("TargetLanguage must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(glossary);
        if (maxTokens < 1)
        {
            throw new DomainException("MaxTokens must be >= 1.");
        }

        if (maxSegmentsPerWindow < 1)
        {
            throw new DomainException("MaxSegmentsPerWindow must be >= 1.");
        }

        var header = BuildHeader(targetLanguage, glossary, style ?? string.Empty);
        var ordered = inputs
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Text))
            .OrderBy(i => i.Sequence)
            .ToList();
        var windows = new List<PlannedWindow>();
        var current = new List<ContextSegmentInput>();
        var currentChars = header.Length;
        var currentSplit = false;

        void Close()
        {
            if (current.Count == 0)
            {
                return;
            }

            var text = string.Concat(header, "\n", string.Join("\n", current.Select(m => BuildSegmentLine(m.Sequence, m.SpeakerDisplay, m.Text))));
            var tokens = EstimateTokens(text);
            windows.Add(new PlannedWindow(
                windows.Count,
                current.ToList(),
                text,
                ComputeHash(text),
                tokens,
                Split: currentSplit,
                Overflow: current.Count == 1 && tokens > maxTokens));
            current.Clear();
            currentChars = header.Length;
            currentSplit = false;
        }

        foreach (var input in ordered)
        {
            var lineChars = BuildSegmentLine(input.Sequence, input.SpeakerDisplay, input.Text).Length + 1;
            var overCount = current.Count + 1 > maxSegmentsPerWindow;
            var overTokens = (currentChars + lineChars) / 4 > maxTokens;
            if (current.Count > 0 && (overCount || overTokens))
            {
                Close();
                currentSplit = overTokens;
            }

            current.Add(input);
            currentChars += lineChars;
        }

        Close();
        return windows;
    }

    /// <summary>
    /// Builds the deterministic LLM summary prompt for one window core text.
    /// Pure; the prompt carries only template text plus window context.
    /// </summary>
    public static SummaryPrompt BuildSummaryPrompt(
        string coreText,
        string sourceLanguage,
        string targetLanguage,
        string templateId,
        int templateVersion,
        int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(coreText))
        {
            throw new DomainException("CoreText must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            throw new DomainException("SourceLanguage must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new DomainException("TargetLanguage must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new DomainException("TemplateId must not be empty.");
        }

        if (templateVersion < 1)
        {
            throw new DomainException("TemplateVersion must be >= 1.");
        }

        var systemText = string.Concat(
            "Summarize the conversation context for dubbing translation from ",
            sourceLanguage.Trim(), " to ", targetLanguage.Trim(),
            " in at most ", maxTokens.ToString(CultureInfo.InvariantCulture),
            " tokens. Keep named entities and glossary terms verbatim. Return plain text only.");
        var promptText = string.Concat(systemText, "\n\n# context\n", coreText);
        return new SummaryPrompt(
            templateId.Trim(),
            templateVersion.ToString(CultureInfo.InvariantCulture),
            systemText,
            promptText,
            ComputeHash(promptText),
            ComputeHash(systemText));
    }

    /// <summary>
    /// Builds (or idempotently rebuilds) every context window for a run:
    /// loads segments with selected transcripts plus speakers, partitions,
    /// optionally summarizes via LLM, publishes one reusable artifact per
    /// window, upserts rows and assignments, and initializes the
    /// <c>ContextBuild</c> barrier. See class docs for idempotency and
    /// overflow semantics.
    /// </summary>
    public async Task<ContextBuildResult> BuildWindowsAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));

        var project = await LoadOwnedProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var run = await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);
        var loaded = await LoadInputsAsync(tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);

        var settings = ParseSettings(project.SettingsJson);
        var target = settings.TargetOverride ?? project.TargetLanguage;
        var planned = Plan(
            loaded.Members, target, settings.Glossary, settings.Style,
            _options.MaxTokens, _options.MaxSegmentsPerWindow);

        if (planned.Count == 0)
        {
            await _barrier.EnsureSummaryAsync(tenantId, runId, StageType.ContextBuild, 0, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Context build for run {RunId}: no selected transcripts ({Skipped} segments skipped); zero windows.",
                runId, loaded.Skipped);
            return new ContextBuildResult([], loaded.Skipped);
        }

        var finals = await FinalizeWindowsAsync(
            tenantId, project, runId, planned, target, settings, cancellationToken).ConfigureAwait(false);
        var built = await PublishAndPersistAsync(
            tenantId, projectId, runId, run, project.SourceLanguage, finals, target, settings, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Context build for run {RunId}: {Windows} windows from {Segments} segments ({Skipped} skipped).",
            runId, built.Count, loaded.Members.Count, loaded.Skipped);
        return new ContextBuildResult(built, loaded.Skipped);
    }

    /// <summary>
    /// Fast path for window-scoped workers: resolves <paramref name="scopeId"/>
    /// (window id in <c>N</c>/<c>D</c> form, or a window sequence number) to
    /// its row plus reusable artifact id. Returns null when the window (or its
    /// artifact) does not exist yet and a full build is required.
    /// </summary>
    public async Task<(ContextWindow Window, Guid ArtifactId)?> TryGetWindowAsync(
        Guid tenantId,
        Guid runId,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(runId, nameof(runId));
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return null;
        }

        var trimmed = scopeId.Trim();
        ContextWindow? window = null;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            if (TryParseWindowId(trimmed, out var windowId))
            {
                window = await db.Set<ContextWindow>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(w => w.Id == windowId, cancellationToken).ConfigureAwait(false);
            }
            else if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence >= 0)
            {
                window = await db.Set<ContextWindow>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(w => w.RunId == runId && w.Sequence == sequence, cancellationToken).ConfigureAwait(false);
            }

            if (window is null || window.TenantId != tenantId || window.RunId != runId)
            {
                return null;
            }

            var artifactId = await FindArtifactAsync(db, tenantId, runId, window.Sequence, window.ContextHash, cancellationToken).ConfigureAwait(false);
            if (artifactId is null)
            {
                return null;
            }

            return (window, artifactId.Value);
        }
    }

    private sealed record LoadedInputs(IReadOnlyList<ContextSegmentInput> Members, int Skipped);

    private sealed record FinalWindow(
        PlannedWindow Planned,
        string Text,
        string Hash,
        int Tokens,
        bool OverBudget,
        string? Summary,
        SummaryPrompt? Prompt,
        ProviderType? Provider,
        string? Model);

    private async Task<List<FinalWindow>> FinalizeWindowsAsync(
        Guid tenantId,
        DubbingProject project,
        Guid runId,
        IReadOnlyList<PlannedWindow> planned,
        string target,
        ContextSettings settings,
        CancellationToken cancellationToken)
    {
        var finals = new List<FinalWindow>(planned.Count);
        if (!_options.UseLlmSummary)
        {
            foreach (var window in planned)
            {
                finals.Add(new FinalWindow(
                    window, window.ContextText, window.ContextHash, window.TokenCount,
                    window.TokenCount > _options.MaxTokens, null, null, null, null));
            }

            return finals;
        }

        if (!await _costGate.CanProceedAsync(tenantId, ProviderCapability.Translation, cancellationToken).ConfigureAwait(false))
        {
            throw new ErrorCodeException(ErrorCodes.QuotaExceeded, "Cost guard blocks context summarization for the current tenant.");
        }

        var totalBytes = (long)Encoding.UTF8.GetByteCount(string.Concat(planned.Select(w => w.ContextText)));
        var totalDuration = planned.SelectMany(w => w.Members).Sum(m => Math.Max(0, m.DurationMs));
        ProviderType provider;
        string model;
        try
        {
            (provider, model) = await _resolver.ResolveAsync(
                ProviderCapability.Translation,
                tenantId,
                project.SourceLanguage,
                totalBytes,
                totalDuration,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Resolver failures are permanent provider-configuration failures with no summary to persist.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new ErrorCodeException(ErrorCodes.ProviderConfigurationError, $"No summarization route is available: {Truncate(ClassifyMessage(ex))}.");
        }

        if (provider != ProviderType.Mock)
        {
            throw new ErrorCodeException(
                ErrorCodes.ProviderConfigurationError,
                string.Concat("No summarization adapter for provider '", provider.ToString(), "'."));
        }

        foreach (var window in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt = BuildSummaryPrompt(
                window.ContextText, project.SourceLanguage, target,
                _options.SummaryTemplateId, _options.SummaryTemplateVersion, _options.MaxTokens);
            var windowId = WindowIdFor(runId, window.Sequence);
            var summary = await SummarizeOnceAsync(
                tenantId, project, runId, window, windowId, prompt,
                provider, model, target, cancellationToken).ConfigureAwait(false);
            var text = string.Concat(window.ContextText, "\n# summary: ", summary);
            var tokens = EstimateTokens(text);
            finals.Add(new FinalWindow(
                window, text, ComputeHash(text), tokens,
                tokens > _options.MaxTokens || window.Overflow, summary, prompt, provider, model));
        }

        return finals;
    }

    private async Task<string> SummarizeOnceAsync(
        Guid tenantId,
        DubbingProject project,
        Guid runId,
        PlannedWindow window,
        Guid windowId,
        SummaryPrompt prompt,
        ProviderType provider,
        string model,
        string target,
        CancellationToken cancellationToken)
    {
        var targetLanguage = target;
        var request = new TranslationRequest(
            tenantId, project.Id, runId,
            windowId.ToString("N"),
            project.SourceLanguage,
            targetLanguage,
            Encoding.UTF8.GetByteCount(prompt.PromptText),
            window.Members.Sum(m => Math.Max(0, m.DurationMs)));
        var requestHash = ConfigurationHashCalculator.Compute(new
        {
            windowId = windowId.ToString("N"),
            prompt = prompt.PromptText,
            systemHash = prompt.SystemHash,
            source = project.SourceLanguage,
            target = targetLanguage,
            templateId = prompt.TemplateId,
            templateVersion = prompt.TemplateVersion,
        });

        var stopwatch = Stopwatch.StartNew();
        TranslationResponse response;
        try
        {
            response = await _translation.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LeaseLostException)
        {
            throw;
        }
#pragma warning disable CA1031 // Provider-failure taxonomy: transport errors propagate for transport retry, coded errors are recorded then rethrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            stopwatch.Stop();
            if (IsTransport(ex))
            {
                throw;
            }

            await RecordSummaryExecutionAsync(
                tenantId, project.Id, runId, provider, model, prompt,
                requestHash, responseHash: null, latencyMs: stopwatch.ElapsedMilliseconds,
                usage: null, outcome: MapOutcome(ex), cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (string.IsNullOrWhiteSpace(response.PrimaryText))
        {
            var responseHash = ConfigurationHashCalculator.Compute(new
            {
                text = response.PrimaryText,
                model = response.Model,
            });
            await RecordSummaryExecutionAsync(
                tenantId, project.Id, runId, provider, model, prompt,
                requestHash, responseHash, stopwatch.ElapsedMilliseconds,
                response.Usage, OutcomeClass.ProviderInvalidResponse, cancellationToken).ConfigureAwait(false);
            throw new ErrorCodeException(ErrorCodes.ProviderInvalidResponse, "Summarization provider returned empty text.");
        }

        var hash = ConfigurationHashCalculator.Compute(new
        {
            text = response.PrimaryText,
            model = response.Model,
        });
        await RecordSummaryExecutionAsync(
            tenantId, project.Id, runId, provider, model, prompt,
            requestHash, hash, stopwatch.ElapsedMilliseconds,
            response.Usage, OutcomeClass.Success, cancellationToken).ConfigureAwait(false);
        return response.PrimaryText.Trim();
    }

    private async Task RecordSummaryExecutionAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        ProviderType provider,
        string model,
        SummaryPrompt prompt,
        string requestHash,
        string? responseHash,
        long latencyMs,
        ProviderUsage? usage,
        OutcomeClass outcome,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var hashPrefix = prompt.PromptHash.Length >= 16
            ? prompt.PromptHash.Substring(0, 16)
            : prompt.PromptHash;
        var scope = string.Concat("Window:summary:", hashPrefix);
        var idempotencyKey = ProviderExecutionRecorder.BuildIdempotencyKey(
            runId, nameof(StageType.ContextBuild), scope, 0);
        var row = new ProviderExecution(
            Guid.NewGuid(), tenantId, projectId, runId,
            null, provider, ProviderCapability.Translation, model,
            null, null, null, null,
            0, requestHash, responseHash, Math.Max(0, latencyMs),
            usage?.TokensIn, usage?.TokensOut, usage?.AudioSeconds,
            usage?.EstimatedCostUsd, usage?.EstimatedCostUsd, null,
            outcome, null,
            string.Concat(prompt.TemplateId, ":", prompt.TemplateVersion), prompt.PromptHash,
            null, null, idempotencyKey, now);
        await _recorder.RecordAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<BuiltWindow>> PublishAndPersistAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        ProcessingRun run,
        string sourceLanguage,
        IReadOnlyList<FinalWindow> finals,
        string target,
        ContextSettings settings,
        CancellationToken cancellationToken)
    {
        var existingArtifacts = await LoadArtifactIndexAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        var artifactIds = new Dictionary<int, (Guid ArtifactId, bool Reused)>();
        foreach (var final in finals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existingArtifacts.TryGetValue(final.Planned.Sequence, out var existing)
                && string.Equals(existing.ContextHash, final.Hash, StringComparison.Ordinal))
            {
                artifactIds[final.Planned.Sequence] = (existing.ArtifactId, true);
                continue;
            }

            var payload = BuildArtifactJson(
                runId, final, sourceLanguage, target, settings);
            PublishResult published;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false))
            {
                published = await _artifacts.PublishAsync(
                    tenantId, projectId, runId,
                    StageType.ContextBuild, ArtifactType.ContextWindow,
                    stream, ".json", "application/json",
                    final.Provider?.ToString(), final.Model,
                    run.ConfigurationHash, run.ExecutionSnapshotHash,
                    final.Planned.Members
                        .Select(m => m.TranscriptArtifactId)
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList(),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            using (TenantContext.BeginScope(tenantId))
            {
                using var db = _contextFactory.CreateDbContext();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE artifacts SET metadata_json = {0} WHERE id = {1} AND tenant_id = {2}",
                    payload, published.ArtifactId, tenantId).ConfigureAwait(false);
            }

            artifactIds[final.Planned.Sequence] = (published.ArtifactId, false);
        }

        await EnsureRunActiveAsync(tenantId, runId, projectId, cancellationToken).ConfigureAwait(false);
        var built = await UpsertRowsAsync(
            tenantId, projectId, runId, finals, artifactIds, cancellationToken).ConfigureAwait(false);

        var rowCount = await CountWindowsAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        await _barrier.EnsureSummaryAsync(tenantId, runId, StageType.ContextBuild, rowCount, cancellationToken).ConfigureAwait(false);
        return built;
    }

    private async Task<List<BuiltWindow>> UpsertRowsAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        IReadOnlyList<FinalWindow> finals,
        IReadOnlyDictionary<int, (Guid ArtifactId, bool Reused)> artifactIds,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var final in finals)
                {
                    var windowId = WindowIdFor(runId, final.Planned.Sequence);
                    var existing = await db.Set<ContextWindow>()
                        .FirstOrDefaultAsync(w => w.RunId == runId && w.Sequence == final.Planned.Sequence, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                    {
                        db.Set<ContextWindow>().Add(new ContextWindow(
                            windowId, tenantId, projectId, runId,
                            final.Planned.Sequence, final.Text, final.Hash, final.Tokens, now));
                    }
                    else
                    {
                        if (existing.Id != windowId)
                        {
                            throw new ErrorCodeException(
                                ErrorCodes.PipelineInvariantViolation,
                                $"Context window sequence {final.Planned.Sequence} maps to conflicting ids.");
                        }

                        if (!string.Equals(existing.ContextHash, final.Hash, StringComparison.Ordinal))
                        {
                            existing.Update(final.Text, final.Hash, final.Tokens);
                        }
                    }
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var windowIds = finals.Select(f => WindowIdFor(runId, f.Planned.Sequence)).ToList();
                var desired = finals
                    .SelectMany(f => f.Planned.Members.Select(m => (m.SegmentId, WindowId: WindowIdFor(runId, f.Planned.Sequence))))
                    .ToHashSet();
                var existingAssignments = await db.Set<SegmentContextAssignment>()
                    .Where(a => windowIds.Contains(a.ContextWindowId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var existingKeys = existingAssignments
                    .Select(a => (a.SegmentId, a.ContextWindowId))
                    .ToHashSet();
                foreach (var link in desired)
                {
                    if (!existingKeys.Contains(link))
                    {
                        db.Set<SegmentContextAssignment>().Add(new SegmentContextAssignment(
                            AssignmentIdFor(link.SegmentId, link.WindowId),
                            tenantId, link.SegmentId, link.WindowId, now));
                    }
                }

                var stale = existingAssignments
                    .Where(a => !desired.Contains((a.SegmentId, a.ContextWindowId)))
                    .ToList();
                db.Set<SegmentContextAssignment>().RemoveRange(stale);

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return finals.Select(f =>
                {
                    var artifact = artifactIds[f.Planned.Sequence];
                    return new BuiltWindow(
                        WindowIdFor(runId, f.Planned.Sequence), f.Planned.Sequence,
                        artifact.ArtifactId, f.Hash, f.Tokens,
                        f.Planned.Members.Count, artifact.Reused);
                }).ToList();
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original exception propagates.
                }

                throw;
            }
        }
    }

    private sealed record ArtifactIndexEntry(Guid ArtifactId, string ContextHash);

    private async Task<Dictionary<int, ArtifactIndexEntry>> LoadArtifactIndexAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Set<Artifact>()
                .AsNoTracking()
                .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.ContextWindow)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new { a.Id, a.MetadataJson, a.CreatedAt })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var index = new Dictionary<int, ArtifactIndexEntry>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.MetadataJson))
                {
                    continue;
                }

                if (!TryParseArtifactIdentity(row.MetadataJson, out var sequence, out var hash))
                {
                    continue;
                }

                index[sequence] = new ArtifactIndexEntry(row.Id, hash);
            }

            return index;
        }
    }

    private async Task<Guid?> FindArtifactAsync(
        DbContext db,
        Guid tenantId,
        Guid runId,
        int sequence,
        string contextHash,
        CancellationToken cancellationToken)
    {
        var rows = await db.Set<Artifact>()
            .AsNoTracking()
            .Where(a => a.ProcessingRunId == runId && a.Type == ArtifactType.ContextWindow)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.Id, a.MetadataJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MetadataJson))
            {
                continue;
            }

            if (TryParseArtifactIdentity(row.MetadataJson, out var parsedSequence, out var parsedHash)
                && parsedSequence == sequence
                && string.Equals(parsedHash, contextHash, StringComparison.Ordinal))
            {
                return row.Id;
            }
        }

        return null;
    }

    private static bool TryParseArtifactIdentity(string json, out int sequence, out string hash)
    {
        sequence = -1;
        hash = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sequence", out var sequenceElement)
                || sequenceElement.ValueKind is not JsonValueKind.Number
                || !sequenceElement.TryGetInt32(out sequence)
                || sequence < 0)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("contextHash", out var hashElement)
                || hashElement.ValueKind is not JsonValueKind.String)
            {
                return false;
            }

            hash = hashElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(hash);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildArtifactJson(
        Guid runId,
        FinalWindow final,
        string sourceLanguage,
        string target,
        ContextSettings settings)
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            runId = runId.ToString("N"),
            windowId = WindowIdFor(runId, final.Planned.Sequence).ToString("N"),
            sequence = final.Planned.Sequence,
            sourceLanguage,
            targetLanguage = target,
            contextHash = final.Hash,
            tokenCount = final.Tokens,
            maxTokens = _options.MaxTokens,
            maxSegmentsPerWindow = _options.MaxSegmentsPerWindow,
            split = final.Planned.Split,
            overflow = final.Planned.Overflow,
            overBudget = final.OverBudget,
            useLlmSummary = final.Prompt is not null,
            summaryTemplateId = final.Prompt?.TemplateId,
            summaryTemplateVersion = final.Prompt?.TemplateVersion,
            promptHash = final.Prompt?.PromptHash,
            systemHash = final.Prompt?.SystemHash,
            provider = final.Provider?.ToString(),
            model = final.Model,
            summary = final.Summary,
            segments = final.Planned.Members.Select(m => new
            {
                segmentId = m.SegmentId.ToString("N"),
                sequence = m.Sequence,
                speaker = m.SpeakerDisplay,
                text = m.Text,
                transcriptArtifactId = m.TranscriptArtifactId == Guid.Empty ? null : m.TranscriptArtifactId.ToString("N"),
            }),
            glossary = settings.Glossary.Select(pair => new { term = pair.Key, translation = pair.Value }),
            style = settings.Style,
            text = final.Text,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private async Task<LoadedInputs> LoadInputsAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var segments = await db.Set<SpeechSegment>()
                .AsNoTracking()
                .Where(s => s.RunId == runId)
                .OrderBy(s => s.Sequence)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var segment in segments)
            {
                if (segment.TenantId != tenantId || segment.ProjectId != projectId)
                {
                    throw new ForbiddenException($"Speech segment '{segment.Id:D}' does not belong to the current tenant/project/run.");
                }
            }

            var selected = await db.Set<TranscriptVersion>()
                .AsNoTracking()
                .Where(v => v.RunId == runId && v.IsSelected)
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var transcriptBySegment = new Dictionary<Guid, TranscriptVersion>();
            foreach (var version in selected)
            {
                if (version.TenantId != tenantId || version.ProjectId != projectId)
                {
                    throw new ForbiddenException($"Transcript version '{version.Id:D}' does not belong to the current tenant/project/run.");
                }

                transcriptBySegment.TryAdd(version.SegmentId, version);
            }

            var speakers = await db.Set<Speaker>()
                .AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var displayBySpeaker = speakers
                .Where(s => s.TenantId == tenantId)
                .ToDictionary(s => s.Id, s => s.DisplayName);

            var members = new List<ContextSegmentInput>(segments.Count);
            foreach (var segment in segments)
            {
                if (!transcriptBySegment.TryGetValue(segment.Id, out var transcript)
                    || string.IsNullOrWhiteSpace(transcript.Text))
                {
                    continue;
                }

                var display = segment.SpeakerId.HasValue
                    && displayBySpeaker.TryGetValue(segment.SpeakerId.Value, out var name)
                    && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : UnknownSpeaker;
                members.Add(new ContextSegmentInput(
                    segment.Id, segment.Sequence, display, transcript.Text,
                    transcript.WordTimestampsArtifactId ?? Guid.Empty,
                    segment.DurationMs));
            }

            return new LoadedInputs(members, segments.Count - members.Count);
        }
    }

    private async Task<DubbingProject> LoadOwnedProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    private async Task<ProcessingRun> EnsureRunActiveAsync(Guid tenantId, Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new NotFoundException($"Processing run '{runId}' was not found.");
            }

            if (run.TenantId != tenantId || run.ProjectId != projectId)
            {
                throw new ForbiddenException($"Processing run '{runId}' does not belong to the current tenant/project.");
            }

            if (run.Status is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
            {
                throw new LeaseLostException($"Processing run '{runId}' is '{run.Status}'; aborting before commit.");
            }

            return run;
        }
    }

    private async Task<int> CountWindowsAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ContextWindow>()
                .CountAsync(w => w.RunId == runId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryParseWindowId(string value, out Guid windowId)
    {
        if (Guid.TryParseExact(value, "N", out windowId) && windowId != Guid.Empty)
        {
            return true;
        }

        if (Guid.TryParseExact(value, "D", out windowId) && windowId != Guid.Empty)
        {
            return true;
        }

        windowId = Guid.Empty;
        return false;
    }

    private static bool IsTransport(Exception exception)
    {
        return exception is HttpRequestException or TimeoutException or SocketException or IOException;
    }

    private static OutcomeClass MapOutcome(Exception exception)
    {
        if (exception is ErrorCodeException coded)
        {
            return coded.ErrorCode switch
            {
                ErrorCodes.ProviderRateLimited => OutcomeClass.ProviderRateLimited,
                ErrorCodes.ProviderTimeout => OutcomeClass.ProviderTimeout,
                ErrorCodes.ProviderInvalidResponse => OutcomeClass.ProviderInvalidResponse,
                ErrorCodes.ProviderConfigurationError => OutcomeClass.UnsupportedCapability,
                ErrorCodes.PolicyDenied => OutcomeClass.PolicyRejected,
                ErrorCodes.ProviderQuotaExhausted => OutcomeClass.ProviderUnavailable,
                ErrorCodes.ProviderFailed => OutcomeClass.ProviderTransientFailure,
                _ => OutcomeClass.ProviderPermanentFailure,
            };
        }

        if (exception is DomainException)
        {
            return OutcomeClass.ProviderPermanentFailure;
        }

        return OutcomeClass.ProviderTransientFailure;
    }

    private static string ClassifyMessage(Exception exception)
    {
        if (string.IsNullOrWhiteSpace(exception.Message))
        {
            return "provider error";
        }

        return exception.Message.Trim();
    }

    private static string Truncate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Context build failed.";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
    }

    private static string? GetString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
