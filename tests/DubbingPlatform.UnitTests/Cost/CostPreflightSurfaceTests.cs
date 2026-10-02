// GAP-025: the processing-start cost preflight is a boolean gate; the estimate
// is computed and discarded. Start must surface the estimate and a budget
// refusal must carry the figures (estimate vs spend vs cap) in the 429 body.
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Api.Models;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Cost;

public sealed class CostPreflightSurfaceTests
{
    [Fact]
    public void Preflight_Result_Carries_Estimate_Spend_Cap_And_Decision()
    {
        var result = CostPreflightEstimate.Create(1.25, currentSpendUsd: 2.5, limitUsd: 3.0);

        Assert.Equal(1.25, result.EstimateUsd);
        Assert.Equal(2.5, result.CurrentSpendUsd);
        Assert.Equal(3.0, result.LimitUsd);
        Assert.True(result.IsOverBudget);
        Assert.Equal(1.25 + 2.5, result.ProjectedTotalUsd);
    }

    [Fact]
    public void Preflight_Result_Validates_Finite_Figures()
    {
        Assert.Throws<ArgumentException>(() => CostPreflightEstimate.Create(double.NaN, 0, 1));
        Assert.Throws<ArgumentException>(() => CostPreflightEstimate.Create(0, double.PositiveInfinity, 1));
        Assert.Throws<ArgumentException>(() => CostPreflightEstimate.Create(0, 0, double.NegativeInfinity));
    }

    [Fact]
    public void Pipeline_Estimate_Figures_Feed_The_Preflight_Decision()
    {
        // The preflight decision is the same pure arithmetic the estimate uses,
        // so a 429 can never disagree with the estimate it reports.
        var estimate = CostService.EstimatePipeline(10.0, 120, 100);
        Assert.True(estimate > 0);

        var within = CostPreflightEstimate.Create(estimate, currentSpendUsd: 0, limitUsd: estimate + 1);
        Assert.False(within.IsOverBudget);

        var over = CostPreflightEstimate.Create(estimate, currentSpendUsd: estimate, limitUsd: estimate);
        Assert.True(over.IsOverBudget);
    }

    [Fact]
    public void Run_Response_Exposes_The_Estimate()
    {
        var property = typeof(ProcessingRunResponse).GetProperty(nameof(ProcessingRunResponse.CostEstimateUsd));
        Assert.NotNull(property);
        Assert.Equal(typeof(double?), property!.PropertyType);

        // Optional: existing clients keep deserializing start/list responses.
        var last = typeof(ProcessingRunResponse).GetConstructors().Single().GetParameters();
        Assert.True(last[^1].HasDefaultValue);
    }

    [Fact]
    public void Budget_Refusal_Exposes_Figures_Through_Error_Details()
    {
        var ex = new CostBudgetExceededException(CostPreflightEstimate.Create(4.2, currentSpendUsd: 1.0, limitUsd: 3.0));

        Assert.Equal(ErrorCodes.QuotaExceeded, ex.ErrorCode);
        Assert.Equal(429, ex.StatusCode);

        var details = ((IErrorDetailsProvider)ex).GetErrorDetails();
        Assert.Equal(4.2, details["estimateUsd"]);
        Assert.Equal(1.0, details["currentSpendUsd"]);
        Assert.Equal(3.0, details["limitUsd"]);
        Assert.Equal(5.2, details["projectedTotalUsd"]);
        Assert.Equal("cost-per-project", details["dimension"]);

        // Also human-readable for clients that only render the message.
        Assert.Contains("4.20", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3.00", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_Error_Envelope_Merges_The_Figures()
    {
        var ex = new CostBudgetExceededException(CostPreflightEstimate.Create(4.2, currentSpendUsd: 1.0, limitUsd: 3.0));

        var mapped = ApiError.Map(ex);
        Assert.Equal(429, mapped.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, mapped.Code);
        Assert.Equal(4.2, mapped.Details["estimateUsd"]);
        Assert.Equal("cost-per-project", mapped.Details["dimension"]);

        var envelope = new ErrorResponse(new ErrorBody(
            mapped.Code, mapped.Message, "corr-1", mapped.Details));
        Assert.Equal(ErrorCodes.QuotaExceeded, envelope.Error.Code);
        Assert.Equal(5, envelope.Error.Details.Count);
    }

    [Fact]
    public void Controller_Consumes_The_Structured_Preflight_And_Returns_The_Estimate()
    {
        var source = Read("src", "DubbingPlatform.Api", "Controllers", "ProcessingController.cs");

        // The estimate is surfaced, not discarded.
        Assert.Contains("PreflightEstimateAsync", source, StringComparison.Ordinal);
        Assert.Contains("costEstimateUsd", source, StringComparison.Ordinal);
        Assert.Contains("CostBudgetExceededException", source, StringComparison.Ordinal);

        // The figure-returning overload replaced the discarding call.
        Assert.DoesNotContain("await _costs.PreflightAsync(", source, StringComparison.Ordinal);

        // Stage names stay catalogued: preflight remains a start-time gate.
        Assert.Contains("nameof(StageType.MediaAnalysis)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAPI_Advertises_The_Estimate_On_The_Run_Response()
    {
        var bundle = Read("src", "DubbingPlatform.Api", "OpenApi", "openapi.v1.json");
        Assert.Contains("costEstimateUsd", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_Estimates_Are_Priced_Individually()
    {
        // The per-capability estimator still prices transcription by minute and
        // the text capabilities by character (unchanged by this gap).
        var transcription = CostService.Estimate(ProviderCapability.Transcription, new CostUsageDims(2, 0, 0));
        var translation = CostService.Estimate(ProviderCapability.Translation, new CostUsageDims(2, 500, 0));
        Assert.True(transcription > 0);
        Assert.True(translation > 0);
        Assert.NotEqual(transcription, translation);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DubbingPlatform.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}