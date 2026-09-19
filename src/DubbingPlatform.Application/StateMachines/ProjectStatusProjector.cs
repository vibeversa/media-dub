using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.StateMachines;

/// <summary>
/// Projects the authoritative run status onto the project status.
/// Delegates to the domain projection so the mapping has a single source of truth.
/// </summary>
public static class ProjectStatusProjector
{
    public static ProjectStatus ProjectFrom(ProcessingRunStatus runStatus, bool hasOpenRequiredReviews)
    {
        return ProjectStatusProjection.Project(runStatus, hasOpenRequiredReviews);
    }
}
