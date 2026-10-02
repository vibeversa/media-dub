using System.Globalization;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.Application.Exceptions;

/// <summary>
/// A cost budget is exhausted at the processing-start preflight. Maps to 429
/// <c>QUOTA_EXCEEDED</c> and carries the figures behind the refusal
/// (estimate, current spend, cap, projected total, dimension) in the error
/// <c>details</c> so the UI can show budget-vs-estimate instead of a bare
/// boolean (GAP-025). Amounts are USD; no media, text, or secrets.
/// </summary>
public sealed class CostBudgetExceededException : AppException, IErrorDetailsProvider
{
    /// <summary>Creates the refusal from the preflight figures.</summary>
    public CostBudgetExceededException(CostPreflightEstimate preflight)
        : base(ErrorCodes.QuotaExceeded, BuildMessage(preflight))
    {
        ArgumentNullException.ThrowIfNull(preflight);
        Preflight = preflight;
    }

    /// <summary>The figures behind the refusal.</summary>
    public CostPreflightEstimate Preflight { get; }

    /// <inheritdoc />
    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> GetErrorDetails() => Preflight.ToDetails();

    private static string BuildMessage(CostPreflightEstimate preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Processing preflight estimate {0} USD plus current {1} USD exceeds project cap {2} USD (dimension {3}).",
            preflight.EstimateUsd.ToString("F2", CultureInfo.InvariantCulture),
            preflight.CurrentSpendUsd.ToString("F2", CultureInfo.InvariantCulture),
            preflight.LimitUsd.ToString("F2", CultureInfo.InvariantCulture),
            CostPreflightEstimate.Dimension);
    }
}