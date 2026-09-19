using DubbingPlatform.Application.Options;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Pipeline;

/// <summary>
/// Hermetic context-build tests (no Docker): window bounds, deterministic
/// hashing, cross-segment reuse, LLM prompt metadata, and the empty-input
/// skip use the pure <see cref="ContextBuilderService"/> planners directly.
/// DB persistence lives in the service/worker (PG, covered in CI).
/// </summary>
public sealed class ContextBuildTests
{
    private static readonly SortedDictionary<string, string> EmptyGlossary = new(StringComparer.Ordinal);

    private static ContextSegmentInput Input(int sequence, string text, string speaker = "Speaker 1")
    {
        return new ContextSegmentInput(
            Guid.NewGuid(), sequence, speaker, text, Guid.NewGuid(), 2000);
    }

    private static List<ContextSegmentInput> Texts(params string[] texts)
    {
        return texts.Select((text, index) => Input(index, text)).ToList();
    }

    [Fact]
    public void Windows_Bounded()
    {
        var big = new string('a', 400);
        var inputs = Texts(big, big, big, big, big);

        var byTokens = ContextBuilderService.Plan(inputs, "es", EmptyGlossary, string.Empty, 200, 5);

        Assert.Equal(5, byTokens.Count);
        foreach (var window in byTokens)
        {
            Assert.True(window.Members.Count <= 5);
            Assert.True(window.TokenCount <= 200 || window.Overflow);
            Assert.False(window.Overflow);
            Assert.Equal(window.ContextText.Length / 4, window.TokenCount);
        }

        Assert.False(byTokens[0].Split);
        Assert.All(byTokens.Skip(1), w => Assert.True(w.Split));

        var byCount = ContextBuilderService.Plan(inputs, "es", EmptyGlossary, string.Empty, 100000, 2);

        Assert.Equal(3, byCount.Count);
        Assert.Equal([2, 2, 1], byCount.Select(w => w.Members.Count).ToArray());
        Assert.All(byCount, w => Assert.False(w.Split));
        Assert.All(byCount, w => Assert.False(w.Overflow));

        var covered = byCount.SelectMany(w => w.Members.Select(m => m.SegmentId)).ToList();
        Assert.Equal(inputs.Count, covered.Count);
        Assert.Equal(inputs.Count, covered.Distinct().Count());

        var huge = ContextBuilderService.Plan(
            Texts(new string('b', 9000)), "es", EmptyGlossary, string.Empty, 2000, 5);

        var single = Assert.Single(huge);
        Assert.True(single.Overflow);
        Assert.Single(single.Members);
        Assert.Contains(new string('b', 9000), single.ContextText, StringComparison.Ordinal);
    }

