using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.UnitTests.Orchestration;

/// <summary>
/// Offline DAG, queue-routing, and saga-construction checks for Task 12.
/// No database or broker required.
/// </summary>
public sealed class StageGraphTests
{
    [Fact]
    public void Graph_Has_All_17_Stages_Exactly_Once()
    {
        var expected = Enum.GetValues<StageType>();
        Assert.Equal(expected.Length, StageGraph.Nodes.Count);
        Assert.Equal(expected.Length, StageGraph.Nodes.Select(n => n.StageType).Distinct().Count());
        foreach (var stage in expected)
        {
            Assert.Contains(StageGraph.Nodes, n => n.StageType == stage);
        }
    }

    [Fact]
    public void Graph_Validates_Clean()
    {
        var exception = Record.Exception(StageGraph.ValidateDag);
        Assert.Null(exception);
    }

    [Fact]
    public void Scopes_Match_Spec()
    {
        var projectStages = new[]
        {
            StageType.MediaValidation, StageType.MediaAnalysis, StageType.AudioPreparation,
            StageType.SourceSeparation, StageType.Vad, StageType.SegmentBuild,
            StageType.Diarization, StageType.TimelineAssembly, StageType.AudioMixing,
            StageType.QualityControl, StageType.Render,
        };
        foreach (var stage in projectStages)
        {
            Assert.Equal(ScopeType.Project, StageGraph.NodeOf(stage).Scope);
        }

        Assert.Equal(ScopeType.Speaker, StageGraph.NodeOf(StageType.VoiceAssignment).Scope);
        Assert.Equal(ScopeType.Window, StageGraph.NodeOf(StageType.ContextBuild).Scope);

        foreach (var stage in new[] { StageType.Transcription, StageType.Translation, StageType.VoiceGeneration, StageType.TimingOptimization })
        {
            Assert.Equal(ScopeType.Segment, StageGraph.NodeOf(stage).Scope);
        }
    }

    [Fact]
    public void Prerequisites_Match_Spec()
    {
        Assert.Empty(StageGraph.NodeOf(StageType.MediaValidation).Prerequisites);
        Assert.Equal(["MediaValidation"], StageGraph.NodeOf(StageType.MediaAnalysis).Prerequisites);
        Assert.Equal(["SourceSeparation"], StageGraph.NodeOf(StageType.Vad).Prerequisites);
        Assert.Equal(["Diarization"], StageGraph.NodeOf(StageType.Transcription).Prerequisites);
        Assert.Equal(["Transcription"], StageGraph.NodeOf(StageType.ContextBuild).Prerequisites);
        Assert.Equal(["ContextBuild"], StageGraph.NodeOf(StageType.Translation).Prerequisites);
        Assert.Equal(["Diarization"], StageGraph.NodeOf(StageType.VoiceAssignment).Prerequisites);
        Assert.Equal(["Translation", "VoiceAssignment"], StageGraph.NodeOf(StageType.VoiceGeneration).Prerequisites);
        Assert.Equal(["VoiceGeneration"], StageGraph.NodeOf(StageType.TimingOptimization).Prerequisites);
        Assert.Equal(["TimingOptimization"], StageGraph.NodeOf(StageType.TimelineAssembly).Prerequisites);
        Assert.Equal(["AudioMixing"], StageGraph.NodeOf(StageType.QualityControl).Prerequisites);
        Assert.Equal(["QualityControl"], StageGraph.NodeOf(StageType.Render).Prerequisites);
    }

    [Fact]
    public void Roots_Is_MediaValidation_Only_And_Render_Is_Terminal()
    {
        var roots = StageGraph.Roots();
        var root = Assert.Single(roots);
        Assert.Equal(StageType.MediaValidation, root.StageType);
        Assert.Empty(StageGraph.GetSuccessors(StageType.Render));
    }

    [Fact]
    public void Diarization_Fans_Out_To_Transcription_And_VoiceAssignment()
    {
        var successors = StageGraph.GetSuccessors(StageType.Diarization)
            .Select(n => n.StageType).ToList();
        Assert.Contains(StageType.Transcription, successors);
        Assert.Contains(StageType.VoiceAssignment, successors);
    }

    [Fact]
    public void SourceSeparation_Is_The_Only_Skippable_Stage()
    {
        Assert.True(StageGraph.IsSkippable(StageGraph.NodeOf(StageType.SourceSeparation)));
        foreach (var node in StageGraph.Nodes.Where(n => n.StageType != StageType.SourceSeparation))
        {
            Assert.False(StageGraph.IsSkippable(node));
        }
    }

    [Fact]
    public void Queue_Router_Covers_All_Stages_By_Workload_Class()
    {
        var allowed = new[]
        {
            QueueNames.MediaPreparation, QueueNames.MediaRender, QueueNames.AiProvider,
            QueueNames.AiGpu, QueueNames.Export, QueueNames.Maintenance,
        };
        foreach (var stage in Enum.GetValues<StageType>())
        {
            Assert.Contains(WorkQueueRouter.QueueFor(stage), allowed);
        }

        Assert.Equal(QueueNames.MediaPreparation, WorkQueueRouter.QueueFor(StageType.MediaValidation));
        Assert.Equal(QueueNames.AiProvider, WorkQueueRouter.QueueFor(StageType.Transcription));
        Assert.Equal(QueueNames.AiGpu, WorkQueueRouter.QueueFor(StageType.VoiceGeneration));
        Assert.Equal(QueueNames.MediaRender, WorkQueueRouter.QueueFor(StageType.Render));
    }

    [Fact]
    public void Saga_Constructs_With_States_And_Events()
    {
        var saga = new ProcessingRunSaga(
            new StubScopeFactory(),
            Microsoft.Extensions.Options.Options.Create(new RetryOptions()),
            NullLogger<ProcessingRunSaga>.Instance);

        Assert.NotNull(saga.Pending);
        Assert.NotNull(saga.Running);
        Assert.NotNull(saga.Cancelling);
        Assert.NotNull(saga.Cancelled);
        Assert.NotNull(saga.Completed);
        Assert.NotNull(saga.Failed);
        Assert.NotNull(saga.ManualReviewRequired);
        Assert.NotNull(saga.RunStartedEvent);
        Assert.NotNull(saga.StageCompletedEvent);
        Assert.NotNull(saga.StageFailedEvent);
        Assert.NotNull(saga.StageCancelledEvent);
        Assert.NotNull(saga.StageReviewRequiredEvent);
        Assert.NotNull(saga.ReviewResolvedEvent);
        Assert.NotNull(saga.RunCancelledRequestedEvent);
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            return new StubScope();
        }

        private sealed class StubScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

            public void Dispose()
            {
            }
        }
    }
}
