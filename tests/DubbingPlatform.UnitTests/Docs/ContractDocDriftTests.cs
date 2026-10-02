// GAP-027: contract-doc drift. (a) Plan B's `reviewThreshold` example used an
// enum string while the validator requires a number in [0,1]; (b) Plan B's
// VoicePreviewJob field list drifted from the implementation. The docs must
// match the implementation field-for-field, and the plan's own examples must
// satisfy the validator that ships.
using System.Text.RegularExpressions;
using DubbingPlatform.Application.Validation;
using DubbingPlatform.Domain.Entities;

namespace DubbingPlatform.UnitTests.Docs;

public sealed class ContractDocDriftTests
{
    private static string PlanB => Read("implementation_plan-B.md");

    private static string ContractDoc => Read(Path.Combine("docs", "api-contract.md"));

    [Fact]
    public void Plan_Example_For_ReviewThreshold_Is_A_Number_In_Range()
    {
        Assert.DoesNotContain("\"reviewThreshold\": \"Default\"", PlanB, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewThreshold\": \"", PlanB, StringComparison.Ordinal);

        var example = ExtractSettingsExampleJson();
        Assert.Contains("\"reviewThreshold\"", example, StringComparison.Ordinal);

        // The plan's own example must satisfy the shipped validator.
        Assert.True(ProjectProcessingSettingsValidator.HaveValidShape(example));
    }

    [Fact]
    public void Plan_Example_ReviewThreshold_Is_Rejected_As_A_String()
    {
        // The behavior the plan example used to promise is explicitly rejected,
        // so the doc fix cannot silently regress into enum-string support.
        Assert.False(ProjectProcessingSettingsValidator.HaveValidShape(
            """{"schemaVersion":1,"reviewThreshold":"Default"}"""));
    }

    [Fact]
    public void Plan_VoicePreviewJob_Field_List_Matches_The_Implementation()
    {
        var documented = PlanFieldNames(PlanB, "8.6.1");
        var actual = typeof(VoicePreviewJob)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(actual, documented);

        // Names the plan used before the alignment are gone.
        foreach (var stale in new[] { "VoiceProfileId", "RequestedText", "FailureCategory", "ExpiresAt" })
        {
            Assert.DoesNotContain(stale, documented);
        }
    }

    [Fact]
    public void Contract_Doc_Lists_VoicePreviewJob_Fields_Field_For_Field()
    {
        var documented = ContractDocFieldNames();
        var actual = typeof(VoicePreviewJob)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(actual, documented);
    }

    [Fact]
    public void Contract_Doc_Names_The_Deviating_Plan_Fields()
    {
        // The doc must record why the plan example was wrong, not just silently
        // differ from it.
        Assert.Contains("Default", ContractDoc, StringComparison.Ordinal);
        Assert.Contains("reviewThreshold", ContractDoc, StringComparison.Ordinal);
    }

    private static string ExtractSettingsExampleJson()
    {
        var start = PlanB.IndexOf("#### 8.2.2", StringComparison.Ordinal);
        Assert.True(start >= 0, "Plan B 8.2.2 section is missing.");

        var fence = PlanB.IndexOf("```json", start, StringComparison.Ordinal);
        Assert.True(fence >= 0, "8.2.2 must contain a json example.");
        fence += "```json".Length;

        var end = PlanB.IndexOf("```", fence, StringComparison.Ordinal);
        Assert.True(end > fence, "Unterminated json example in 8.2.2.");
        return PlanB[fence..end];
    }

    private static IReadOnlyList<string> PlanFieldNames(string plan, string section)
    {
        var start = plan.IndexOf("#### " + section, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Plan B {section} section is missing.");

        // Stop at the next structural marker: the sibling status/rule lists that
        // follow the field list also use `- \`Name\`` bullets.
        var end = -1;
        foreach (var marker in new[] { "\nStatuses:", "\nRules:", "\n### " })
        {
            var candidate = plan.IndexOf(marker, start, StringComparison.Ordinal);
            if (candidate > 0 && (end < 0 || candidate < end))
            {
                end = candidate;
            }
        }

        if (end < 0)
        {
            end = plan.Length;
        }

        var names = new List<string>();
        foreach (Match match in Regex.Matches(
            plan[start..end], @"^- `(?<name>[A-Za-z]+)`", RegexOptions.Multiline))
        {
            names.Add(match.Groups["name"].Value);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static IReadOnlyList<string> ContractDocFieldNames()
    {
        var doc = ContractDoc;
        var start = doc.IndexOf("### VoicePreviewJob", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/api-contract.md must document VoicePreviewJob.");
        var end = doc.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
        if (end < 0)
        {
            end = doc.Length;
        }

        var names = new List<string>();
        foreach (Match match in Regex.Matches(
            doc[start..end], @"^\| `(?<name>[A-Za-z]+)`", RegexOptions.Multiline))
        {
            names.Add(match.Groups["name"].Value);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string Read(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DubbingPlatform.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, relativePath);
        Assert.True(File.Exists(path), $"Expected {relativePath} at {path}.");
        return File.ReadAllText(path);
    }
}