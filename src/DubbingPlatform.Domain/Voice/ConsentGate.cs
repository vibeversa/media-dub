using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Domain.Voice;

/// <summary>
/// Public consent state surfaced to voice UIs (Task 037, R5).
/// <c>Unknown</c> covers missing rows and incomplete evidence; both fail
/// closed like <c>Revoked</c> and <c>Expired</c>.
/// </summary>
public enum ConsentState
{
    Unknown,
    Granted,
    Revoked,
    Expired,
}

/// <summary>
/// Gate decision for one cloned-voice use.
/// <c>Allowed</c> proceeds; <c>Blocked</c> rejects new synthesis/preview with
/// <c>CONSENT_REQUIRED</c>; <c>AllowDrain</c> lets an already-running job
/// finish while its result is discarded and the user is notified.
/// </summary>
public enum ConsentDecision
{
    Allowed,
    Blocked,
    AllowDrain,
}

/// <summary>
/// Pure domain consent gate (Task 037, R5). Voice features are
/// disabled-by-default: a missing record, incomplete evidence, non-granted
/// status, revoked stamp, scope mismatch, voice mismatch, or expiry all block
/// new use. Revocation takes effect immediately because every assignment and
/// preview re-validates live; in-flight jobs drain via
/// <see cref="DecideInflight"/> (finish, discard the result, notify the user).
/// Every decision is auditable via <see cref="AuditEvent"/> (ids and outcome
/// only — never subject identity or evidence content).
/// Delegates scope/evidence semantics to the application
/// <c>ConsentService.ValidateForVoice</c> contract so domain and service agree.
/// </summary>
public static class ConsentGate
{
    /// <summary>
    /// Resolves the public <see cref="ConsentState"/> for a record. Pure.
    /// Null records and incomplete evidence map to <c>Unknown</c>;
    /// <c>RevokedAt</c> or <c>Revoked</c> maps to <c>Revoked</c>;
    /// <c>Expired</c> maps to <c>Expired</c> (treated as revoked downstream);
    /// otherwise <c>Granted</c>.
    /// </summary>
    public static ConsentState ResolveState(ConsentRecord? record)
    {
        if (record is null)
        {
            return ConsentState.Unknown;
        }

        if (string.IsNullOrWhiteSpace(record.SubjectIdentity)
            || string.IsNullOrWhiteSpace(record.EvidenceReference)
            || string.IsNullOrWhiteSpace(record.Scope)
            || string.IsNullOrWhiteSpace(record.Jurisdiction))
        {
            return ConsentState.Unknown;
        }

        if (record.Status == ConsentStatus.Revoked || record.RevokedAt.HasValue)
        {
            return ConsentState.Revoked;
        }

        if (record.Status == ConsentStatus.Expired)
        {
            return ConsentState.Expired;
        }

        return record.Status == ConsentStatus.Granted ? ConsentState.Granted : ConsentState.Unknown;
    }

    /// <summary>
    /// Whether the state permits new synthesis/preview use. Pure.
    /// Only <c>Granted</c> allows; every other state blocks (fail closed).
    /// </summary>
    public static bool IsUsable(ConsentState state)
    {
        return state == ConsentState.Granted;
    }

    /// <summary>
    /// Evaluates one new cloned-voice use. Pure. Returns
    /// <c>Allowed</c> only when the record is granted, covers
    /// <paramref name="projectId"/>, and (when bound) matches
    /// <paramref name="voiceProfileId"/>; otherwise <c>Blocked</c>.
    /// Mirrors <c>ConsentService.ValidateForVoice</c> without throwing so
    /// callers can audit the outcome before mapping to
    /// <c>CONSENT_REQUIRED</c>.
    /// </summary>
    public static ConsentDecision Evaluate(
        ConsentRecord? record,
        Guid projectId,
        Guid? voiceProfileId)
    {
        if (projectId == Guid.Empty)
        {
            return ConsentDecision.Blocked;
        }

        if (voiceProfileId.HasValue && voiceProfileId.Value == Guid.Empty)
        {
            return ConsentDecision.Blocked;
        }

        if (record is null)
        {
            return ConsentDecision.Blocked;
        }

        if (ResolveState(record) != ConsentState.Granted)
        {
            return ConsentDecision.Blocked;
        }

        if (!ScopeCoversProject(record.Scope, projectId))
        {
            return ConsentDecision.Blocked;
        }

        if (voiceProfileId.HasValue
            && record.VoiceProfileId.HasValue
            && record.VoiceProfileId.Value != Guid.Empty
            && record.VoiceProfileId.Value != voiceProfileId.Value)
        {
            return ConsentDecision.Blocked;
        }

        return ConsentDecision.Allowed;
    }

    /// <summary>
    /// Decides the fate of an in-flight preview/synthesis job after the
    /// consent changed mid-run. Pure. A still-granted consent lets the job
    /// complete normally (<c>Allowed</c>); any other state drains:
    /// the job finishes but its result is discarded and the user is notified
    /// (<c>AllowDrain</c>). In-flight work is never left running unbounded —
    /// the drain is terminal for the artifact.
    /// </summary>
    public static ConsentDecision DecideInflight(ConsentRecord? record, Guid projectId, Guid? voiceProfileId)
    {
        var decision = Evaluate(record, projectId, voiceProfileId);
        return decision == ConsentDecision.Allowed ? ConsentDecision.Allowed : ConsentDecision.AllowDrain;
    }

    /// <summary>
    /// Builds the redacted audit payload for one gate decision. Pure.
    /// Contains ids, state, decision, and scope coverage only — never subject
    /// identity, evidence content, or media.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> AuditEvent(
        Guid tenantId,
        Guid projectId,
        Guid? consentId,
        ConsentState state,
        ConsentDecision decision,
        string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenantId"] = tenantId.ToString("N"),
            ["projectId"] = projectId.ToString("N"),
            ["consentId"] = consentId?.ToString("N"),
            ["state"] = state.ToString(),
            ["decision"] = decision.ToString(),
            ["endpoint"] = endpoint.Trim(),
        };
    }

    private static bool ScopeCoversProject(string? scope, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        var trimmed = scope.Trim();
        if (string.Equals(trimmed, "*", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.Contains(projectId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(projectId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }
}
