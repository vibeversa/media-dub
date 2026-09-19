using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Conversation context-window tuning. Binds to the <c>Context</c> section.
/// <c>MaxTokens</c> bounds the estimated tokens (chars/4) of one window's
/// persisted text; <c>MaxSegmentsPerWindow</c> bounds its member count;
/// <c>UseLlmSummary</c> (default false) appends a translation-provider LLM
/// summary section to each window (deterministic concatenation when false,
/// so the default path has no provider cost or failure domain);
/// <c>SummaryTemplateId</c>/<c>SummaryTemplateVersion</c> identify the prompt
/// template recorded on the <c>ProviderExecution</c> row when LLM
/// summarization is used.
/// </summary>
public sealed class ContextOptions
{
    public const string SectionName = "Context";

    [Range(100, 100000)]
    public int MaxTokens { get; set; } = 2000;

    [Range(1, 50)]
    public int MaxSegmentsPerWindow { get; set; } = 5;

    public bool UseLlmSummary { get; set; }

    [Required]
    [MinLength(1)]
    [MaxLength(128)]
    public string SummaryTemplateId { get; set; } = "context-window-summary";

    [Range(1, 1000)]
    public int SummaryTemplateVersion { get; set; } = 1;
}

/// <summary>
/// Fail-fast startup validation for <see cref="ContextOptions"/>.
/// </summary>
public sealed class ContextOptionsValidator : IValidateOptions<ContextOptions>
{
    public ValidateOptionsResult Validate(string? name, ContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxTokens < 100 || options.MaxTokens > 100000)
        {
            return ValidateOptionsResult.Fail($"{nameof(ContextOptions)}.{nameof(ContextOptions.MaxTokens)} must be in 100..100000.");
        }

        if (options.MaxSegmentsPerWindow < 1 || options.MaxSegmentsPerWindow > 50)
        {
            return ValidateOptionsResult.Fail($"{nameof(ContextOptions)}.{nameof(ContextOptions.MaxSegmentsPerWindow)} must be in 1..50.");
        }

        if (string.IsNullOrWhiteSpace(options.SummaryTemplateId))
        {
            return ValidateOptionsResult.Fail($"{nameof(ContextOptions)}.{nameof(ContextOptions.SummaryTemplateId)} must not be empty.");
        }

        if (options.SummaryTemplateVersion < 1 || options.SummaryTemplateVersion > 1000)
        {
            return ValidateOptionsResult.Fail($"{nameof(ContextOptions)}.{nameof(ContextOptions.SummaryTemplateVersion)} must be in 1..1000.");
        }

        return ValidateOptionsResult.Success;
    }
}
