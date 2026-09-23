using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Processing;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.Workspace;

namespace DubbingPlatform.UnitTests.Workspace;

/// <summary>
/// Hermetic Task 008 coverage: idempotency hashes, workspace permissions, and
/// progress empty-stage semantics. Docker-backed <c>WorkspaceProgressTests</c>
/// replays the HTTP paths in CI.
/// </summary>
public sealed class WorkspaceShellTests
{
    [Fact]
    public void Idempotency_Hash_Deterministic_And_Sensitive()
    {
        var project = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var run = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var a = ProcessingIdempotency.HashFor(project, "{}", false);
        var b = ProcessingIdempotency.HashFor(project, "{}", false);
        Assert.Equal(a, b);

        Assert.NotEqual(a, ProcessingIdempotency.HashFor(project, "{}", true));
        Assert.NotEqual(a, ProcessingIdempotency.HashFor(project, "{\"x\":1}", false));
        Assert.NotEqual(a, ProcessingIdempotency.HashFor(Guid.NewGuid(), "{}", false));

        var r1 = ProcessingIdempotency.HashForRetry(project, run, string.Empty);
        var r2 = ProcessingIdempotency.HashForRetry(project, run, string.Empty);
        Assert.Equal(r1, r2);
        Assert.NotEqual(r1, ProcessingIdempotency.HashForRetry(project, Guid.NewGuid(), string.Empty));
        Assert.NotEqual(a, r1);
    }

    [Fact]
    public void Idempotency_RequireKey_400_Code()
    {
        foreach (var bad in new string?[] { null, string.Empty, "   " })
        {
            var ex = Assert.Throws<Application.Exceptions.ErrorCodeException>(
                () => ProcessingIdempotency.RequireKey(bad));
            Assert.Equal(ErrorCodes.IdempotencyKeyRequired, ex.ErrorCode);
            Assert.Equal(400, ex.StatusCode);
        }

        Assert.Equal("abc", ProcessingIdempotency.RequireKey("  abc  "));
    }

    [Fact]
    public void Idempotency_Endpoint_And_Expiry_Are_Frozen()
    {
        Assert.Equal("processing", ProcessingIdempotency.Endpoint);
        Assert.Equal(TimeSpan.FromHours(24), ProcessingIdempotency.Expiry);
        Assert.Equal("Idempotent-Replayed", ProcessingIdempotency.ReplayedHeaderName);
    }

    [Fact]
    public void Workspace_Permissions_Mapping()
    {
        Assert.Equal(12, WorkspaceService.AllowedActionsFor(["TenantAdmin"]).Count);
        Assert.Equal(12, WorkspaceService.AllowedActionsFor(["Service"]).Count);

        var owner = WorkspaceService.AllowedActionsFor(["ProjectOwner"]);
        Assert.Contains("processing.start", owner);
        Assert.Contains("processing.cancel", owner);
        Assert.Contains("processing.retry", owner);

        var editor = WorkspaceService.AllowedActionsFor(["ProjectEditor"]);
        Assert.Contains("processing.start", editor);
        Assert.Contains("processing.retry", editor);
        Assert.DoesNotContain("processing.cancel", editor);

        var viewer = WorkspaceService.AllowedActionsFor(["ProjectViewer"]);
        Assert.Contains("project.view", viewer);
        Assert.DoesNotContain("processing.start", viewer);

        Assert.Empty(WorkspaceService.AllowedActionsFor([]));
        Assert.Equal(12, WorkspaceService.QueryCeiling);
    }

    [Fact]
    public void Progress_Empty_Summaries_Yields_Null_Stage()
    {
        Assert.Null(ProgressService.DeriveCurrentStage([]));
        Assert.Equal(0, ProgressService.ComputePercentageIndicator(0, 0));
    }

    [Fact]
    public void New_Processing_Error_Codes_Map()
    {
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.IdempotencyKeyRequired));
        Assert.Equal(422, ErrorCodes.StatusFor(ErrorCodes.IdempotencyKeyReused));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ProjectArchived));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.RunAlreadyTerminal));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.RunAlreadyActive));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ConfigChangedSinceRun));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.SelectionConflict));
        Assert.Equal(404, ErrorCodes.StatusFor(ErrorCodes.VersionNotFound));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.VersionSegmentMismatch));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.SegmentTextEmpty));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.SegmentRetryActive));
        Assert.Equal(422, ErrorCodes.StatusFor(ErrorCodes.VoiceIncompatible));
        Assert.Equal(403, ErrorCodes.StatusFor(ErrorCodes.VoiceConsentRequired));
        Assert.Equal(429, ErrorCodes.StatusFor(ErrorCodes.PreviewQuotaExceeded));
        Assert.Equal(404, ErrorCodes.StatusFor(ErrorCodes.VoiceNotFound));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.PreviewTextInvalid));
        Assert.Equal(57, ErrorCodes.All.Length);
    }
}
