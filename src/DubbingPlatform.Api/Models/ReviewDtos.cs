namespace DubbingPlatform.Api.Models;

/// <summary>
/// Hardened review mutation body: <c>{ expectedVersion, reason, editText? }</c>.
/// <c>ExpectedVersion</c> must equal the current decision-count version shown
/// by the context endpoint (stale → 409 <c>REVIEW_VERSION_CONFLICT</c> with the
/// current version); <c>reason</c> is required, max 500, sanitized (HTML
/// stripped); <c>editText</c> is resolve-with-edit only (empty → 400
/// <c>REVIEW_EDIT_EMPTY</c>). Requires the <c>Idempotency-Key</c> header —
/// replays return the original result with <c>Idempotent-Replayed: true</c>.
/// Example: <c>{ expectedVersion: 0, reason: "confirmed with client" }</c>.
/// </summary>
public sealed class ReviewMutationRequest
{
    public int ExpectedVersion { get; set; }

    public string? Reason { get; set; }

    public string? EditText { get; set; }
}

/// <summary>
/// Hardened review mutation response: the transitioned status, the new
/// decision-count version, and the linked manual version for resolve-with-edit.
/// </summary>
public sealed record ReviewMutationResponse(
    string ReviewId,
    string Status,
    int Version,
    string? ManualVersionId,
    string? VersionKind);