    [Fact]
    public void Deterministic_Hash()
    {
        var inputs = Texts("hello world", "second line", "third line");
        var first = ContextBuilderService.Plan(inputs, "es", EmptyGlossary, "casual", 2000, 5);
        var reordered = ContextBuilderService.Plan(
            inputs.AsEnumerable().Reverse().ToList(), "es", EmptyGlossary, "casual", 2000, 5);

        Assert.Equal(first.Count, reordered.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].ContextText, reordered[i].ContextText);
            Assert.Equal(first[i].ContextHash, reordered[i].ContextHash);
            Assert.Equal(first[i].Sequence, reordered[i].Sequence);
            Assert.Equal(ContextBuilderService.ComputeHash(first[i].ContextText), first[i].ContextHash);
        }

        var changed = Texts("hello world", "second line CHANGED", "third line");
        var rebuilt = ContextBuilderService.Plan(changed, "es", EmptyGlossary, "casual", 2000, 5);
        Assert.NotEqual(first[0].ContextHash, rebuilt[0].ContextHash);

        Assert.Equal(64, first[0].ContextHash.Length);
        Assert.DoesNotContain("login", first[0].ContextText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reusable_Across_Segments()
    {
        var inputs = Texts("one", "two", "three");
        var glossary = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["hola"] = "hello",
            ["adios"] = "goodbye",
        };

        var windows = ContextBuilderService.Plan(inputs, "es", glossary, "formal", 2000, 5);

        var window = Assert.Single(windows);
        Assert.Equal(0, window.Sequence);
        Assert.Equal(3, window.Members.Count);
        Assert.False(window.Split);
        Assert.False(window.Overflow);
        Assert.Contains("[0 Speaker 1: one]", window.ContextText, StringComparison.Ordinal);
        Assert.Contains("[2 Speaker 1: three]", window.ContextText, StringComparison.Ordinal);
        Assert.Contains("# target: es", window.ContextText, StringComparison.Ordinal);
        Assert.Contains("# style: formal", window.ContextText, StringComparison.Ordinal);
        Assert.Contains("# - adios => goodbye", window.ContextText, StringComparison.Ordinal);
        Assert.Contains("# - hola => hello", window.ContextText, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_Recorded_When_Llm()
    {
        const string core = "# target: es\n[0 Speaker 1: hello]";
        var prompt = ContextBuilderService.BuildSummaryPrompt(
            core, "en", "es", "context-window-summary", 1, 2000);

        Assert.Equal("context-window-summary", prompt.TemplateId);
        Assert.Equal("1", prompt.TemplateVersion);
        Assert.Equal(ContextBuilderService.ComputeHash(prompt.PromptText), prompt.PromptHash);
        Assert.Equal(ContextBuilderService.ComputeHash(prompt.SystemText), prompt.SystemHash);
        Assert.Equal(64, prompt.PromptHash.Length);
        Assert.Equal(64, prompt.SystemHash.Length);
        Assert.Contains(core, prompt.PromptText, StringComparison.Ordinal);
        Assert.Contains("es", prompt.SystemText, StringComparison.Ordinal);
        Assert.Contains("en", prompt.SystemText, StringComparison.Ordinal);
        Assert.NotEqual(prompt.PromptHash, prompt.SystemHash);
    }

    [Fact]
    public void Empty_Transcripts_Skips()
    {
        Assert.Empty(ContextBuilderService.Plan([], "es", EmptyGlossary, string.Empty, 2000, 5));
        Assert.Empty(ContextBuilderService.Plan(
            Texts("   ", string.Empty), "es", EmptyGlossary, string.Empty, 2000, 5));

        var settings = ContextBuilderService.ParseSettings("{ invalid json");
        Assert.Empty(settings.Glossary);
        Assert.Equal(string.Empty, settings.Style);
        Assert.Null(settings.TargetOverride);

        var parsed = ContextBuilderService.ParseSettings(
            """{"glossary": {"hola": "hello", "bad": 42, "": "empty"}, "style": "formal", "targetLanguage": "fr"}""");
        Assert.Equal("hello", parsed.Glossary["hola"]);
        Assert.DoesNotContain("bad", parsed.Glossary.Keys);
        Assert.Equal("formal", parsed.Style);
        Assert.Equal("fr", parsed.TargetOverride);
    }

    [Fact]
    public void Options_Defaults()
    {
        var options = new ContextOptions();
        Assert.Equal(2000, options.MaxTokens);
        Assert.Equal(5, options.MaxSegmentsPerWindow);
        Assert.False(options.UseLlmSummary);
        Assert.Equal("context-window-summary", options.SummaryTemplateId);
        Assert.Equal(1, options.SummaryTemplateVersion);

        var validator = new ContextOptionsValidator();
        Assert.True(validator.Validate(null, options).Succeeded);
        Assert.False(validator.Validate(null, new ContextOptions { MaxTokens = 50 }).Succeeded);
        Assert.False(validator.Validate(null, new ContextOptions { MaxSegmentsPerWindow = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new ContextOptions { SummaryTemplateId = " " }).Succeeded);
    }
}
