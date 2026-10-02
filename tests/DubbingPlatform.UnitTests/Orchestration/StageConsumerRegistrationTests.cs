// GAP-013: every StageGraph stage must resolve to a registered consumer.
// MediaValidation is dispatched by the saga on RunStarted, so it needs a
// BaseConsumer on media.preparation or the run stalls at the first stage.
using System.Reflection;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Orchestration;
using DubbingPlatform.Workers.Consumers;
using DubbingPlatform.Contracts.Messages;

namespace DubbingPlatform.UnitTests.Orchestration;

public sealed class StageConsumerRegistrationTests
{
    private static readonly Assembly WorkersAssembly = typeof(MediaAnalyzerWorker).Assembly;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DubbingPlatform.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string WorkerProgramSource() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "DubbingPlatform.Workers", "Program.cs"));

    private static string ConsumerSource(Type consumerType)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "DubbingPlatform.Workers", "Consumers", $"{consumerType.Name}.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>
    /// Consumers registered in <c>Workers/Program.cs</c> through
    /// <c>configureExtra: x =&gt; x.AddConsumer&lt;T&gt;()</c>.
    /// </summary>
    private static IReadOnlyList<Type> RegisteredStageConsumers()
    {
        var program = WorkerProgramSource();
        return WorkersAssembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(BaseConsumer<StageWorkRequested>).IsAssignableFrom(t))
            .Where(t => program.Contains($"AddConsumer<{t.FullName}>", StringComparison.Ordinal)
                || program.Contains($"AddConsumer<{t.Name}>", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The single stage a stage consumer filters on, read from the
    /// <c>nameof(StageType.X)</c> literal inside its <c>ShouldProcess</c> body
    /// (the pattern every stage consumer follows).
    /// </summary>
    private static StageType? StageHandledBy(Type consumerType)
    {
        var source = ConsumerSource(consumerType);
        var body = StageFilterBody(source);
        var match = StageLiteral.Match(body);
        if (!match.Success || !Enum.TryParse(match.Groups[1].Value, out StageType stage))
        {
            return null;
        }

        return stage;
    }

    private static readonly Regex StageLiteral = new(@"nameof\((?:Domain\.Enums\.)?StageType\.(\w+)\)", RegexOptions.Compiled);

    private static string StageFilterBody(string source)
    {
        var start = source.IndexOf("ShouldProcess", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = source.IndexOf("HandleAsync", StringComparison.Ordinal);
        return end > start ? source[start..end] : source[start..];
    }

    [Fact]
    public void Every_StageGraph_Stage_Has_A_Registered_BaseConsumer()
    {
        var handled = RegisteredStageConsumers()
            .Select(StageHandledBy)
            .Where(stage => stage.HasValue)
            .Select(stage => stage!.Value)
            .ToHashSet();

        var missing = StageGraph.Nodes
            .Select(n => n.StageType)
            .Where(stage => !handled.Contains(stage))
            .Select(stage => stage.ToString())
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void MediaValidation_Has_A_Registered_Consumer()
    {
        // The saga dispatches StageWorkRequested(MediaValidation) on RunStarted;
        // without a consumer on media.preparation the message is acked as a
        // no-op and the barrier never advances (the run stalls at stage one).
        var consumer = Assert.Single(RegisteredStageConsumers(), t => StageHandledBy(t) == StageType.MediaValidation);
        Assert.Equal("MediaValidationWorker", consumer.Name);

        // Shares the media.preparation endpoint with the other media stages.
        Assert.Equal(
            WorkQueueRouter.QueueFor(StageType.MediaAnalysis),
            WorkQueueRouter.QueueFor(StageType.MediaValidation));
    }

    [Fact]
    public void MediaValidation_Consumer_Uses_The_Shared_Lease_Fenced_Base()
    {
        // Uniform retry/lease/instrumentation comes from BaseConsumer; a bespoke
        // IConsumer would silently drop lease fencing and metrics.
        Assert.True(typeof(BaseConsumer<StageWorkRequested>).IsAssignableFrom(typeof(MediaValidationWorker)));
        Assert.True(typeof(MediaValidationWorker).IsPublic);
        Assert.NotNull(typeof(MediaValidationWorker).GetMethod(
            "HandleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Stage_Consumer_Count_Matches_StageGraph_Size()
    {
        // 17 StageGraph stages, each with exactly one registered consumer.
        Assert.Equal(StageGraph.Nodes.Count, RegisteredStageConsumers().Count);
    }
}