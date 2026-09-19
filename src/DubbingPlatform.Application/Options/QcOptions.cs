using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Quality-control policy. Binds to the <c>Qc</c> section.
/// <c>BlockFailsRun</c> (default false) selects the blocking outcome: false
/// blocks the render, creates a <c>QC_BLOCKED</c> review, and moves the
/// execution to <c>ManualReviewRequired</c> (the saga moves the run to
/// <c>ManualReviewRequired</c>; the run stays awaiting a manual fix); true
/// fails the execution with <c>QC_BLOCKED</c> instead (the saga fail-fast
/// policy then fails the run). <c>TerminologyStrict</c> (default false) is the
/// global default for glossary enforcement; project
/// <c>settings.terminologyStrict</c> overrides it per run when present.
/// <c>VerifyChecksums</c> (default true) re-hashes staged segment audio and
/// compares it to the committed <c>ContentObject</c> hash; mismatches block
/// with <c>ARTIFACT_CHECKSUM_MISMATCH</c>.
/// </summary>
public sealed class QcOptions
{
    public const string SectionName = "Qc";

    public bool BlockFailsRun { get; set; }

    public bool TerminologyStrict { get; set; }

    public bool VerifyChecksums { get; set; } = true;
}

/// <summary>
/// Fail-fast startup validation for <see cref="QcOptions"/>.
/// All members are booleans, so any combination is valid.
/// </summary>
public sealed class QcOptionsValidator : IValidateOptions<QcOptions>
{
    public ValidateOptionsResult Validate(string? name, QcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ValidateOptionsResult.Success;
    }
}
