// GAP-015: the retention sweeper deletes Committed content objects directly,
// so the state machine must allow Committed -> Deleted (empty-ref + retention
// satisfied) without passing through Orphaned.
using DubbingPlatform.Application.Services;
using DubbingPlatform.Application.StateMachines;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.StateMachines;

public sealed class ContentObjectStateMachineTests
{
    [Theory]
    [InlineData(ContentObjectStatus.Pending, ContentObjectStatus.Committed)]
    [InlineData(ContentObjectStatus.Committed, ContentObjectStatus.Orphaned)]
    [InlineData(ContentObjectStatus.Orphaned, ContentObjectStatus.Deleted)]
    [InlineData(ContentObjectStatus.Committed, ContentObjectStatus.Deleted)]
    public void Legal_Transitions_Are_Allowed(ContentObjectStatus from, ContentObjectStatus to)
    {
        Assert.True(ContentObjectStateMachine.CanTransition(from, to));
        ContentObjectStateMachine.EnsureCanTransition(from, to);
    }

    [Fact]
    public void Same_State_Transitions_Are_Idempotent()
    {
        foreach (var status in Enum.GetValues<ContentObjectStatus>())
        {
            Assert.True(ContentObjectStateMachine.CanTransition(status, status));
            ContentObjectStateMachine.EnsureCanTransition(status, status);
        }
    }

    [Theory]
    [InlineData(ContentObjectStatus.Pending, ContentObjectStatus.Orphaned)]
    [InlineData(ContentObjectStatus.Pending, ContentObjectStatus.Deleted)]
    [InlineData(ContentObjectStatus.Orphaned, ContentObjectStatus.Committed)]
    [InlineData(ContentObjectStatus.Deleted, ContentObjectStatus.Committed)]
    [InlineData(ContentObjectStatus.Orphaned, ContentObjectStatus.Pending)]
    public void Illegal_Transitions_Throw(ContentObjectStatus from, ContentObjectStatus to)
    {
        Assert.False(ContentObjectStateMachine.CanTransition(from, to));
        Assert.Throws<DomainException>(() => ContentObjectStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void Sweeper_Delete_Path_Is_Representable()
    {
        // RetentionService.SweepAsync selects Committed rows past the final
        // retention window and marks them Deleted; the state machine must
        // accept that path or the sweeper throws on its legal path.
        Assert.True(ContentObjectStateMachine.CanTransition(ContentObjectStatus.Committed, ContentObjectStatus.Deleted));
    }

    [Fact]
    public void Physically_Delete_Gate_Still_Requires_Dereference_And_Retention()
    {
        // Live references block deletion regardless of the new fast path.
        Assert.False(RetentionService.CanPhysicallyDelete(1, retentionExpired: true, hasActiveHold: false));
        Assert.False(RetentionService.CanPhysicallyDelete(0, retentionExpired: false, hasActiveHold: false));
        Assert.False(RetentionService.CanPhysicallyDelete(0, retentionExpired: true, hasActiveHold: true));
        Assert.True(RetentionService.CanPhysicallyDelete(0, retentionExpired: true, hasActiveHold: false));
    }
}