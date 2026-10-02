// GAP-026: the plan's `POST /projects/{id}/cancel|retry` was implemented as
// run-scoped canonical routes (`processing/{runId}/cancel|retry`) plus
// project-scoped compat routes (`processing/cancel|retry`). The contract must
// mark which is which so clients do not build on the legacy pair.
using System.Text.Json;

namespace DubbingPlatform.UnitTests.Api;

public sealed class ProcessingRouteCanonicalityTests
{
    private static readonly JsonDocument Bundle = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "DubbingPlatform.Api", "OpenApi", "openapi.v1.json")));

    private const string CanonicalCancel = "/projects/{projectId}/processing/{runId}/cancel";
    private const string CanonicalRetry = "/projects/{projectId}/processing/{runId}/retry";
    private const string CompatCancel = "/projects/{projectId}/processing/cancel";
    private const string CompatRetry = "/projects/{projectId}/processing/retry";

    [Theory]
    [InlineData(CanonicalCancel)]
    [InlineData(CanonicalRetry)]
    public void Canonical_Run_Scoped_Routes_Are_Not_Deprecated_And_Say_So(string path)
    {
        var operation = Post(path);
        Assert.False(operation.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True);
        Assert.Contains("canonical", operation.GetProperty("description").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CompatCancel, CanonicalCancel)]
    [InlineData(CompatRetry, CanonicalRetry)]
    public void Compat_Project_Scoped_Routes_Are_Deprecated_And_Point_At_The_Canonical_Route(string path, string canonical)
    {
        var operation = Post(path);
        Assert.True(operation.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True);

        var description = operation.GetProperty("description").GetString()!;
        Assert.Contains("compat", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(canonical[(canonical.IndexOf("/processing", StringComparison.Ordinal) + 1)..], description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CompatCancel)]
    [InlineData(CompatRetry)]
    public void Compat_Routes_Keep_Their_Operation_Ids_For_Back_Compatibility(string path)
    {
        // Renaming an operationId is a breaking change for existing clients;
        // the deprecation must not also rename it.
        var operationId = Post(path).GetProperty("operationId").GetString();
        Assert.NotNull(operationId);
        Assert.StartsWith(
            path.Contains("/retry", StringComparison.Ordinal) ? "retryActive" : "cancelActive",
            operationId!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_Routes_Match_The_Declared_Canonical_And_Compat_Paths()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "DubbingPlatform.Api", "Controllers", "ProcessingController.cs"));

        Assert.Contains("\"processing/{runId}/cancel\"", source, StringComparison.Ordinal);
        Assert.Contains("\"processing/{runId}/retry\"", source, StringComparison.Ordinal);
        Assert.Contains("\"processing/cancel\"", source, StringComparison.Ordinal);
        Assert.Contains("\"processing/retry\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_Records_The_Route_Shape_Errata()
    {
        var plan = File.ReadAllText(Path.Combine(FindRepoRoot(), "implementation_plan-A.md"));

        Assert.Contains("Errata", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("processing/{runId}/cancel", plan, StringComparison.Ordinal);
        Assert.Contains("processing/{runId}/retry", plan, StringComparison.Ordinal);
    }

    private static JsonElement Post(string path)
    {
        var paths = Bundle.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty(path, out var pathItem), $"Missing path {path} in the OpenAPI bundle.");
        Assert.True(pathItem.TryGetProperty("post", out var operation), $"Missing POST operation on {path}.");
        return operation;
    }

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
}