using DubbingPlatform.Application.Errors;
using DubbingPlatform.Domain.Enums;
using ReviewApp = global::DubbingPlatform.Application.Reviews;

namespace DubbingPlatform.UnitTests.Reviews;

/// <summary>
/// Hermetic Task 011 coverage: new error codes, permission/action derivation,
/// severity ranking, and mutation sanitization/hashing. Docker-backed
/// <c>ReviewContextTests</c> replays the HTTP paths in CI.
/// </summary>
public sealed class ReviewContextMutationTests
{
    [Fact]
    public void New_Review_Codes_Map()
    {
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ReviewVersionConflict));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.ReviewReasonRequired));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ReviewAlreadyResolved));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ReviewNotResolved));
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.ReviewEditEmpty));
        Assert.Equal(62, ErrorCodes.All.Length);
    }

    [Fact]
    public void Resolve_Edit_Permissions_From_Roles()
    {
        Assert.True(ReviewApp.ReviewContextService.CanResolve(["TenantAdmin"]));
        Assert.True(ReviewApp.ReviewContextService.CanResolve(["Reviewer"]));
        Assert.True(ReviewApp.ReviewContextService.CanResolve(["ProjectEditor"]));
        Assert.False(ReviewApp.ReviewContextService.CanResolve(["ProjectViewer"]));
        Assert.False(ReviewApp.ReviewContextService.CanResolve([]));

        Assert.True(ReviewApp.ReviewContextService.CanEdit(["ProjectEditor"]));
        Assert.True(ReviewApp.ReviewContextService.CanEdit(["TenantAdmin"]));
        Assert.False(ReviewApp.ReviewContextService.CanEdit(["Reviewer"]));
        Assert.False(ReviewApp.ReviewContextService.CanEdit(["ProjectViewer"]));
    }

    [Fact]
    public void Allowed_Actions_Reflect_State_And_Policy()
    {
        Assert.Equal(
            ["resolve", "dismiss", "resolve-with-edit"],
            ReviewApp.ReviewContextService.AllowedActionsFor(ReviewStatus.Open, true));
        Assert.Equal(
            ["reopen"],
            ReviewApp.ReviewContextService.AllowedActionsFor(ReviewStatus.Approved, true));
        Assert.Equal(
            ["reopen"],
            ReviewApp.ReviewContextService.AllowedActionsFor(ReviewStatus.ResolvedWithEdit, true));
        Assert.Empty(ReviewApp.ReviewContextService.AllowedActionsFor(ReviewStatus.Open, false));
        Assert.Empty(ReviewApp.ReviewContextService.AllowedActionsFor(ReviewStatus.Rejected, false));
    }

    [Fact]
    public void Severity_Ranked()
    {
        Assert.Equal("high", ReviewApp.ReviewContextService.RankSeverity("high"));
        Assert.Equal("high", ReviewApp.ReviewContextService.RankSeverity("BLOCKED"));
        Assert.Equal("low", ReviewApp.ReviewContextService.RankSeverity("low"));
        Assert.Equal("medium", ReviewApp.ReviewContextService.RankSeverity(null));
        Assert.Equal("medium", ReviewApp.ReviewContextService.RankSeverity("warning"));
    }

    [Fact]
    public void Reason_Sanitized_And_Truncated()
    {
        Assert.Null(ReviewApp.ReviewMutationService.SanitizeReason(null));
        Assert.Null(ReviewApp.ReviewMutationService.SanitizeReason("   "));
        Assert.Equal("hello", ReviewApp.ReviewMutationService.SanitizeReason("<b>hello</b>"));
        Assert.Equal(500, ReviewApp.ReviewMutationService.SanitizeReason(new string('x', 600))!.Length);
        Assert.Null(ReviewApp.ReviewMutationService.SanitizeEditText("<br/>"));
    }

    [Fact]
    public void Hash_Stable_And_Action_Scoped()
    {
        var review = Guid.NewGuid();
        var a = ReviewApp.ReviewMutationService.HashFor(review, ReviewApp.ReviewMutationAction.Resolve, 0, "ok", null);
        var b = ReviewApp.ReviewMutationService.HashFor(review, ReviewApp.ReviewMutationAction.Resolve, 0, "ok", null);
        var c = ReviewApp.ReviewMutationService.HashFor(review, ReviewApp.ReviewMutationAction.Dismiss, 0, "ok", null);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Audit_Actions_Named()
    {
        Assert.Equal("review.resolve", ReviewApp.ReviewMutationService.AuditActionFor(ReviewApp.ReviewMutationAction.Resolve));
        Assert.Equal("review.dismiss", ReviewApp.ReviewMutationService.AuditActionFor(ReviewApp.ReviewMutationAction.Dismiss));
        Assert.Equal("review.reopen", ReviewApp.ReviewMutationService.AuditActionFor(ReviewApp.ReviewMutationAction.Reopen));
        Assert.Equal("review.resolvewithedit", ReviewApp.ReviewMutationService.AuditActionFor(ReviewApp.ReviewMutationAction.ResolveWithEdit));
    }

    [Fact]
    public void Conflict_Carries_Refresh_Payload()
    {
        var version = new ReviewApp.ReviewVersionConflictException(3);
        Assert.Equal(ErrorCodes.ReviewVersionConflict, version.ErrorCode);
        Assert.Equal(409, version.StatusCode);
        Assert.Equal(3, version.GetErrorDetails()["currentVersion"]);

        var resolved = new ReviewApp.ReviewAlreadyResolvedException("Approved");
        Assert.Equal(ErrorCodes.ReviewAlreadyResolved, resolved.ErrorCode);
        Assert.Equal("Approved", resolved.GetErrorDetails()["currentStatus"]);

        var open = new ReviewApp.ReviewNotResolvedException("Open");
        Assert.Equal(ErrorCodes.ReviewNotResolved, open.ErrorCode);
        Assert.Equal(409, open.StatusCode);
    }
}
