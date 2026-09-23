using DubbingPlatform.Application.Activity;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exports;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Application.Output;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Output;

/// <summary>
/// Hermetic Task 012 coverage: new error codes, export profile allowlist,
/// and role-matrix entries. Docker-backed suites replay the HTTP paths in CI.
/// </summary>
public sealed class OutputExportNotificationsContractTests
{
    [Fact]
    public void New_Output_Codes_Map()
    {
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ExportIncomplete));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.OutputIncomplete));
        Assert.Equal(410, ErrorCodes.StatusFor(ErrorCodes.UrlExpired));
        Assert.Equal(409, ErrorCodes.StatusFor(ErrorCodes.ExportNotReady));
        Assert.Equal(65, ErrorCodes.All.Length);
    }

    [Fact]
    public void Export_Profile_Allowlisted()
    {
        Assert.Null(ExportProfileValidator.Normalize(null));
        Assert.Null(ExportProfileValidator.Normalize("   "));
        Assert.Equal("default", ExportProfileValidator.Normalize("Default"));
        Assert.Equal("broadcast-hd", ExportProfileValidator.Normalize("broadcast-hd"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => ExportProfileValidator.Normalize("../etc/passwd"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => ExportProfileValidator.Normalize("a/b"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => ExportProfileValidator.Normalize("a\\b"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => ExportProfileValidator.Normalize("has space!"));
        Assert.Throws<global::DubbingPlatform.Domain.Exceptions.DomainException>(
            () => ExportProfileValidator.Normalize(new string('x', 65)));
    }

    [Fact]
    public void Role_Matrix_Covers_Output_Exports_Notifications()
    {
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/output", ["ProjectViewer"]));
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/exports", ["ProjectViewer"]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/exports", ["ProjectEditor"]));
        Assert.False(RoleMatrix.IsAllowed("POST /api/v1/exports", ["ProjectViewer"]));
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/notifications", ["ProjectViewer"]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/notifications", ["Reviewer"]));
    }

    [Fact]
    public void Export_Formats_Allowlisted()
    {
        Assert.True(ExportFormatParser.TryParse("srt", out _));
        Assert.True(ExportFormatParser.TryParse("webvtt", out _));
        Assert.True(ExportFormatParser.TryParse("json-timeline", out _));
        Assert.False(ExportFormatParser.TryParse("../../etc/passwd", out _));
        Assert.False(ExportFormatParser.TryParse("../srt", out _));
    }

    [Fact]
    public void Export_Idempotency_Retention_7d()
    {
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.Export);
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.ExpiryFor("POST /api/v1/projects/abc/exports"));
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.ExpiryFor("POST /api/v1/exports"));
    }

    [Fact]
    public void Output_GenerationState_Aliases_State()
    {
        var completeness = new OutputCompletenessDto(96, 100);
        var entry = new OutputAssetEntryDto("ready", "ready", "https://example.test/x", [], null);
        Assert.Equal(entry.State, entry.GenerationState);
        var qc = new OutputQcDto("partial", "partial", "summary", null, ["SEGMENT_PENDING"]);
        Assert.Equal(qc.State, qc.GenerationState);
        var response = new OutputResponse(
            "Ready", "Ready", null, completeness, null, null,
            new OutputItemsDto(null, null, [], null, null, null, null, qc),
            [], DateTimeOffset.UtcNow);
        Assert.Equal(response.State, response.GenerationState);
    }

    [Fact]
    public void Export_Completion_Mappers_Carry_Ids_Only()
    {
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var export = Guid.NewGuid();
        var source = Guid.NewGuid();
        var completed = NotificationEventMapper.FromExport(tenant, project, export, "srt", true, source);
        Assert.Equal(global::DubbingPlatform.Domain.Enums.NotificationType.ExportCompleted, completed.Type);
        Assert.Equal(export.ToString("N"), completed.ResourceId);
        var failed = NotificationEventMapper.FromExport(tenant, project, export, "srt", false, source);
        Assert.Equal(global::DubbingPlatform.Domain.Enums.NotificationType.ExportFailed, failed.Type);
        var activityOk = ActivityEventMapper.FromExportCompleted(
            tenant, project, Guid.NewGuid(), export, "srt", true, "corr-1", DateTimeOffset.UtcNow);
        Assert.Equal(global::DubbingPlatform.Domain.Enums.ActivityType.ExportCompleted, activityOk.Type);
        var activityFail = ActivityEventMapper.FromExportCompleted(
            tenant, project, Guid.NewGuid(), export, "srt", false, "corr-1", DateTimeOffset.UtcNow);
        Assert.Equal(global::DubbingPlatform.Domain.Enums.ActivityType.ExportFailed, activityFail.Type);
        Assert.DoesNotContain("https://", completed.Body, StringComparison.OrdinalIgnoreCase);
    }
}
