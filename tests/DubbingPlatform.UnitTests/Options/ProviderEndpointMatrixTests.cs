// Task 039C: options validation unit gap closure.

using DubbingPlatform.Application.Options;

namespace DubbingPlatform.UnitTests.Options;

/// <summary>
/// End-to-end matrix for <see cref="ProviderEndpointValidator"/>, the shared
/// BaseUrl allowlist used by the Azure/Google/OpenAI/local-inference provider
/// options validators and adapters. Covers every allow and deny branch the
/// helper actually implements: https anywhere, http only for loopback hosts,
/// and denial of blank, non-absolute, non-http(s) and cleartext public hosts.
/// <para>
/// Pure in-process URL-shape checks over <see cref="Uri"/> values - no DNS
/// resolution, no socket, no outbound call of any kind. All host names are
/// reserved-for-testing style placeholders.
/// </para>
/// </summary>
public sealed class ProviderEndpointMatrixTests
{
    // https is allowed for any host, scheme comparison is case-insensitive.
    [Theory]
    [InlineData("https://api.example.test/v1")]
    [InlineData("HTTPS://API.EXAMPLE.TEST/v1")]
    [InlineData("https://sidecar.example.test:8443/")]
    [InlineData("  https://api.example.test/v1  ")]
    [InlineData("https://api.example.test/v1?query=1#frag")]
    public void IsAllowed_Accepts_Any_Https_Endpoint(string url)
    {
        Assert.True(ProviderEndpointValidator.IsAllowed(url));
    }

    // http is allowed only for loopback hosts (WireMock-style local test doubles).
    [Theory]
    [InlineData("http://localhost:9091/v1")]
    [InlineData("http://LOCALHOST:9091/v1")]
    [InlineData("http://LocalHost")]
    [InlineData("http://127.0.0.1:9091/v1")]
    [InlineData("http://[::1]:9091/v1")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:9091/v1")]
    [InlineData("  http://localhost:9091/  ")]
    public void IsAllowed_Accepts_Http_Only_For_Loopback_Hosts(string url)
    {
        Assert.True(ProviderEndpointValidator.IsAllowed(url));
    }

    // http to a non-loopback host stays cleartext to the public internet: denied.
    [Theory]
    [InlineData("http://api.example.test/v1")]
    [InlineData("http://10.0.0.5:8000")]
    [InlineData("http://192.168.1.10")]
    [InlineData("http://local-inference:8000")] // in-cluster sidecar, not a provider BaseUrl
    [InlineData("http://localhost.example.test:8000")]
    [InlineData("http://127.0.0.1.example.test:8000")]
    public void IsAllowed_Rejects_Cleartext_NonLoopback_Hosts(string url)
    {
        Assert.False(ProviderEndpointValidator.IsAllowed(url));
    }

    // Non-http(s) schemes are denied even on loopback.
    [Theory]
    [InlineData("ftp://files.example.test")]
    [InlineData("ftps://files.example.test")]
    [InlineData("ws://localhost:9091")]
    [InlineData("file://host/share")]
    [InlineData("gopher://localhost")]
    [InlineData("localhost:9091")] // no scheme
    public void IsAllowed_Rejects_NonHttp_Schemes(string url)
    {
        Assert.False(ProviderEndpointValidator.IsAllowed(url));
    }

    // Relative / non-absolute values cannot be parsed as an absolute URI.
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("api.example.test/v1")]
    [InlineData("/v1")]
    [InlineData("example.test")]
    public void IsAllowed_Rejects_NonAbsolute_Values(string url)
    {
        Assert.False(ProviderEndpointValidator.IsAllowed(url));
    }

    // Null, empty and whitespace-only values are denied before URI parsing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void IsAllowed_Rejects_Blank_Values(string? url)
    {
        Assert.False(ProviderEndpointValidator.IsAllowed(url));
    }

    [Fact]
    public void RequireAllowed_Returns_Trimmed_Endpoint_Without_Trailing_Slash()
    {
        Assert.Equal(
            "https://api.example.test/v1",
            ProviderEndpointValidator.RequireAllowed("https://api.example.test/v1/", "BaseUrl"));

        Assert.Equal(
            "http://localhost:9091/v1",
            ProviderEndpointValidator.RequireAllowed("  http://localhost:9091/v1/  ", "BaseUrl"));

        Assert.Equal(
            "https://api.example.test",
            ProviderEndpointValidator.RequireAllowed("https://api.example.test", "Azure:TranslatorBaseUrl"));
    }

    [Theory]
    [InlineData("http://api.example.test/v1", "OpenAiProviderOptions.BaseUrl")]
    [InlineData("not-a-url", "OpenAiProviderOptions.BaseUrl")]
    [InlineData("", "GoogleProviderOptions.BaseUrl")]
    [InlineData("   ", "AzureProviderOptions.SpeechBaseUrl")]
    public void RequireAllowed_Throws_ArgumentException_Naming_The_Property(string? url, string property)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => ProviderEndpointValidator.RequireAllowed(url, property));

        Assert.Equal(property, exception.ParamName);
        Assert.Contains("is not allowed", exception.Message);
    }

    [Fact]
    public void Consumer_Validators_Surface_The_Shared_Allowlist_Decision()
    {
        // Same predicate, reached through each provider options validator, so the
        // allowlist cannot drift away from its consumers.
        Assert.True(ProviderEndpointValidator.IsAllowed("https://api.example.test/v1"));
        Assert.True(ProviderEndpointValidator.IsAllowed("http://localhost:9091/v1"));
        Assert.False(ProviderEndpointValidator.IsAllowed("http://api.example.test/v1"));

        Assert.True(LocalInferenceOptions.IsSidecarEndpointAllowed("https://sidecar.example.test:8443"));
        Assert.True(LocalInferenceOptions.IsSidecarEndpointAllowed("http://localhost:8081"));
        Assert.True(LocalInferenceOptions.IsSidecarEndpointAllowed("http://local-inference:8000"));
        Assert.False(LocalInferenceOptions.IsSidecarEndpointAllowed("http://api.example.test/v1"));
    }
}
