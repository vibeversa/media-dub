// Task 039C: error mapping and correlation propagation unit gap closure.
using System.Diagnostics;
using System.Text.Json;
using DubbingPlatform.Api.Errors;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace DubbingPlatform.UnitTests.Middleware;

/// <summary>
/// Unit coverage for the error-mapping surface:
/// <list type="bullet">
/// <item><c>ApiError.Map</c> - the frozen exception to (status, code,
/// message, details) table for every catalogued exception type.</item>
/// <item><c>ErrorMappingMiddleware</c> - envelope emission, status/content-type,
/// and correlation-id propagation (via <c>InvokeAsync</c> on a
/// <see cref="DefaultHttpContext"/>).</item>
/// <item><c>ErrorResponse</c>/<c>ErrorBody</c> - the exact envelope record
/// shape.</item>
/// <item><c>ErrorCodes</c> - catalog membership and the default-status
/// table.</item>
/// <item>Correlation propagation: Plan B extension cases only for
/// <see cref="CorrelationIdMiddleware"/> (Plan A owns the canonical helper -
/// see the comments below), plus the Task 038
/// <see cref="CorrelationMiddleware"/> facade.</item>
/// </list>
/// The canonical <c>CorrelationIdMiddleware</c> header acceptance policy and
/// minting are owned by Plan A; this file asserts only the Plan B extension
/// contract (bounds, items/TraceIdentifier wiring, the error-envelope
/// correlation propagation, and the <see cref="CorrelationMiddleware"/>
/// propagation-flag facade) and never re-implements the helper.
/// </summary>
public sealed class ErrorMappingTests
{
    [Fact]
    public void ApiError_Maps_Every_Catalogued_Exception_Type()
    {
        var inner = new InvalidOperationException("inner");

        // The seven concrete AppException types plus the catch-all
        // ErrorCodeException: (status, code) pairs from the frozen table.
        AppException[] appExceptions =
        [
            new ConflictException("Clash."),
            new ConflictException("Clash.", inner),
            new ForbiddenException("Denied."),
            new ForbiddenException("Denied.", inner),
            new LeaseLostException("Lease gone."),
            new LeaseLostException("Lease gone.", inner),
            new NotFoundException("Missing."),
            new NotFoundException("Missing.", inner),
            new QuotaExceededException("Too much."),
            new QuotaExceededException("Too much.", inner),
            new RateLimitedException("Slow down."),
            new RateLimitedException("Slow down.", inner),
            new ErrorCodeException(ErrorCodes.StorageUnavailable, "Down."),
            new ErrorCodeException(ErrorCodes.StorageUnavailable, "Down.", inner),
            new IdempotencyInProgressException("In flight.", 30),
        ];

        foreach (var exception in appExceptions)
        {
            var (status, code, message, details) = ApiError.Map(exception);

            Assert.Equal(exception.StatusCode, status);
            Assert.Equal(exception.ErrorCode, code);
            Assert.Equal(ErrorCodes.StatusFor(exception.ErrorCode), status);
            Assert.Equal(exception.Message, message);
            Assert.NotNull(details);
        }

        Assert.Equal(409, ApiError.Map(new LeaseLostException("Lease gone.")).StatusCode);
        Assert.Equal(ErrorCodes.LeaseLost, ApiError.Map(new LeaseLostException("Lease gone.")).Code);
        Assert.Equal(429, ApiError.Map(new QuotaExceededException("Too much.")).StatusCode);
        Assert.Equal(ErrorCodes.RateLimited, ApiError.Map(new RateLimitedException("Slow down.")).Code);
    }

    [Fact]
    public void ApiError_Maps_Innermost_Inner_Exception_For_Wrapped_App_Exceptions()
    {
        var inner = new InvalidOperationException("inner");
        var wrapped = new NotFoundException("Missing.", inner);

        var (_, _, _, details) = ApiError.Map(wrapped);

        Assert.Empty(details);
        Assert.Equal("Missing.", wrapped.Message);
    }

