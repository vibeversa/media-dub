using DubbingPlatform.Api.Models;
using DubbingPlatform.Application.Authorization;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Contracts.Messages;

namespace DubbingPlatform.UnitTests.Notifications;

/// <summary>
/// Hermetic Task 012B coverage: payload hygiene (R4), deep-link resource
/// types with documented fallback (R5), read-all dual-name compat (R3), and
/// role-matrix entries. Docker-backed <c>NotificationsApiTests</c> replay the
/// HTTP paths in CI.
/// </summary>
public sealed class NotificationApiContractTests
{
    private static readonly HashSet<string> AllowedResourceTypes = new(StringComparer.Ordinal)
    {
        "ProcessingRun", "ReviewItem", "ExportJob", "MediaAsset", "DubbingProject", "Tenant",
    };

    [Fact]
    public void Sanitize_Strips_Urls_And_Tokens()
    {
        var cleaned = NotificationProjector.Sanitize(
            "See https://example.test/x with bearer abc123-def456 end",
            global::DubbingPlatform.Domain.Entities.Notification.MaxBodyLength);
        Assert.DoesNotContain("https://", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer ", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123-def456", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_Redacts_Urls_And_Tokens_Instead_Of_Storing_Them()
    {
        var url = NotificationProjector.Sanitize("https://example.test/x", 100);
        Assert.DoesNotContain("https://", url, StringComparison.OrdinalIgnoreCase);
        var token = NotificationProjector.Sanitize("bearer abc123", 100);
        Assert.DoesNotContain("abc123", token, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepLink_ResourceTypes_Are_Documented()
    {
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var run = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var inputs = new List<NotificationInput>
        {
            NotificationEventMapper.FromRunCompleted(new RunCompleted(
                Guid.NewGuid(), "corr", tenant, project, run, null, null, null, null, null,
                1, now, 1, null, null, null, run.ToString("N"))),
            NotificationEventMapper.FromRunFailed(new RunFailed(
                Guid.NewGuid(), "corr", tenant, project, run, null, null, null, null, null,
                1, now, 1, null, null, null, "PROVIDER_TIMEOUT", "timed out")),
            NotificationEventMapper.FromReviewRequired(new StageReviewRequired(
                Guid.NewGuid(), "corr", tenant, project, run, null, null, null, null, null,
                1, now, 1, null, null, null, "Translation", "rev-1")),
            NotificationEventMapper.FromReviewResolved(new ReviewResolved(
                Guid.NewGuid(), "corr", tenant, project, run, null, null, null, null, null,
                1, now, 1, null, null, null, "rev-1", "Approved")),
            NotificationEventMapper.FromUploadRejected(new MediaValidated(
                Guid.NewGuid(), "corr", tenant, project, run, null, null, null, null, null,
                1, now, 1, null, null, null, "media-1", false)),
            NotificationEventMapper.FromExport(tenant, project, Guid.NewGuid(), "srt", true, Guid.NewGuid()),
            NotificationEventMapper.FromExport(tenant, project, Guid.NewGuid(), "srt", false, Guid.NewGuid()),
            NotificationEventMapper.FromQuotaWarning(tenant, project, "storage", Guid.NewGuid()),
            NotificationEventMapper.FromQuotaWarning(tenant, null, "storage", Guid.NewGuid()),
            NotificationEventMapper.FromProviderPolicyWarning(tenant, project, "policy", Guid.NewGuid()),
        };

        Assert.NotEmpty(inputs);
        foreach (var input in inputs)
        {
            Assert.Contains(input.ResourceType, AllowedResourceTypes);
            Assert.False(string.IsNullOrWhiteSpace(input.ResourceId));
            // R4: mapper bodies carry short ids/codes only — never URLs or tokens.
            Assert.DoesNotContain("https://", input.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", input.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReadAll_Response_Carries_Both_Names()
    {
        var response = new MarkAllReadResponse(2, 2);
        Assert.Equal(response.MarkedCount, response.Marked);
        var empty = new MarkAllReadResponse(0, 0);
        Assert.Equal(0, empty.Marked);
    }

    [Fact]
    public void Role_Matrix_Covers_Notifications()
    {
        Assert.True(RoleMatrix.IsAllowed("GET /api/v1/notifications", ["ProjectViewer"]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/notifications", ["Reviewer"]));
        Assert.True(RoleMatrix.IsAllowed("POST /api/v1/notifications", ["ProjectEditor"]));
    }
}
