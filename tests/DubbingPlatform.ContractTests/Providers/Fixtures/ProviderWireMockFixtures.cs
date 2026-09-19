using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DubbingPlatform.ContractTests.Providers.Fixtures;

/// <summary>
/// WireMock stub fixtures per provider family covering 429/timeout/malformed/
/// async lifecycle/duplicate/partial/expiry/quota without live paid calls.
/// Each method stubs the exact path the adapters call; bodies are minimal JSON
/// matching the adapter wire contracts (see adapter XML docs).
/// </summary>
internal static class ProviderWireMockFixtures
{
    public static void StubAzureTranscribe(WireMockServer server, int status, string body, string? retryAfter = null)
    {
        var builder = Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json");
        if (retryAfter is not null)
        {
            builder = builder.WithHeader("Retry-After", retryAfter);
        }

        server.Given(Request.Create().WithPath("/speech/recognition/transcribe*").UsingPost())
            .RespondWith(builder);
    }

    public static void StubAzureTranscribeDelay(WireMockServer server, TimeSpan delay)
    {
        server.Given(Request.Create().WithPath("/speech/recognition/transcribe*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"text":"slow","confidence":0.9,"words":[]}""").WithDelay(delay));
    }

    public static void StubAzureBatch(WireMockServer server, string statusBody)
    {
        server.Given(Request.Create().WithPath("/speech/batch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBody("""{"jobId":"az-job-1"}""").WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath("/speech/batch/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(statusBody).WithHeader("Content-Type", "application/json"));
    }

    public static void StubAzureTts(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/cognitiveservices/v1").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubAzureTranslate(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/translate*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubOpenAiTranscriptions(WireMockServer server, int status, string body, string? retryAfter = null)
    {
        var builder = Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json");
        if (retryAfter is not null)
        {
            builder = builder.WithHeader("Retry-After", retryAfter);
        }

        server.Given(Request.Create().WithPath("/audio/transcriptions").UsingPost())
            .RespondWith(builder);
    }

    public static void StubOpenAiTranscriptionsDelay(WireMockServer server, TimeSpan delay)
    {
        server.Given(Request.Create().WithPath("/audio/transcriptions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"text":"slow","confidence":0.9}""").WithDelay(delay));
    }

    public static void StubOpenAiBatch(WireMockServer server, string statusBody)
    {
        server.Given(Request.Create().WithPath("/audio/batch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202).WithBody("""{"jobId":"oai-job-1"}""").WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath("/audio/batch/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(statusBody).WithHeader("Content-Type", "application/json"));
    }

    public static void StubOpenAiChat(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubOpenAiTts(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/audio/speech").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubGoogleRecognize(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/v2/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubGoogleRecognizeDelay(WireMockServer server, TimeSpan delay)
    {
        server.Given(Request.Create().WithPath("/v2/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"results":[{"transcript":"slow","confidence":0.9}]}""").WithDelay(delay));
    }

    public static void StubGoogleTranslate(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/v3/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubGemini(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/v1/models/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubGoogleTts(WireMockServer server, int status, string body)
    {
        server.Given(Request.Create().WithPath("/v1/text:synthesize").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
    }

    public static void StubLocalInfer(WireMockServer server, int status, string body, TimeSpan? delay = null)
    {
        var builder = Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json");
        if (delay.HasValue)
        {
            builder = builder.WithDelay(delay.Value);
        }

        server.Given(Request.Create().WithPath("/infer").UsingPost())
            .RespondWith(builder);
    }

    public static void StubLocalHealth(WireMockServer server)
    {
        server.Given(Request.Create().WithPath("/health").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"status":"ok"}"""));
    }

    /// <summary>
    /// Explicit fast warmup stub (Task 044 stabilization). <c>WarmupAsync</c>
    /// tries <c>POST /warmup</c> first and falls back to <c>GET /health</c>
    /// on 404; leaving <c>/warmup</c> unstubbed makes warmup depend on
    /// WireMock's default unmatched-404 handling, which is timing-sensitive
    /// under parallel load. Stubbing it gives the timeout test a
    /// deterministic sub-100ms warmup so only <c>POST /infer</c> exercises
    /// the timeout path. Other local tests intentionally keep the
    /// health-fallback path covered by stubbing only <c>/health</c>.
    /// </summary>
    public static void StubLocalWarmup(WireMockServer server)
    {
        server.Given(Request.Create().WithPath("/warmup").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"status":"warmed"}""").WithHeader("Content-Type", "application/json"));
    }
}