    [Fact]
    public void ApiError_Maps_ValidationException_With_Field_Details()
    {
        var validation = new ValidationException(
        [
            new ValidationFailure("Name", "'Name' must not be empty."),
            new ValidationFailure("Count", "'Count' must be positive."),
            new ValidationFailure("Name", "'Name' is too long."),
        ]);

        var (status, code, message, details) = ApiError.Map(validation);

        Assert.Equal(400, status);
        Assert.Equal(ErrorCodes.ValidationFailed, code);
        Assert.Contains("Name", message);

        // Failures are grouped per property name.
        var nameFailures = Assert.IsType<string[]>(details["Name"]);
        Assert.Equal(2, nameFailures.Length);
        Assert.Contains(nameFailures, m => m.Contains("must not be empty"));
        Assert.IsType<string[]>(details["Count"]);
    }

    [Fact]
    public void ApiError_Maps_DomainException_And_UnauthorizedAccess_To_The_Catalog()
    {
        var (domainStatus, domainCode, _, domainDetails) = ApiError.Map(new DomainException("Bad input."));
        Assert.Equal(400, domainStatus);
        Assert.Equal(ErrorCodes.ValidationFailed, domainCode);
        Assert.Empty(domainDetails);

        var (unauthorizedStatus, unauthorizedCode, _, unauthorizedDetails) =
            ApiError.Map(new UnauthorizedAccessException("Nope."));
        Assert.Equal(401, unauthorizedStatus);
        Assert.Equal(ErrorCodes.Unauthorized, unauthorizedCode);
        Assert.Empty(unauthorizedDetails);
    }

    [Fact]
    public void ApiError_Maps_Unknown_Exception_To_A_Generic_500()
    {
        foreach (var exception in new Exception[]
        {
            new InvalidOperationException("Connection string Host=db Password=hunter2 failed."),
            new NotSupportedException("Unsupported."),
            new TimeoutException("Timed out."),
        })
        {
            var (status, code, message, details) = ApiError.Map(exception);

            Assert.Equal(500, status);
            Assert.Equal(ErrorCodes.InternalError, code);
            Assert.Equal(ApiError.GenericInternalMessage, message);
            Assert.Empty(details);
        }
    }

    [Fact]
    public void ApiError_Redacts_Secrets_From_AppException_Messages()
    {
        var (status, code, message, _) =
            ApiError.Map(new ConflictException("Retry failed for ApiKey=hunter2, later."));

        Assert.Equal(409, status);
        Assert.Equal(ErrorCodes.Conflict, code);
        Assert.DoesNotContain("hunter2", message);
        Assert.Contains("[REDACTED]", message);
    }

    [Fact]
    public void ApiError_Falls_Back_To_The_Code_When_The_Message_Redacts_To_Whitespace()
    {
        // A whitespace-only message has nothing to publish, so the catalog code
        // is used verbatim rather than an empty message.
        var (_, code, message, _) = ApiError.Map(new ConflictException("   "));

        Assert.Equal(ErrorCodes.Conflict, code);
        Assert.Equal(ErrorCodes.Conflict, message);
    }

    [Fact]
    public void ApiError_Merges_And_Redacts_IErrorDetails_Provider_Details()
    {
        var (status, code, _, details) = ApiError.Map(new DetailsCarryingException(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runId"] = "abc",
                ["ApiKey"] = "hunter2",
                ["attempts"] = 3,
            }));

        Assert.Equal(409, status);
        Assert.Equal(ErrorCodes.Conflict, code);
        Assert.Equal("abc", details["runId"]);
        Assert.Equal(3, details["attempts"]);

