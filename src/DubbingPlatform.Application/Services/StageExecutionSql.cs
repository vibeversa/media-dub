namespace DubbingPlatform.Application.Services;

/// <summary>
/// Lease-fenced SQL for stage executions. All statements are parameterized
/// (never concatenated) and use snake_case columns matching the
/// <c>stage_executions</c> table. Every predicate includes the lease fencing
/// triple (<c>lease_owner</c>, <c>lease_token</c>, <c>status = 'Running'</c>)
/// so stale workers affect zero rows. Recovery scans
/// <c>status = 'Running' AND lease_expires_at &lt; now</c> via the partial
/// running index plus the composite
/// <c>(processing_run_id, status, lease_expires_at)</c> index.
/// </summary>
public static class StageExecutionSql
{
    /// <summary>
    /// Conditional commit for success. Parameters: {0}=status, {1}=completedAt,
    /// {2}=updatedAt, {3}=outputJson, {4}=id, {5}=leaseOwner, {6}=leaseToken.
    /// </summary>
    public const string CompleteSql =
        "UPDATE stage_executions SET status = {0}, completed_at = {1}, updated_at = {2}, output_artifact_ids_json = {3} " +
        "WHERE id = {4} AND lease_owner = {5} AND lease_token = {6} AND status = 'Running'";

    /// <summary>
    /// Conditional commit for failure. Parameters: {0}=status, {1}=completedAt,
    /// {2}=updatedAt, {3}=errorCode, {4}=errorMessage, {5}=id, {6}=leaseOwner, {7}=leaseToken.
    /// </summary>
    public const string FailSql =
        "UPDATE stage_executions SET status = {0}, completed_at = {1}, updated_at = {2}, error_code = {3}, error_message = {4} " +
        "WHERE id = {5} AND lease_owner = {6} AND lease_token = {7} AND status = 'Running'";

    /// <summary>
    /// Lease renewal. Parameters: {0}=leaseExpiresAt, {1}=updatedAt, {2}=id,
    /// {3}=leaseOwner, {4}=leaseToken. Only extends when the caller still owns
    /// a Running lease.
    /// </summary>
    public const string RenewLeaseSql =
        "UPDATE stage_executions SET lease_expires_at = {0}, updated_at = {1} " +
        "WHERE id = {2} AND lease_owner = {3} AND lease_token = {4} AND status = 'Running'";

    /// <summary>
    /// Lease release. Parameters: {0}=leaseExpiresAt, {1}=updatedAt, {2}=id,
    /// {3}=leaseOwner, {4}=leaseToken. Expires the lease immediately so the
    /// sweeper can re-queue; requires the caller to own a Running lease.
    /// </summary>
    public const string ReleaseLeaseSql =
        "UPDATE stage_executions SET lease_expires_at = {0}, updated_at = {1} " +
        "WHERE id = {2} AND lease_owner = {3} AND lease_token = {4} AND status = 'Running'";

    /// <summary>
    /// Expired-lease takeover. Parameters: {0}=status, {1}=leaseOwner,
    /// {2}=leaseToken, {3}=leaseExpiresAt, {4}=updatedAt, {5}=id,
    /// {6}=oldLeaseOwner, {7}=oldLeaseToken, {8}=now. Only a Running or
    /// RetryPending row whose lease already expired is taken over, fenced on
    /// the previous owner+token so concurrent claimants serialize and exactly
    /// one wins; the token rotation keeps the dead worker fenced out of
    /// future commits. Active leases are never stolen.
    /// </summary>
    public const string TakeoverSql =
        "UPDATE stage_executions SET status = {0}, lease_owner = {1}, lease_token = {2}, lease_expires_at = {3}, updated_at = {4} " +
        "WHERE id = {5} AND lease_owner = {6} AND lease_token = {7} AND status IN ('Running', 'RetryPending') AND lease_expires_at < {8}";

    /// <summary>
    /// Stale recovery marker. Parameters: {0}=now, {1}=retryPendingStatus,
    /// {2}=updatedAt. Matches only Running rows whose lease expired, using the
    /// partial Running index plus the composite
    /// (processing_run_id, status, lease_expires_at) index. Attempt increment
    /// and retry bounding happen in
    /// <c>StageExecutionService.RecoverStaleAsync</c> per row to respect
    /// per-stage <c>RetryOptions</c> overrides.
    /// </summary>
    public const string RecoverStaleSql =
        "UPDATE stage_executions SET status = {1}, updated_at = {2} " +
        "WHERE status = 'Running' AND lease_expires_at < {0}";
}
