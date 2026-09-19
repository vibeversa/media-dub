using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Messaging;

/// <summary>
/// Verifies lease-fenced SQL contains the fencing triple so stale workers
/// affect zero rows.
/// </summary>
public sealed class LeaseSqlTests
{
    [Fact]
    public void Complete_Sql_Contains_Lease_Predicates()
    {
        Assert.Contains("lease_owner", StageExecutionSql.CompleteSql, StringComparison.Ordinal);
        Assert.Contains("lease_token", StageExecutionSql.CompleteSql, StringComparison.Ordinal);
        Assert.Contains("status", StageExecutionSql.CompleteSql, StringComparison.Ordinal);
        Assert.Contains("stage_executions", StageExecutionSql.CompleteSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_Sql_Contains_Lease_Predicates()
    {
        Assert.Contains("lease_owner", StageExecutionSql.FailSql, StringComparison.Ordinal);
        Assert.Contains("lease_token", StageExecutionSql.FailSql, StringComparison.Ordinal);
        Assert.Contains("status", StageExecutionSql.FailSql, StringComparison.Ordinal);
        Assert.Contains("stage_executions", StageExecutionSql.FailSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Renew_Sql_Contains_Lease_Predicates()
    {
        Assert.Contains("lease_owner", StageExecutionSql.RenewLeaseSql, StringComparison.Ordinal);
        Assert.Contains("lease_token", StageExecutionSql.RenewLeaseSql, StringComparison.Ordinal);
        Assert.Contains("status", StageExecutionSql.RenewLeaseSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_Sql_Contains_Lease_Predicates()
    {
        Assert.Contains("lease_owner", StageExecutionSql.ReleaseLeaseSql, StringComparison.Ordinal);
        Assert.Contains("lease_token", StageExecutionSql.ReleaseLeaseSql, StringComparison.Ordinal);
        Assert.Contains("status", StageExecutionSql.ReleaseLeaseSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Recover_Sql_Scans_Running_Expired_Leases()
    {
        Assert.Contains("status", StageExecutionSql.RecoverStaleSql, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at", StageExecutionSql.RecoverStaleSql, StringComparison.Ordinal);
        Assert.Contains("Running", StageExecutionSql.RecoverStaleSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Takeover_Sql_Fences_On_Old_Owner_Token_And_Expiry()
    {
        Assert.Contains("lease_owner", StageExecutionSql.TakeoverSql, StringComparison.Ordinal);
        Assert.Contains("lease_token", StageExecutionSql.TakeoverSql, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at", StageExecutionSql.TakeoverSql, StringComparison.Ordinal);
        Assert.Contains("RetryPending", StageExecutionSql.TakeoverSql, StringComparison.Ordinal);
        Assert.Contains("stage_executions", StageExecutionSql.TakeoverSql, StringComparison.Ordinal);
    }
}
