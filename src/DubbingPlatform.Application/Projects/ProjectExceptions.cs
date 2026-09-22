using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;

namespace DubbingPlatform.Application.Projects;

/// <summary>
/// Project/dashboard failures for Task 007. Each carries a catalogued public
/// code so envelopes stay structured; messages carry ids only, never content.
/// </summary>
public sealed class LanguageImmutableException : AppException
{
    public LanguageImmutableException(string message)
        : base(ErrorCodes.LanguageImmutable, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Processing-settings change blocked by an active run.
/// </summary>
public sealed class SettingsLockedActiveRunException : AppException
{
    public SettingsLockedActiveRunException(string message)
        : base(ErrorCodes.SettingsLockedActiveRun, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Project missing, soft-deleted, or (for <c>prj_</c> ids) cross-tenant.
/// </summary>
public sealed class ProjectNotFoundException : AppException
{
    public ProjectNotFoundException(string message)
        : base(ErrorCodes.ProjectNotFound, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Delete blocked by an active run.
/// </summary>
public sealed class ProjectHasActiveRunException : AppException
{
    public ProjectHasActiveRunException(string message)
        : base(ErrorCodes.ProjectHasActiveRun, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Optimistic-concurrency mismatch on <c>SettingsVersion</c>.
/// </summary>
public sealed class SettingsVersionConflictException : AppException
{
    public SettingsVersionConflictException(string message)
        : base(ErrorCodes.SettingsVersionConflict, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
