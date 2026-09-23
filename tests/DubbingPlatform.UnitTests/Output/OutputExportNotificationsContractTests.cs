using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exports;

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
}
