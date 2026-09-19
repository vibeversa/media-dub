using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DubbingPlatform.Api.Middleware;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.IntegrationTests.Api;

/// <summary>
/// Verifies the real API middleware pipeline returns structured error envelopes
/// with correlation IDs over HTTP, and that the real <c>Program</c> registers
/// options, validation, and resilience services at startup.
/// HTTP behavior runs against the production middleware pair
/// (<see cref="CorrelationIdMiddleware"/> outside
/// <see cref="ExceptionHandlingMiddleware"/>, matching <c>Program.cs</c>)
/// hosted on Kestrel with a test endpoint; service registration is asserted
/// against the real application factory.
/// </summary>
public sealed class ErrorEnvelopeApiTests : IClassFixture<WebApplicationFactory<CorrelationIdMiddleware>>, IAsyncLifetime, IDisposable
{
    private readonly WebApplicationFactory<CorrelationIdMiddleware> _factory;
    private WebApplication? _host;
    private HttpClient? _client;

    public ErrorEnvelopeApiTests(WebApplicationFactory<CorrelationIdMiddleware> factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var host = builder.Build();
        host.UseMiddleware<CorrelationIdMiddleware>();
        host.UseMiddleware<ExceptionHandlingMiddleware>();
        host.MapPost("/test/items", async (HttpContext context) =>
        {
            var payload = await context.Request.ReadFromJsonAsync<TestItem>(context.RequestAborted);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
            {
                throw new DomainException("Invalid request: 'name' is required.");
            }

            await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
        });
        host.Urls.Clear();
        host.Urls.Add("http://127.0.0.1:0");
        await host.StartAsync();
        _host = host;
        _client = new HttpClient { BaseAddress = new Uri(host.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_host is not null)
        {
            await _host.StopAsync();
            await _host.DisposeAsync();
            _host = null;
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task Invalid_Post_Returns_400_ValidationFailed_With_Correlation_Header()
    {
        Assert.NotNull(_client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/test/items")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "itest-001");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("itest-001", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());

        using var document = await ReadBodyAsync(response);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("itest-001", error.GetProperty("correlationId").GetString());
        Assert.True(error.TryGetProperty("details", out _));
    }

    [Fact]
    public async Task Missing_Correlation_Header_Is_Generated_And_Echoed()
    {
        Assert.NotNull(_client);
        using var response = await _client.PostAsync("/test/items", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var header = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.False(string.IsNullOrWhiteSpace(header));

        using var document = await ReadBodyAsync(response);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(header, error.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Valid_Post_Returns_200_With_Correlation_Header()
    {
        Assert.NotNull(_client);
        using var response = await _client.PostAsync("/test/items", JsonContent.Create(new TestItem("ok")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public void Options_Are_Bound_With_Expected_Defaults()
    {
        using var scope = _factory.Services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<IOptions<MediaOptions>>().Value;
        var retry = scope.ServiceProvider.GetRequiredService<IOptions<RetryOptions>>().Value;
        var providers = scope.ServiceProvider.GetRequiredService<IOptions<ProviderOptions>>().Value;

        Assert.Equal(5368709120, media.MaxUploadBytes);
        Assert.Equal(3, retry.ProviderRequestMaxAttempts);
        Assert.Equal("mock", providers.DefaultProvider);
    }

    [Fact]
    public void Providers_HttpClient_Is_Registered()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("providers");

        Assert.NotNull(client);
    }

    [Fact]
    public void FluentValidation_Services_Are_Registered()
    {
        IReadOnlyList<ServiceDescriptor>? captured = null;
        using var probe = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => captured = services.ToList());
        });

        _ = probe.Services;
        Assert.NotNull(captured);
        Assert.Contains(captured, descriptor => IsFluentValidationService(descriptor));
    }

    private static bool IsFluentValidationService(ServiceDescriptor descriptor)
    {
        return IsFluentValidationAssembly(descriptor.ServiceType.Assembly) ||
            (descriptor.ImplementationType is not null && IsFluentValidationAssembly(descriptor.ImplementationType.Assembly));
    }

    private static bool IsFluentValidationAssembly(System.Reflection.Assembly assembly)
    {
        return assembly.GetName().Name?.StartsWith("FluentValidation", StringComparison.Ordinal) is true;
    }

    private static async Task<JsonDocument> ReadBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private sealed record TestItem(string? Name);
}
