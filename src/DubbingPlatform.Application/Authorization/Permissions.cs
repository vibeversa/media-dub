namespace DubbingPlatform.Application.Authorization;

/// <summary>
/// Permission-string contract for the frontend shell (navigation, guards,
/// locale-gated UX). These strings are UX hints only — never a security
/// boundary. Every endpoint re-authorizes via <c>[Authorize(Policy=...)]</c>
/// plus the project-ownership resource check; a test asserts 403 when a hint
/// is present but the server policy denies. The set is frozen at these 12
/// names; a contract test asserts set equality.
/// </summary>
public static class Permissions
{
    public const string ProjectView = "project.view";

    public const string ProjectEdit = "project.edit";

    public const string ProjectDelete = "project.delete";

    public const string ProcessingStart = "processing.start";

    public const string ProcessingCancel = "processing.cancel";

    public const string ProcessingRetry = "processing.retry";

    public const string ReviewView = "review.view";

    public const string ReviewResolve = "review.resolve";

    public const string ExportCreate = "export.create";

    public const string ExportDownload = "export.download";

    public const string AdminManage = "admin.manage";

    public const string DiagnosticsView = "diagnostics.view";

    /// <summary>
    /// All 12 permission strings.
    /// </summary>
    public static readonly string[] All =
    [
        ProjectView,
        ProjectEdit,
        ProjectDelete,
        ProcessingStart,
        ProcessingCancel,
        ProcessingRetry,
        ReviewView,
        ReviewResolve,
        ExportCreate,
        ExportDownload,
        AdminManage,
        DiagnosticsView,
    ];

    /// <summary>
    /// Determines whether the given string is a known permission.
    /// </summary>
    public static bool IsKnown(string? permission)
    {
        return !string.IsNullOrEmpty(permission) && All.Contains(permission, StringComparer.Ordinal);
    }
}
