using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Projects;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Projects;

/// <summary>
/// Hermetic Task 007 coverage: config-hash determinism, guard active-status
/// set, and list-query validation. Docker-backed <c>ProjectsApiTests</c>
/// replays the HTTP paths in CI.
/// </summary>
public sealed class ProjectGuardHashTests
{
    [Fact]
    public void ConfigHash_Is_Deterministic_And_Sensitive_To_Inputs()
    {
        var a = ProjectConfigHash.Compute("en", "es", "{}", null);
        var b = ProjectConfigHash.Compute("en", "es", "{}", null);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);

        var processing = ProjectConfigHash.Compute("en", "es", "{}", "{\"schemaVersion\":1}");
        Assert.NotEqual(a, processing);

        var otherTarget = ProjectConfigHash.Compute("en", "fr", "{}", null);
        Assert.NotEqual(a, otherTarget);
    }

    [Fact]
    public void Guard_Active_Status_Set_Matches_Quota_Definition()
    {
        Assert.True(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.Pending));
        Assert.True(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.Running));
        Assert.True(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.Cancelling));
        Assert.True(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.ManualReviewRequired));
        Assert.False(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.Completed));
        Assert.False(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.Failed));
        Assert.False(ProjectSettingsGuard.IsActiveStatus(ProcessingRunStatus.Cancelled));
    }

    [Fact]
    public void ListQuery_Validates_Sort_Archived_Owner()
    {
        new ProjectListQuery(null, null, null, null, null, null, 1, 20).Validate();
        new ProjectListQuery(null, null, null, "all", "name", "asc", 1, 20).Validate();

        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(() =>
            new ProjectListQuery(null, null, null, null, "bogus", null, 1, 20).Validate());
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(() =>
            new ProjectListQuery(null, null, null, null, null, "sideways", 1, 20).Validate());
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(() =>
            new ProjectListQuery(null, null, null, "sometimes", null, null, 1, 20).Validate());
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(() =>
            new ProjectListQuery(null, Guid.Empty, null, null, null, null, 1, 20).Validate());
    }

    [Fact]
    public void New_Error_Codes_Map_To_Expected_Status()
    {
        Assert.Equal(400, ErrorCodes.StatusFor(ErrorCodes.LanguageImmutable));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.SettingsLockedActiveRun));
        Assert.Equal(404, ErrorCodes.StatusFor(ErrorCodes.ProjectNotFound));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ProjectHasActiveRun));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.SettingsVersionConflict));
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
        Assert.Equal(52, ErrorCodes.All.Length);
    }
}
