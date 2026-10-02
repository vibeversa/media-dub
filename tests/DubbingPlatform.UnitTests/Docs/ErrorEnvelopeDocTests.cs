// GAP-028: the error envelope. The wire shape is nested
// `{ "error": { code, message, correlationId, details } }` everywhere, while
// Plan B 9.12 still printed the flat shape; the plan also lists nine frontend
// categories where the implementation collapses to seven kinds. The plan snippet
// must match the wire, and the code→kind/hint mapping must cover every code in
// the catalog.
using System.Text.Json;
using System.Text.RegularExpressions;
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Errors;

namespace DubbingPlatform.UnitTests.Docs;

public sealed class ErrorEnvelopeDocTests
{
    private static string PlanB => Read("implementation_plan-B.md");

    private static string NormalizeError => Read(Path.Combine("frontend", "src", "api", "errors", "normalizeError.ts"));

    private static string Kinds => Read(Path.Combine("frontend", "src", "api", "errors", "kinds.ts"));

    [Fact]
    public void Catalog_Is_The_Sixty_Five_Code_Set_The_Plan_And_Frontend_Carry()
    {
        Assert.Equal(65, ErrorCodes.All.Length);
        Assert.Equal(65, BundleErrorCodes().Count);
        Assert.Equal(ErrorCodes.All.OrderBy(c => c, StringComparer.Ordinal), BundleErrorCodes().OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void Plan_Example_Is_The_Nested_Wire_Envelope()
    {
        var example = ExtractPlanEnvelope();
        using var document = JsonDocument.Parse(example);
        var root = document.RootElement;

        // Nested, not flat.
        Assert.False(root.TryGetProperty("code", out _));
        var error = root.GetProperty("error");
        Assert.Equal("PROVIDER_TIMEOUT", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, error.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("correlationId").GetString()));

        // Field-for-field with the implementation record.
        var shape = typeof(ErrorBody).GetProperties()
            .Select(p => p.Name.ToUpperInvariant())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            shape,
            error.EnumerateObject().Select(p => p.Name.ToUpperInvariant()).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Serialized camelCase, as ASP.NET emits it.
        var camel = typeof(ErrorBody).GetProperties()
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["code", "correlationId", "details", "message"], camel);
    }

    [Fact]
    public void ApiError_Still_Documents_The_Nested_Shape()
    {
        // Guard against the doc fix drifting away from the code it documents.
        var source = Read(Path.Combine("src", "DubbingPlatform.Api", "Errors", "ApiError.cs"));
        Assert.Contains("{ error: { code, message, correlationId, details } }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_Mapping_Table_Collapses_To_The_Seven_Implemented_Kinds()
    {
        var kinds = Regex.Matches(Kinds, @"'(?<kind>Validation|Auth|Conflict|Quota|Media|Review|Unknown)'")
            .Select(m => m.Groups["kind"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(7, kinds.Length);

        var section = ExtractSection("### 9.12");
        foreach (var kind in kinds)
        {
            Assert.Contains("`" + kind + "`", section, StringComparison.Ordinal);
        }

        // The nine plan-only category names must not be presented as a live
        // mapping any more; they survive only inside the errata that records why.
        var tables = ExtractMappingTables(section);
        foreach (var collapsed in new[]
        {
            "ValidationError", "AuthenticationError", "AuthorizationError", "QuotaOrRateLimitError",
            "TemporaryProcessingFailure", "UnknownError",
        })
        {
            Assert.DoesNotContain("`" + collapsed + "`", tables, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Frontend_Kind_And_Hint_Maps_Cover_Every_Catalog_Code()
    {
        var kinds = MapKeys("errorKindByCode");
        var hints = MapKeys("recoveryHintByCode");

        foreach (var code in ErrorCodes.All)
        {
            Assert.Contains(code, kinds);
            Assert.Contains(code, hints);
        }

        Assert.Equal(ErrorCodes.All.Length, kinds.Count);
        Assert.Equal(ErrorCodes.All.Length, hints.Count);
    }

    [Fact]
    public void Plan_Mapping_Table_Accounts_For_Every_Code_Class()
    {
        // Each catalog code must fall into one of the plan's mapping rows; the
        // plan documents class→kind, so the classes are named explicitly.
        var section = ExtractSection("### 9.12");
        foreach (var code in ErrorCodes.All.OrderBy(c => c, StringComparer.Ordinal))
        {
            var documented = section.Contains($"`{code}`", StringComparison.Ordinal)
                || section.Contains($"`{code[..1]}_", StringComparison.Ordinal);
            Assert.True(
                documented,
                $"Plan B 9.12 must cover {code} either by name or by its documented class prefix.");
        }
    }

    private static IReadOnlyList<string> MapKeys(string mapName)
    {
        var start = NormalizeError.IndexOf("export const " + mapName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"normalizeError.ts must export {mapName}.");

        var end = NormalizeError.IndexOf("});", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{mapName} literal is unterminated.");

        var body = NormalizeError[start..end];
        return Regex.Matches(body, @"^\s{2}(?<code>[A-Z0-9_]+):", RegexOptions.Multiline)
            .Select(m => m.Groups["code"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> BundleErrorCodes()
    {
        using var bundle = JsonDocument.Parse(Read(Path.Combine("src", "DubbingPlatform.Api", "OpenApi", "openapi.v1.json")));
        return bundle.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ErrorCode")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static string ExtractMappingTables(string section)
    {
        var start = section.IndexOf("| Backend code |", StringComparison.Ordinal);
        Assert.True(start >= 0, "9.12 must carry the code -> kind mapping table.");
        return section[start..];
    }

    private static string ExtractPlanEnvelope()
    {
        var section = ExtractSection("### 9.12");
        var fence = section.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(fence >= 0, "9.12 must contain the envelope example.");
        fence += "```json".Length;
        var end = section.IndexOf("```", fence, StringComparison.Ordinal);
        Assert.True(end > fence, "Unterminated envelope example in 9.12.");
        return section[fence..end];
    }

    private static string ExtractSection(string heading)
    {
        var start = PlanB.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Plan B section {heading} is missing.");
        var end = PlanB.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = PlanB.Length;
        }

        return PlanB[start..end];
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