        // Sensitive detail keys are replaced wholesale, never the value.
        Assert.Equal("[REDACTED]", details["ApiKey"]);
        Assert.DoesNotContain("hunter2", string.Join("|", details.Values.Select(v => v?.ToString() ?? string.Empty)));
    }

    [Fact]
    public void ApiError_Handles_A_Null_Details_Provider_Payload()
    {
        var (_, _, _, details) = ApiError.Map(new NullDetailsException());

        Assert.Empty(details);
    }

    [Fact]
    public void ApiError_Swallows_Details_Provider_Failures()
    {
        var (status, code, message, details) = ApiError.Map(new ThrowingDetailsException());

        Assert.Equal(409, status);
        Assert.Equal(ErrorCodes.Conflict, code);
        Assert.Equal("Broken details.", message);
        Assert.Empty(details);
    }

    [Fact]
    public void ErrorResponse_And_ErrorBody_Are_Positional_Frozen_Records()
    {
        var inner = new Dictionary<string, object?>(StringComparer.Ordinal) { ["runId"] = "abc" };
        var body = new ErrorBody(ErrorCodes.Conflict, "Clash.", "corr-1", inner);
        var response = new ErrorResponse(body);

        Assert.Equal(ErrorCodes.Conflict, body.Code);
        Assert.Equal("Clash.", body.Message);
        Assert.Equal("corr-1", body.CorrelationId);
        Assert.Same(inner, body.Details);
        Assert.Same(body, response.Error);

        // Record value equality is the contract the envelope serializer relies on.
        Assert.Equal(response, new ErrorResponse(new ErrorBody(ErrorCodes.Conflict, "Clash.", "corr-1", inner)));
        Assert.NotEqual(response, new ErrorResponse(new ErrorBody(ErrorCodes.Conflict, "Clash.", "corr-2", inner)));
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Writes_The_Envelope_For_An_Exception()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Items[CorrelationIdMiddleware.ItemKey] = "corr-1";

        var middleware = new ErrorMappingMiddleware(_ => Task.FromException(new ConflictException("Already exists.")));
        await middleware.InvokeAsync(context);

        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("corr-1", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());

        var captured = await ReadEnvelopeAsync(context);
        Assert.Equal(ErrorCodes.Conflict, captured.Code);
        Assert.Equal("Already exists.", captured.Message);
        Assert.Equal("corr-1", captured.CorrelationId);
        Assert.Equal("corr-1", captured.Header);
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Envelope_Shape_Is_Camel_Cased()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Items[CorrelationIdMiddleware.ItemKey] = "corr-shape";

        var middleware = new ErrorMappingMiddleware(
            _ => Task.FromException(new ValidationException(
                [new ValidationFailure("Name", "'Name' must not be empty.")])));
        await middleware.InvokeAsync(context);

        using var document = JsonDocument.Parse(ReadBody(context));
        var error = document.RootElement.GetProperty("error");

        Assert.True(error.TryGetProperty("code", out _));
        Assert.True(error.TryGetProperty("message", out _));
        Assert.True(error.TryGetProperty("correlationId", out _));
        Assert.Equal(JsonValueKind.Object, error.GetProperty("details").ValueKind);
        Assert.True(error.GetProperty("details").TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Propagates_The_Header_Correlation_Id_Into_The_Envelope()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "client-corr-9";
        context.Items[CorrelationIdMiddleware.ItemKey] = "client-corr-9";

        var middleware = new ErrorMappingMiddleware(_ => Task.FromException(new NotFoundException("Missing.")));
        await middleware.InvokeAsync(context);

        var captured = await ReadEnvelopeAsync(context);
        Assert.Equal("client-corr-9", captured.CorrelationId);
        Assert.Equal("client-corr-9", captured.Header);
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Falls_Back_To_The_Trace_Identifier_When_The_Item_Is_Missing()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-fallback";

        var middleware = new ErrorMappingMiddleware(_ => Task.FromException(new NotFoundException("Missing.")));
        await middleware.InvokeAsync(context);

        var captured = await ReadEnvelopeAsync(context);
        Assert.Equal("trace-fallback", captured.CorrelationId);
        Assert.Equal("trace-fallback", captured.Header);
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Hides_Internals_For_An_Unknown_Exception()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Items[CorrelationIdMiddleware.ItemKey] = "corr-500";

        var middleware = new ErrorMappingMiddleware(
            _ => Task.FromException(new InvalidOperationException("Postgres at 10.0.0.5 Password=hunter2 blew up.")));
        await middleware.InvokeAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        var captured = await ReadEnvelopeAsync(context);
        Assert.Equal(ErrorCodes.InternalError, captured.Code);
        Assert.Equal(ApiError.GenericInternalMessage, captured.Message);
        Assert.DoesNotContain("hunter2", captured.RawBody);
        Assert.DoesNotContain("10.0.0.5", captured.RawBody);
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Passes_Through_When_No_Exception_Escapes()
    {
        var context = new DefaultHttpContext();
        var reached = false;

        var middleware = new ErrorMappingMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);

        Assert.True(reached);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Rethrows_When_The_Response_Already_Started()
    {
        var features = new FeatureCollection();
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var context = new DefaultHttpContext(features);

        Assert.True(context.Response.HasStarted);

        var middleware = new ErrorMappingMiddleware(_ => Task.FromException(new ConflictException("Too late.")));

        // The response is committed, so the boundary must not swallow the failure.
        var ex = await Assert.ThrowsAsync<ConflictException>(() => middleware.InvokeAsync(context));
        Assert.Equal("Too late.", ex.Message);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task ErrorMappingMiddleware_Maps_Every_Catalogued_Code_Through_The_Envelope()
    {
        foreach (var code in ErrorCodes.All)
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Items[CorrelationIdMiddleware.ItemKey] = "corr-catalog";

            var middleware = new ErrorMappingMiddleware(_ => Task.FromException(new ErrorCodeException(code, "Boom.")));
            await middleware.InvokeAsync(context);

            var captured = await ReadEnvelopeAsync(context);
            Assert.Equal(code, captured.Code);
            Assert.Equal(ErrorCodes.StatusFor(code), context.Response.StatusCode);
        }
    }

    [Fact]
    public void ErrorCodes_Catalog_Contract_Is_Frozen()
    {
        Assert.Equal(65, ErrorCodes.All.Length);
        Assert.Equal(65, ErrorCodes.All.Distinct(StringComparer.Ordinal).Count());

        foreach (var code in ErrorCodes.All)
        {
            Assert.True(ErrorCodes.IsKnown(code));
        }

        // Every catalogued code is a distinct, non-empty SCREAMING_SNAKE token.
        foreach (var code in ErrorCodes.All)
        {
            Assert.Equal(code, code.Trim());
            Assert.All(code, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '_'));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not_found", false)]
    [InlineData("NOT FOUND", false)]
    [InlineData("FOO_BAR", false)]
    [InlineData(ErrorCodes.NotFound, true)]
    [InlineData(ErrorCodes.InternalError, true)]
    [InlineData(ErrorCodes.UrlExpired, true)]
    public void ErrorCodes_IsKnown_Is_Ordinal_And_Null_Safe(string? code, bool expected)
    {
        Assert.Equal(expected, ErrorCodes.IsKnown(code));
    }

    [Fact]
    public void ErrorCodes_StatusFor_Unknown_Code_Defaults_To_500()
    {
        Assert.Equal(500, ErrorCodes.StatusFor("NOT_A_CATALOG_CODE"));
        Assert.Equal(500, ErrorCodes.StatusFor(string.Empty));
    }

    [Fact]
    public void ErrorCodes_StatusFor_Covers_Every_Http_Family_Used_By_The_Catalog()
    {
        var statuses = ErrorCodes.All.Select(ErrorCodes.StatusFor).ToHashSet();

        foreach (var status in new[] { 400, 401, 403, 404, 409, 410, 413, 415, 422, 429, 500, 502, 503, 504 })
        {
            Assert.Contains(status, statuses);
        }

        // 2xx is never produced by the catalog, and every value is a valid
        // error status in 4xx/5xx.
        Assert.All(statuses, status => Assert.InRange(status, 400, 599));
    }

    [Fact]
    public void ErrorCodeException_Rejects_Uncatalogued_Codes()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ErrorCodeException("MADE_UP_CODE", "Boom."));

        Assert.Equal("errorCode", ex.ParamName);
    }

    [Theory]
    [InlineData(ErrorCodes.LeaseLost, 409)]
    [InlineData(ErrorCodes.QuotaExceeded, 429)]
    [InlineData(ErrorCodes.RateLimited, 429)]
    [InlineData(ErrorCodes.NotFound, 404)]
    [InlineData(ErrorCodes.Forbidden, 403)]
    [InlineData(ErrorCodes.Conflict, 409)]
    [InlineData(ErrorCodes.IdempotencyKeyReused, 422)]
    [InlineData(ErrorCodes.StorageUnavailable, 503)]
    [InlineData(ErrorCodes.ProviderTimeout, 504)]
    [InlineData(ErrorCodes.UrlExpired, 410)]
    public void App_Exception_Statuses_Track_The_Catalog(string code, int expectedStatus)
    {
        var exception = new ErrorCodeException(code, "Boom.");

        Assert.Equal(code, exception.ErrorCode);
        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(expectedStatus, ErrorCodes.StatusFor(code));
    }

    [Fact]
    public void ApiError_Redacts_A_Validation_Failure_Message_And_Its_Detail()
    {
        var validation = new ValidationException(
            [new ValidationFailure("ApiKey", "ApiKey=hunter2 is not acceptable.")]);

        var (status, code, message, details) = ApiError.Map(validation);

        Assert.Equal(400, status);
        Assert.Equal(ErrorCodes.ValidationFailed, code);
        Assert.DoesNotContain("hunter2", message);
        Assert.DoesNotContain(
            "hunter2",
            string.Join("|", details.Values.Select(v => v?.ToString() ?? string.Empty)));
    }

    [Fact]
    public void ApiError_Leaves_A_Non_Sensitive_Validation_Detail_Unredacted()
    {
        var validation = new ValidationException(
            [new ValidationFailure("SegmentCount", "'SegmentCount' must be positive.")]);

        var (_, _, _, details) = ApiError.Map(validation);
        var messages = Assert.IsType<string[]>(details["SegmentCount"]);

        Assert.Contains("must be positive", messages[0]);
    }

    [Fact]
    public void ApiError_Handles_A_ValidationException_With_No_Failures()
    {
        var (status, code, message, details) = ApiError.Map(new ValidationException([]));

        Assert.Equal(400, status);
        Assert.Equal(ErrorCodes.ValidationFailed, code);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Empty(details);
    }

    [Fact]
    public void IdempotencyInProgressException_Carries_Retry_After_And_Conflicts()
    {
        var exception = new IdempotencyInProgressException("Key is in flight.", 45);

        Assert.Equal(ErrorCodes.Conflict, exception.ErrorCode);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(45, exception.RetryAfterSeconds);
        Assert.Equal("Key is in flight.", exception.Message);
    }

    // ---------------------------------------------------------------------
    // Correlation propagation.
    //
    // Plan A owns the canonical CorrelationIdMiddleware helper (header
    // acceptance policy, random 32-hex minting, and the echo). This file only
    // adds the Plan B extension cases: the getter's fallback precedence, the
    // Items/TraceIdentifier/header wiring asserted through the pipeline, and the
    // Task 038 CorrelationMiddleware propagation-flag facade. Nothing here
    // re-implements the Plan A helper.
    // ---------------------------------------------------------------------

    [Fact]
    public void CorrelationIdMiddleware_GetCorrelationId_Prefers_The_Item_Then_The_Trace_Identifier()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-1";

        // No item yet: falls back to the trace identifier.
        Assert.Equal("trace-1", CorrelationIdMiddleware.GetCorrelationId(context));

        context.Items[CorrelationIdMiddleware.ItemKey] = "item-1";
        Assert.Equal("item-1", CorrelationIdMiddleware.GetCorrelationId(context));

        // A blank or non-string item is treated as absent.
        context.Items[CorrelationIdMiddleware.ItemKey] = "   ";
        Assert.Equal("trace-1", CorrelationIdMiddleware.GetCorrelationId(context));

        context.Items[CorrelationIdMiddleware.ItemKey] = 42;
        Assert.Equal("trace-1", CorrelationIdMiddleware.GetCorrelationId(context));
    }

    [Fact]
    public void CorrelationIdMiddleware_Constants_Are_Frozen()
    {
        Assert.Equal("X-Correlation-Id", CorrelationIdMiddleware.HeaderName);
        Assert.Equal("CorrelationId", CorrelationIdMiddleware.ItemKey);

        // The Task 038 facade aliases the canonical names rather than forking them.
        Assert.Equal(CorrelationIdMiddleware.HeaderName, CorrelationMiddleware.HeaderName);
        Assert.Equal(CorrelationIdMiddleware.ItemKey, CorrelationMiddleware.ItemKey);
        Assert.Equal("CorrelationPropagated", CorrelationMiddleware.PropagatedItemKey);
    }

    [Fact]
    public async Task CorrelationIdMiddleware_Echoes_The_Supplied_Id_And_Exposes_It_To_The_Pipeline()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "  client-corr-7  ";
        string? seenItem = null;
        string? seenTrace = null;

        var middleware = new CorrelationIdMiddleware(next =>
        {
            seenItem = next.Items[CorrelationIdMiddleware.ItemKey] as string;
            seenTrace = next.TraceIdentifier;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);

        // Plan A trims the incoming value; the trimmed id reaches items, the
        // trace identifier, and the response header.
        Assert.Equal("client-corr-7", seenItem);
        Assert.Equal("client-corr-7", seenTrace);
        Assert.Equal("client-corr-7", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
        Assert.Equal("client-corr-7", CorrelationIdMiddleware.GetCorrelationId(context));
    }

    [Theory]
    [MemberData(nameof(RejectedCorrelationIds))]
    public async Task CorrelationIdMiddleware_Rejects_Unsafe_Caller_Ids_At_The_Pipeline_Boundary(string? header)
    {
        // Plan A owns the acceptance policy; this is the Plan B extension case
        // that the policy is actually enforced when the middleware runs, not only
        // through the pure facade. An empty, blank, over-long, or illegal value
        // must never be echoed back verbatim.
        var context = new DefaultHttpContext();
        if (header is not null)
        {
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = header;
        }

        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var used = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.ItemKey]);
        Assert.NotEqual((header ?? string.Empty).Trim(), used);
        Assert.Equal(32, used.Length);
        Assert.All(used, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.Equal(used, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task CorrelationIdMiddleware_Rejects_Exactly_129_Characters_But_Accepts_128()
    {
        // The bound is inclusive at 128 and exclusive at 129.
        var atBound = new DefaultHttpContext();
        atBound.Request.Headers[CorrelationIdMiddleware.HeaderName] = new string('a', 128);
        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(atBound);
        Assert.Equal(new string('a', 128), atBound.Items[CorrelationIdMiddleware.ItemKey]);

        var overBound = new DefaultHttpContext();
        overBound.Request.Headers[CorrelationIdMiddleware.HeaderName] = new string('a', 129);
        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(overBound);
        Assert.NotEqual(new string('a', 129), overBound.Items[CorrelationIdMiddleware.ItemKey]);
    }

    [Fact]
    public async Task CorrelationIdMiddleware_Tags_The_Active_Activity_With_The_Correlation_Id()
    {
        using var activity = new System.Diagnostics.Activity("corr-activity-1");
        activity.Start();

        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "corr-activity-1";
        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.Equal("corr-activity-1", activity.GetTagItem("correlation.id"));
    }

    [Fact]
    public async Task CorrelationIdMiddleware_Mints_An_Accepted_Header_In_Pipeline_And_Propagates_It_To_The_Error_Envelope()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "corr-into-envelope";

        // Compose the canonical middleware ahead of the error boundary: the id
        // minted/accepted upstream must be the one the envelope reports.
        var errorBoundary = new ErrorMappingMiddleware(_ => Task.FromException(new ConflictException("Clash.")));
        var correlation = new CorrelationIdMiddleware(errorBoundary.InvokeAsync);
        await correlation.InvokeAsync(context);

        var captured = await ReadEnvelopeAsync(context);
        Assert.Equal("corr-into-envelope", captured.CorrelationId);
        Assert.Equal("corr-into-envelope", captured.Header);
    }

    public static TheoryData<string> AcceptedCorrelationIds() =>
    [
        "a",
        "A-b_9",
        "0",
        "corr-action-1",
        new string('x', 128),
    ];

    public static TheoryData<string?> RejectedCorrelationIds() =>
    [
        null,
        string.Empty,
        "   ",
        "bad id",
        "bad/id",
        "bad.id",
        "bad\nid",
        "bad:id",
        "bad;id",
        "bad?id",
        "bad#id",
        "bad%id",
        "bad&id",
        "bad(id)",
        "bad[id]",
        "bad{id}",
        "bad\\id",
        "bad\"id",
        "bad'id",
        "bad+id",
        "bad=id",
        "bad`id",
        "bad|id",
        new string('x', 129),
        new string('x', 512),
    ];

    [Theory]
    [MemberData(nameof(AcceptedCorrelationIds))]
    public void CorrelationMiddleware_IsPropagatedId_Accepts_Safe_Caller_Ids(string candidate)
    {
        Assert.True(CorrelationMiddleware.IsPropagatedId(candidate));
    }

    [Theory]
    [MemberData(nameof(RejectedCorrelationIds))]
    public void CorrelationMiddleware_IsPropagatedId_Rejects_Unsafe_Caller_Ids(string? candidate)
    {
        Assert.False(CorrelationMiddleware.IsPropagatedId(candidate));
    }

    [Fact]
    public void CorrelationMiddleware_ResolveWithPropagation_Passes_Safe_Ids_Through()
    {
        var (safe, propagated) = CorrelationMiddleware.ResolveWithPropagation("corr-action-1");
        Assert.Equal("corr-action-1", safe);
        Assert.True(propagated);

        var (padded, paddedPropagated) = CorrelationMiddleware.ResolveWithPropagation("  corr-action-2  ");
        Assert.Equal("corr-action-2", padded);
        Assert.True(paddedPropagated);
    }

    [Theory]
    [MemberData(nameof(RejectedCorrelationIds))]
    public void CorrelationMiddleware_ResolveWithPropagation_Mints_For_Missing_Or_Unsafe_Ids(string? candidate)
    {
        var (minted, propagated) = CorrelationMiddleware.ResolveWithPropagation(candidate);

        Assert.False(propagated);
        Assert.Equal(32, minted.Length);
        Assert.All(minted, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void CorrelationMiddleware_Minted_Ids_Are_Random_Not_Sequential()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 32; i++)
        {
            ids.Add(CorrelationMiddleware.ResolveWithPropagation(null).CorrelationId);
        }

        // 32 distinct 32-hex ids: minted, not a counter.
        Assert.Equal(32, ids.Count);
    }

    [Fact]
    public async Task CorrelationMiddleware_InvokeAsync_Records_Propagation_For_An_Accepted_Header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "corr-action-7";
        string? seenItem = null;

        var middleware = new CorrelationMiddleware(next =>
        {
            seenItem = next.Items[CorrelationMiddleware.ItemKey] as string;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);

        Assert.Equal("corr-action-7", seenItem);
        Assert.Equal("corr-action-7", context.Items[CorrelationMiddleware.ItemKey]);
        Assert.Equal("corr-action-7", context.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
        Assert.True(CorrelationMiddleware.WasPropagated(context));
    }

    [Fact]
    public async Task CorrelationMiddleware_InvokeAsync_Mints_And_Marks_Not_Propagated_For_A_Missing_Header()
    {
        var context = new DefaultHttpContext();

        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var minted = Assert.IsType<string>(context.Items[CorrelationMiddleware.ItemKey]);
        Assert.Equal(32, minted.Length);
        Assert.Equal(minted, context.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
        Assert.False(CorrelationMiddleware.WasPropagated(context));
    }

    [Fact]
    public async Task CorrelationMiddleware_InvokeAsync_Mints_And_Marks_Not_Propagated_For_An_Unsafe_Header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "bad id!";

        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var minted = Assert.IsType<string>(context.Items[CorrelationMiddleware.ItemKey]);
        Assert.NotEqual("bad id!", minted);
        Assert.Equal(minted, context.Response.Headers[CorrelationMiddleware.HeaderName].ToString());
        Assert.False(CorrelationMiddleware.WasPropagated(context));
    }

    [Fact]
    public void CorrelationMiddleware_WasPropagated_Returns_Null_Until_InvokeAsync_Runs()
    {
        var context = new DefaultHttpContext();

        Assert.Null(CorrelationMiddleware.WasPropagated(context));

        // A non-bool item under the key is treated as "not run".
        context.Items[CorrelationMiddleware.PropagatedItemKey] = "true";
        Assert.Null(CorrelationMiddleware.WasPropagated(context));
    }

    [Fact]
    public async Task CorrelationMiddleware_InvokeAsync_Rejects_A_Null_Context()
    {
        var middleware = new CorrelationMiddleware(_ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CorrelationMiddleware.WasPropagated(null!));
    }

    [Fact]
    public async Task CorrelationMiddleware_Facade_Id_Reaches_The_Error_Envelope()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers[CorrelationMiddleware.HeaderName] = "corr-facade-1";

        var errorBoundary = new ErrorMappingMiddleware(_ => Task.FromException(new ForbiddenException("Denied.")));
        var facade = new CorrelationMiddleware(errorBoundary.InvokeAsync);
        await facade.InvokeAsync(context);

        var captured = await ReadEnvelopeAsync(context);
        Assert.Equal("corr-facade-1", captured.CorrelationId);
        Assert.Equal("corr-facade-1", captured.Header);
        Assert.True(CorrelationMiddleware.WasPropagated(context));
        Assert.Equal(403, context.Response.StatusCode);
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static async Task<CapturedEnvelope> ReadEnvelopeAsync(HttpContext context)
    {
        var body = ReadBody(context);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");

        return new CapturedEnvelope(
            error.GetProperty("code").GetString() ?? string.Empty,
            error.GetProperty("message").GetString() ?? string.Empty,
            error.GetProperty("correlationId").GetString() ?? string.Empty,
            context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString(),
            body);
    }

    private sealed record CapturedEnvelope(
        string Code,
        string Message,
        string CorrelationId,
        string Header,
        string RawBody);

    /// <summary>
    /// A response feature that reports the response as already committed, so the
    /// "cannot write an envelope any more" branch of the error boundary is
    /// reachable without a real server.
    /// </summary>
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => true;

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public string? ReasonPhrase { get; set; }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }

    private abstract class DetailsExceptionBase(string message) : AppException(ErrorCodes.Conflict, message)
    {
        public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
    }

    private sealed class DetailsCarryingException(IReadOnlyDictionary<string, object?> details)
        : DetailsExceptionBase("Clash."), IErrorDetailsProvider
    {
        public IReadOnlyDictionary<string, object?> GetErrorDetails() => details;
    }

    private sealed class NullDetailsException : DetailsExceptionBase, IErrorDetailsProvider
    {
        public NullDetailsException()
            : base("No details.")
        {
        }

        public IReadOnlyDictionary<string, object?> GetErrorDetails() => null!;
    }

    private sealed class ThrowingDetailsException : DetailsExceptionBase, IErrorDetailsProvider
    {
        public ThrowingDetailsException()
            : base("Broken details.")
        {
        }

        public IReadOnlyDictionary<string, object?> GetErrorDetails() =>
            throw new InvalidOperationException("details provider exploded");
    }
}
