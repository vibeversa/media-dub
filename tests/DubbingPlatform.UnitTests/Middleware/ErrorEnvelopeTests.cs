using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;

namespace DubbingPlatform.UnitTests.Middleware;

/// <summary>
/// Verifies exception-to-envelope mapping, the exact envelope shape, secret
/// redaction, and correlation-ID handling for the API middleware.
/// </summary>
public sealed class ErrorEnvelopeTests
{
    public static TheoryData<Exception, int, string> ExceptionMappings()
    {
        var data = new TheoryData<Exception, int, string>
        {
            { new DomainException("Invalid input."), 400, ErrorCodes.ValidationFailed },
            { new UnauthorizedAccessException("Nope."), 401, ErrorCodes.Unauthorized },
            { new ForbiddenException("Denied."), 403, ErrorCodes.Forbidden },
            { new NotFoundException("Missing."), 404, ErrorCodes.NotFound },
            { new ConflictException("Clash."), 409, ErrorCodes.Conflict },
            { new QuotaExceededException("Too much."), 429, ErrorCodes.QuotaExceeded },
            { new RateLimitedException("Slow down."), 429, ErrorCodes.RateLimited },
            { new InvalidOperationException("Boom."), 500, ErrorCodes.InternalError },
        };

        data.Add(
            new ValidationException([new ValidationFailure("Name", "'Name' must not be empty.")]),
            400,
            ErrorCodes.ValidationFailed);

        foreach (var code in ErrorCodes.All)
        {
            data.Add(new ErrorCodeException(code, $"Failure {code}."), ErrorCodes.StatusFor(code), code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExceptionMappings))]
    public async Task Exception_Maps_To_Code_And_Status(Exception exception, int expectedStatus, string expectedCode)
    {
        var response = await InvokeAsync(exception);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(expectedCode, response.Code);
    }

    [Fact]
    public async Task All_36_Error_Codes_Are_Mappable()
    {
        Assert.Equal(36, ErrorCodes.All.Length);

        foreach (var code in ErrorCodes.All)
        {
            var exception = new ErrorCodeException(code, "Mapped.");
            var response = await InvokeAsync(exception);
            Assert.Equal(code, response.Code);
            Assert.Equal(ErrorCodes.StatusFor(code), response.StatusCode);
        }
    }

    [Fact]
    public async Task Envelope_Shape_Is_Exact()
    {
        var response = await InvokeAsync(new ConflictException("Already exists."), "corr-1");

        Assert.Equal("CONFLICT", response.Code);
        Assert.Equal("Already exists.", response.Message);
        Assert.Equal("corr-1", response.CorrelationId);
        Assert.Equal("corr-1", response.Header);
        Assert.NotNull(response.Details);

        using var document = JsonDocument.Parse(response.RawBody);
        Assert.True(document.RootElement.TryGetProperty("error", out var error));
        Assert.True(error.TryGetProperty("code", out _));
        Assert.True(error.TryGetProperty("message", out _));
        Assert.True(error.TryGetProperty("correlationId", out _));
        Assert.True(error.TryGetProperty("details", out var details));
        Assert.Equal(JsonValueKind.Object, details.ValueKind);
    }

    [Fact]
    public async Task Internal_Error_Hides_Detail()
    {
        var response = await InvokeAsync(new InvalidOperationException("Connection string Host=db Password=hunter2 failed."));

        Assert.Equal(500, response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", response.Code);
        Assert.Equal("An unexpected error occurred.", response.Message);
        Assert.DoesNotContain("hunter2", response.RawBody);
        Assert.DoesNotContain("Password", response.RawBody);
    }

    [Fact]
    public async Task Secrets_Are_Redacted_From_Messages()
    {
        var response = await InvokeAsync(
            new DomainException("Call failed for SecretKey=hunter2, retry later."));

        Assert.Equal("VALIDATION_FAILED", response.Code);
        Assert.Contains("[REDACTED]", response.Message);
        Assert.DoesNotContain("hunter2", response.Message);
        Assert.DoesNotContain("hunter2", response.RawBody);
    }

    [Fact]
    public async Task Validation_Failures_Carry_Field_Details()
    {
        var failures = new[]
        {
            new ValidationFailure("Name", "'Name' must not be empty."),
            new ValidationFailure("Count", "'Count' must be positive."),
        };

        var response = await InvokeAsync(new ValidationException(failures));

        Assert.Equal(400, response.StatusCode);
        Assert.True(response.Details.TryGetValue("Name", out _));
        Assert.True(response.Details.TryGetValue("Count", out _));
    }

    [Fact]
    public async Task Missing_Correlation_Header_Generates_Random_Id()
    {
        var context = await InvokeCorrelationAsync(correlationHeader: null);

        var generated = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.ItemKey]);
        Assert.Equal(32, generated.Length);
        Assert.True(generated.All(char.IsAsciiHexDigit));
        Assert.Equal(generated, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task Provided_Correlation_Id_Is_Propagated()
    {
        var context = await InvokeCorrelationAsync("client-123_ABC");

        Assert.Equal("client-123_ABC", context.Items[CorrelationIdMiddleware.ItemKey]);
        Assert.Equal("client-123_ABC", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task Unsafe_Correlation_Id_Is_Replaced()
    {
        var context = await InvokeCorrelationAsync("bad id!");

        var used = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.ItemKey]);
        Assert.NotEqual("bad id!", used);
        Assert.Equal(used, context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }

    private static async Task<CapturedResponse> InvokeAsync(Exception exception, string? correlationHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (correlationHeader is not null)
        {
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = correlationHeader;
            context.Items[CorrelationIdMiddleware.ItemKey] = correlationHeader;
        }

        var middleware = new ExceptionHandlingMiddleware(_ => Task.FromException(exception));
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in error.GetProperty("details").EnumerateObject())
        {
            details[property.Name] = property.Value.ToString();
        }

        return new CapturedResponse(
            context.Response.StatusCode,
            context.Response.ContentType ?? string.Empty,
            error.GetProperty("code").GetString() ?? string.Empty,
            error.GetProperty("message").GetString() ?? string.Empty,
            error.GetProperty("correlationId").GetString() ?? string.Empty,
            context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString(),
            details,
            body);
    }

    private static async Task<HttpContext> InvokeCorrelationAsync(string? correlationHeader)
    {
        var context = new DefaultHttpContext();
        if (correlationHeader is not null)
        {
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = correlationHeader;
        }

        string? seen = null;
        var middleware = new CorrelationIdMiddleware(nextContext =>
        {
            seen = nextContext.Items[CorrelationIdMiddleware.ItemKey] as string;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        Assert.NotNull(seen);
        return context;
    }

    private sealed record CapturedResponse(
        int StatusCode,
        string ContentType,
        string Code,
        string Message,
        string CorrelationId,
        string Header,
        Dictionary<string, object?> Details,
        string RawBody);
}
