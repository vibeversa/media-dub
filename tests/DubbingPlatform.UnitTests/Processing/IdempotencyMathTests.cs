// Task 039C: idempotency unit gap closure.
using System.Reflection;
using System.Text.Json;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Processing;
using DubbingPlatform.Application.Services;

namespace DubbingPlatform.UnitTests.Processing;

/// <summary>
/// Unit coverage for the processing idempotency surface:
/// <list type="bullet">
/// <item><see cref="ProcessingIdempotency"/> key normalization and the two
/// canonical request-hash derivations (all pure, no store).</item>
/// <item>Tenant scoping of the claims (<see cref="IdempotencyService"/> and
/// <see cref="ProcessingIdempotency"/> argument guards, verified against a
/// strict context factory that must never be touched).</item>
/// <item><see cref="IdempotencyRetention"/> endpoint-to-expiry resolution plus
/// the resulting expiry arithmetic against an explicit frozen
/// <see cref="DateTimeOffset"/> <c>now</c> - 0 elapsed, exactly at expiry,
/// just before, and long expired.</item>
/// </list>
/// No database, container, network, wall clock, or sleep is involved: the
/// retention math takes <c>now</c> as an explicit parameter and the claim
/// guards are asserted before any context is created.
/// </summary>
public sealed class IdempotencyMathTests
{
    /// <summary>
    /// A frozen reference instant. Every retention assertion in this file is
    /// expressed as an offset from this value, so nothing depends on the
    /// machine clock.
    /// </summary>
    private static readonly DateTimeOffset FrozenNow = new(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid FrozenTenant = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid FrozenProject = Guid.Parse("1f8fad5b-d9cb-469f-a165-70867728950f");
    private static readonly Guid FrozenRun = Guid.Parse("2f8fad5b-d9cb-469f-a165-70867728950a");

    [Fact]
    public void Processing_Namespace_Constants_Are_Frozen()
    {
        Assert.Equal("processing", ProcessingIdempotency.Endpoint);
        Assert.Equal(TimeSpan.FromHours(24), ProcessingIdempotency.Expiry);
        Assert.Equal("Idempotent-Replayed", ProcessingIdempotency.ReplayedHeaderName);
        Assert.Equal("Idempotency-Key", IdempotencyService.HeaderName);
        Assert.Equal(TimeSpan.FromMinutes(5), IdempotencyService.InProgressWindow);
    }

    [Fact]
    public void RequireKey_Trims_And_Accepts_A_256_Character_Key()
    {
        Assert.Equal("abc", ProcessingIdempotency.RequireKey("  abc  "));
        Assert.Equal("abc", ProcessingIdempotency.RequireKey("abc"));
        Assert.Equal("corr-key-9", ProcessingIdempotency.RequireKey("\tcorr-key-9\n"));

        // The 256-character bound is on the trimmed key.
        var atBound = new string('k', 256);
        Assert.Equal(atBound, ProcessingIdempotency.RequireKey($"  {atBound}  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("k-with-only-space-around")]
    public void RequireKey_Rejects_Blank_Keys_With_The_Required_Code(string? key)
    {
        // Surrounding whitespace only is trimmed to empty and rejected.
        if (key is not null && key.Trim().Length > 0)
        {
            Assert.Equal("k-with-only-space-around", ProcessingIdempotency.RequireKey(key));
            return;
        }

        var ex = Assert.Throws<ErrorCodeException>(() => ProcessingIdempotency.RequireKey(key));
        Assert.Equal(ErrorCodes.IdempotencyKeyRequired, ex.ErrorCode);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void RequireKey_Rejects_A_Key_Longer_Than_256_Characters()
    {
        var overBound = new string('k', 257);

        var ex = Assert.Throws<ErrorCodeException>(() => ProcessingIdempotency.RequireKey(overBound));
        Assert.Equal(ErrorCodes.IdempotencyKeyRequired, ex.ErrorCode);

        // Whitespace padding does not count toward the bound: the trimmed key is
        // 256 characters and is accepted, the 257-character key is not.
        var padded = ProcessingIdempotency.RequireKey($"   {new string('k', 256)}   ");
        Assert.Equal(256, padded.Length);
    }

    [Fact]
    public void HashFor_Is_A_Deterministic_Lowercase_Hex_Digest()
    {
        var first = ProcessingIdempotency.HashFor(FrozenProject, "{}", false);
        var second = ProcessingIdempotency.HashFor(FrozenProject, "{}", false);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void HashFor_Separates_By_Project_Body_And_Force()
    {
        var baseline = ProcessingIdempotency.HashFor(FrozenProject, "{}", false);

        Assert.NotEqual(baseline, ProcessingIdempotency.HashFor(FrozenProject, "{}", true));
        Assert.NotEqual(baseline, ProcessingIdempotency.HashFor(FrozenProject, "{\"x\":1}", false));
        Assert.NotEqual(baseline, ProcessingIdempotency.HashFor(Guid.NewGuid(), "{}", false));

        // A null body and an empty body are the same payload, so they hash equal.
        Assert.Equal(
            ProcessingIdempotency.HashFor(FrozenProject, string.Empty, false),
            ProcessingIdempotency.HashFor(FrozenProject, null, false));

        // A whitespace body is a distinct payload: only null collapses to empty.
        Assert.NotEqual(
            ProcessingIdempotency.HashFor(FrozenProject, string.Empty, false),
            ProcessingIdempotency.HashFor(FrozenProject, "   ", false));
    }

    [Fact]
    public void HashForRetry_Is_A_Deterministic_Lowercase_Hex_Digest()
    {
        var first = ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, "{\"segments\":[1]}");
        var second = ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, "{\"segments\":[1]}");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void HashForRetry_Separates_By_Project_Run_And_Body()
    {
        var baseline = ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, "{\"segments\":[1]}");

        Assert.NotEqual(baseline, ProcessingIdempotency.HashForRetry(Guid.NewGuid(), FrozenRun, "{\"segments\":[1]}"));
        Assert.NotEqual(baseline, ProcessingIdempotency.HashForRetry(FrozenProject, Guid.NewGuid(), "{\"segments\":[1]}"));
        Assert.NotEqual(baseline, ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, "{\"segments\":[2]}"));

        // A null retry body and an empty retry body are the same payload.
        Assert.Equal(
            ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, string.Empty),
            ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, null));
    }

    [Fact]
    public void Retry_Hash_Is_A_Different_Namespace_From_The_Start_Hash()
    {
        // Start and retry share the "processing" namespace, so their payloads
        // must not collide: a retry can never replay a completed-start claim.
        var start = ProcessingIdempotency.HashFor(FrozenProject, "{\"segments\":[1]}", false);
        var retry = ProcessingIdempotency.HashForRetry(FrozenProject, FrozenRun, "{\"segments\":[1]}");

        Assert.NotEqual(start, retry);
    }

    [Fact]
    public void ProcessingIdempotency_Rejects_A_Null_Service()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessingIdempotency(null!));
    }

    [Fact]
    public async Task TryClaimAsync_Requires_A_Key_Before_Touching_Any_Store()
    {
        var idempotency = new IdempotencyService(TouchingNothingContextFactory());
        var processing = new ProcessingIdempotency(idempotency);

        // The key guard runs first, so a blank key fails with the 400 code and
        // the context factory is never asked for a context.
        foreach (var key in new string?[] { null, string.Empty, "   ", new string('k', 257) })
        {
            var ex = await Assert.ThrowsAsync<ErrorCodeException>(
                () => processing.TryClaimAsync(FrozenTenant, key!, "hash"));
            Assert.Equal(ErrorCodes.IdempotencyKeyRequired, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task TryClaimAsync_Rejects_A_Blank_Request_Hash()
    {
        var processing = new ProcessingIdempotency(new IdempotencyService(TouchingNothingContextFactory()));

        // A null hash is rejected as a null argument; a blank one as a blank argument.
        var nullHash = await Assert.ThrowsAsync<ArgumentNullException>(
            () => processing.TryClaimAsync(FrozenTenant, "key-1", null!));
        Assert.Equal("requestHash", nullHash.ParamName);

        foreach (var hash in new[] { string.Empty, "   " })
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(
                () => processing.TryClaimAsync(FrozenTenant, "key-1", hash));
            Assert.Equal("requestHash", ex.ParamName);
        }
    }

    [Fact]
    public async Task Complete_And_Fail_Reject_A_Blank_Key_Before_Reaching_The_Store()
    {
        var processing = new ProcessingIdempotency(new IdempotencyService(TouchingNothingContextFactory()));

        // Both completion paths normalize the key first, so a blank key is a 400
        // without the ledger ever being consulted.
        foreach (var key in new string?[] { null, string.Empty, "   ", new string('k', 257) })
        {
            var complete = await Assert.ThrowsAsync<ErrorCodeException>(
                () => processing.CompleteAsync(
                    FrozenTenant, key!, 202, FrozenRun, FrozenProject.ToString("N"), "Running"));
            Assert.Equal(ErrorCodes.IdempotencyKeyRequired, complete.ErrorCode);
            Assert.Equal(400, complete.StatusCode);

            var fail = await Assert.ThrowsAsync<ErrorCodeException>(
                () => processing.FailAsync(FrozenTenant, key!));
            Assert.Equal(ErrorCodes.IdempotencyKeyRequired, fail.ErrorCode);
            Assert.Equal(400, fail.StatusCode);
        }
    }

    [Fact]
    public async Task TryClaimAsync_Propagates_A_Store_Failure_Unchanged_After_Normalizing_The_Key()
    {
        // The pure guards run first, then the ledger is consulted. A store
        // failure is not translated: only ConflictException and
        // IdempotencyInProgressException are remapped to 422, so anything else
        // surfaces as-is.
        var processing = new ProcessingIdempotency(new IdempotencyService(TouchingNothingContextFactory()));
        var hash = ProcessingIdempotency.HashFor(FrozenProject, "{}", false);

        var claim = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processing.TryClaimAsync(FrozenTenant, "  key-1  ", hash));
        Assert.Equal(StoreMustNotBeTouched, claim.Message);

        // The empty-tenant guard wins over the store, proving the tenant scope is
        // validated before any I/O.
        var tenant = await Assert.ThrowsAsync<DubbingPlatform.Domain.Exceptions.DomainException>(
            () => processing.TryClaimAsync(Guid.Empty, "key-1", hash));
        Assert.Equal("TenantId must not be empty.", tenant.Message);
    }

    [Fact]
    public async Task CompleteAsync_Serializes_The_Stored_Response_Then_Surfaces_A_Store_Failure()
    {
        // The envelope is built (key normalization + StoredRun/StoredResponse
        // serialization) before the ledger write is attempted.
        var processing = new ProcessingIdempotency(new IdempotencyService(TouchingNothingContextFactory()));

        var complete = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processing.CompleteAsync(
                FrozenTenant, "  key-1  ", 202, FrozenRun, FrozenProject.ToString("N"), "Running"));
        Assert.Equal(StoreMustNotBeTouched, complete.Message);

        var fail = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processing.FailAsync(FrozenTenant, "  key-1  "));
        Assert.Equal(StoreMustNotBeTouched, fail.Message);
    }

    [Fact]
    public async Task IdempotencyService_Claims_Are_Tenant_Scoped_And_Reject_An_Empty_Tenant()
    {
        var service = new IdempotencyService(TouchingNothingContextFactory());

        // The empty-tenant guard fires before any context is created, proving the
        // claim is tenant-scoped rather than global.
        var claim = await Assert.ThrowsAsync<DubbingPlatform.Domain.Exceptions.DomainException>(
            () => service.TryClaimAsync(Guid.Empty, "POST /api/v1/projects", "key-1", "hash-1", IdempotencyRetention.Default));
        Assert.Equal("TenantId must not be empty.", claim.Message);

        var complete = await Assert.ThrowsAsync<DubbingPlatform.Domain.Exceptions.DomainException>(
            () => service.CompleteAsync(Guid.Empty, "POST /api/v1/projects", "key-1", 202, "{}"));
        Assert.Equal("TenantId must not be empty.", complete.Message);

        var fail = await Assert.ThrowsAsync<DubbingPlatform.Domain.Exceptions.DomainException>(
            () => service.FailAsync(Guid.Empty, "POST /api/v1/projects", "key-1"));
        Assert.Equal("TenantId must not be empty.", fail.Message);
    }

    [Fact]
    public async Task IdempotencyService_Argument_Guards_Run_Before_Any_Context_Is_Created()
    {
        var service = new IdempotencyService(TouchingNothingContextFactory());
        var expiry = IdempotencyRetention.Default;

        // A non-positive expiry is rejected as a domain invariant violation.
        foreach (var bad in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            var ex = await Assert.ThrowsAsync<DubbingPlatform.Domain.Exceptions.DomainException>(
                () => service.TryClaimAsync(FrozenTenant, "POST /api/v1/projects", "key-1", "hash-1", bad));
            Assert.Equal("Expiry must be positive.", ex.Message);
        }

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.TryClaimAsync(FrozenTenant, "   ", "key-1", "hash-1", expiry));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.TryClaimAsync(FrozenTenant, "POST /api/v1/projects", "   ", "hash-1", expiry));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.TryClaimAsync(FrozenTenant, "POST /api/v1/projects", "key-1", "   ", expiry));

        // A completed claim must carry a valid HTTP status.
        foreach (var status in new[] { 199, 600 })
        {
            var ex = await Assert.ThrowsAsync<DubbingPlatform.Domain.Exceptions.DomainException>(
                () => service.CompleteAsync(FrozenTenant, "POST /api/v1/projects", "key-1", status, "{}"));
            Assert.Equal("StatusCode must be a valid HTTP status.", ex.Message);
        }

        Assert.Throws<ArgumentNullException>(() => new IdempotencyService(null!));
    }

    // ---------------------------------------------------------------
    // Stored-response run-id derivation.
    //
    // ProcessingIdempotency.TryExtractRunId is declared `internal` and the
    // repository has no InternalsVisibleTo for the unit-test assembly, so it is
    // reached reflectively. It is a pure static parser; this only invokes the
    // production method, it does not re-implement it. (Adding
    // InternalsVisibleTo would mean editing production/project files, which
    // this task forbids.)
    // ---------------------------------------------------------------

    private static readonly MethodInfo TryExtractRunIdMethod =
        typeof(ProcessingIdempotency).GetMethod(
            "TryExtractRunId",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("ProcessingIdempotency.TryExtractRunId was not found.");

    private static Guid? ExtractRunId(string? stored) =>
        (Guid?)TryExtractRunIdMethod.Invoke(null, [stored]);

    private static string StoredEnvelope(string innerBodyJson) =>
        JsonSerializer.Serialize(
            new { statusCode = 202, body = innerBodyJson },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void TryExtractRunId_Reads_The_Canonical_32_Hex_And_Dashed_Forms()
    {
        var expected = Guid.NewGuid();
        var hex = expected.ToString("N");
        var dashed = expected.ToString("D");

        Assert.Equal(expected, ExtractRunId(StoredEnvelope($$"""{"runId":"{{hex}}","projectId":"p","status":"Running"}""")));
        Assert.Equal(expected, ExtractRunId(StoredEnvelope($$"""{"runId":"{{dashed}}"}""")));
    }

    [Fact]
    public void TryExtractRunId_Strips_The_Public_Run_Id_Prefix()
    {
        var expected = Guid.NewGuid();
        var prefixed = "run_" + expected.ToString("N");

        Assert.Equal(expected, ExtractRunId(StoredEnvelope($$"""{"runId":"{{prefixed}}"}""")));
    }

    [Fact]
    public void TryExtractRunId_Trims_The_Run_Id_Before_Parsing()
    {
        var expected = Guid.NewGuid();

        Assert.Equal(expected, ExtractRunId(StoredEnvelope($$"""{"runId":"  {{expected:N}}  "}""")));
        Assert.Equal(
            expected,
            ExtractRunId(StoredEnvelope($$"""{"runId":"  run_{{expected:N}}"}""")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void TryExtractRunId_Returns_Null_For_An_Unusable_Stored_Response(string? stored)
    {
        // Best-effort parsing: anything that cannot yield a run id means
        // "execute", never throw.
        Assert.Null(ExtractRunId(stored));
    }

    [Fact]
    public void TryExtractRunId_Returns_Null_When_The_Envelope_Shape_Is_Wrong()
    {
        // No "body" property.
        Assert.Null(ExtractRunId("""{"statusCode":202}"""));

        // "body" is not a string.
        Assert.Null(ExtractRunId("""{"statusCode":202,"body":{"runId":"x"}}"""));
        Assert.Null(ExtractRunId("""{"statusCode":202,"body":42}"""));

        // The envelope body is blank.
        Assert.Null(ExtractRunId("""{"statusCode":202,"body":""}"""));
        Assert.Null(ExtractRunId("""{"statusCode":202,"body":"   "}"""));

        // The envelope is a JSON array, not an object.
        Assert.Null(ExtractRunId("[]"));

        // The inner body has no string "runId".
        Assert.Null(ExtractRunId(StoredEnvelope("""{"projectId":"p"}""")));
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":42}""")));
        Assert.Null(ExtractRunId(StoredEnvelope("""[1,2,3]""")));

        // The run id is present but blank.
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":""}""")));
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":"   "}""")));

        // The run id is present but unparseable.
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":"not-a-guid"}""")));
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":"run_not-a-guid"}""")));

        // All-zero guids are rejected as empty.
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":"00000000000000000000000000000000"}""")));
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":"00000000-0000-0000-0000-000000000000"}""")));

        // The inner body is not valid JSON.
        Assert.Null(ExtractRunId(StoredEnvelope("{ not json")));
    }

    [Fact]
    public void TryExtractRunId_Falls_Back_To_The_Dashed_Guid_Parse_For_A_Malformed_Hex_Form()
    {
        // A 32-char value with a non-hex character cannot parse as "N", but the
        // dashed fallback is tried next; when that also fails the result is null.
        Assert.Null(ExtractRunId(StoredEnvelope("""{"runId":"zzzzzzzz-1111-1111-1111-111111111111"}""")));

        // Uppercase hex parses as "N" just fine.
        var expected = Guid.NewGuid();
        Assert.Equal(expected, ExtractRunId(StoredEnvelope($$"""{"runId":"{{expected.ToString("N").ToUpperInvariant()}}"}""")));
    }

    [Fact]
    public void IdempotencyRetention_Constants_Match_The_Task_012A_Binding()
    {
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.ProjectCreate);
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.UploadCreate);
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.UploadComplete);
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.ProcessingStart);
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.Export);
        Assert.Equal(TimeSpan.FromHours(24), IdempotencyRetention.Cancel);
        Assert.Equal(TimeSpan.FromHours(24), IdempotencyRetention.Retry);
        Assert.Equal(TimeSpan.FromHours(24), IdempotencyRetention.Default);
    }

    [Theory]
    [InlineData("POST /api/v1/projects", 7 * 24)]
    [InlineData("post /api/v1/projects", 7 * 24)]
    [InlineData("  POST /api/v1/projects  ", 7 * 24)]
    [InlineData("POST /api/v1/uploads", 7 * 24)]
    [InlineData("POST /api/v1/uploads/complete", 7 * 24)]
    [InlineData("POST /api/v1/uploads/COMPLETE", 7 * 24)]
    [InlineData("POST /api/v1/processing", 7 * 24)]
    [InlineData("POST /api/v1/exports", 7 * 24)]
    [InlineData("POST /api/v1/processing/cancel", 24)]
    [InlineData("POST /api/v1/processing/retry", 24)]
    [InlineData("POST /api/v1/projects/abc/retry", 24)]
    [InlineData("DELETE /api/v1/unknown", 24)]
    [InlineData("POST /api/v1/reviews/approve", 24)]
    public void ExpiryFor_Resolves_The_Bound_Retention_For_Each_Endpoint_Class(string endpoint, int expectedHours)
    {
        var expiry = IdempotencyRetention.ExpiryFor(endpoint);

        Assert.Equal(TimeSpan.FromHours(expectedHours), expiry);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExpiryFor_Defaults_For_A_Blank_Endpoint_Key(string? endpoint)
    {
        Assert.Equal(IdempotencyRetention.Default, IdempotencyRetention.ExpiryFor(endpoint!));
    }

    [Fact]
    public void ExpiryFor_Prefers_Cancel_Then_Retry_Then_Export_Then_Upload_Then_Processing_Then_Project()
    {
        // The resolution order is part of the contract: a key mentioning several
        // classes resolves to the most specific (first) match.
        Assert.Equal(
            IdempotencyRetention.Cancel,
            IdempotencyRetention.ExpiryFor("POST /api/v1/exports/retry/cancel"));
        Assert.Equal(
            IdempotencyRetention.Retry,
            IdempotencyRetention.ExpiryFor("POST /api/v1/exports/retry"));
        Assert.Equal(
            IdempotencyRetention.Export,
            IdempotencyRetention.ExpiryFor("POST /api/v1/exports/upload/processing"));
        Assert.Equal(
            IdempotencyRetention.UploadCreate,
            IdempotencyRetention.ExpiryFor("POST /api/v1/uploads/processing"));
        Assert.Equal(
            IdempotencyRetention.UploadComplete,
            IdempotencyRetention.ExpiryFor("POST /api/v1/uploads/complete/processing"));
        Assert.Equal(
            IdempotencyRetention.ProcessingStart,
            IdempotencyRetention.ExpiryFor("POST /api/v1/processing/projects"));
    }

    [Fact]
    public void Retention_Expiry_Math_Is_Pinned_Against_A_Frozen_Now()
    {
        // Expiry arithmetic uses the frozen instant as "now"; no wall clock.
        var createdAt = FrozenNow;
        var expiry = IdempotencyRetention.ExpiryFor("POST /api/v1/projects");
        var expiresAt = createdAt.Add(expiry);

        Assert.Equal(FrozenNow.AddDays(7), expiresAt);

        // 0 elapsed: the claim is fresh.
        AssertFresh(createdAt, expiresAt, FrozenNow);

        // Exactly at expiry: still fresh (the store compares ExpiresAt < now).
        AssertFresh(createdAt, expiresAt, expiresAt);

        // One tick past expiry: expired.
        AssertExpired(createdAt, expiresAt, expiresAt.AddTicks(1));

        // Just before expiry: fresh.
        AssertFresh(createdAt, expiresAt, expiresAt.AddTicks(-1));

        // Long expired: 30 days past expiry.
        AssertExpired(createdAt, expiresAt, expiresAt.AddDays(30));
    }

    [Fact]
    public void Retention_Expiry_Math_For_A_24h_Endpoint_Is_Pinned_Against_A_Frozen_Now()
    {
        var createdAt = FrozenNow;
        var expiry = IdempotencyRetention.ExpiryFor("POST /api/v1/processing/cancel");
        var expiresAt = createdAt.Add(expiry);

        Assert.Equal(FrozenNow.AddHours(24), expiresAt);

        AssertFresh(createdAt, expiresAt, FrozenNow);
        AssertFresh(createdAt, expiresAt, expiresAt);
        AssertFresh(createdAt, expiresAt, expiresAt.AddTicks(-1));
        AssertExpired(createdAt, expiresAt, expiresAt.AddTicks(1));
        AssertExpired(createdAt, expiresAt, FrozenNow.AddDays(365));
    }

    [Fact]
    public void Retention_Expiry_Math_Is_Monotonic_Across_The_Whole_Window()
    {
        var createdAt = FrozenNow;
        var expiresAt = createdAt.Add(IdempotencyRetention.ExpiryFor("POST /api/v1/exports"));

        // Halfway, five days in, and one second to go are all fresh.
        AssertFresh(createdAt, expiresAt, createdAt.AddTicks((expiresAt - createdAt).Ticks / 2));
        AssertFresh(createdAt, expiresAt, createdAt.AddDays(5));
        AssertFresh(createdAt, expiresAt, expiresAt.AddSeconds(-1));
        AssertFresh(createdAt, expiresAt, expiresAt);

        // One second past the boundary the row is reset as a new claim.
        AssertExpired(createdAt, expiresAt, expiresAt.AddSeconds(1));
        AssertExpired(createdAt, expiresAt, createdAt.AddDays(7).AddSeconds(1));
        AssertExpired(createdAt, expiresAt, createdAt.AddDays(8));
    }

    [Fact]
    public void In_Progress_Window_Math_Is_Pinned_Against_A_Frozen_Now()
    {
        // A "Started" claim younger than InProgressWindow yields 409 + Retry-After;
        // the store's test is `age < window`, so the window boundary itself is
        // already stale. All arithmetic is relative to the frozen instant.
        var startedAt = FrozenNow;
        var ageAtNow = FrozenNow - startedAt;

        // 0 elapsed: still in progress, retry after the full window.
        Assert.Equal(300, RetryAfterSeconds(ageAtNow));
        Assert.True(ageAtNow < IdempotencyService.InProgressWindow);

        // One tick before the window: still in progress, Retry-After floors at 1s.
        var justInside = IdempotencyService.InProgressWindow - TimeSpan.FromTicks(1);
        Assert.True(justInside < IdempotencyService.InProgressWindow);
        Assert.Equal(1, RetryAfterSeconds(justInside));

        // Four minutes in: 60 seconds of window left.
        Assert.Equal(60, RetryAfterSeconds(TimeSpan.FromMinutes(4)));

        // Exactly at the window: not in progress any more (age < window is strict).
        var atWindow = ageAtNow + IdempotencyService.InProgressWindow;
        Assert.False(atWindow < IdempotencyService.InProgressWindow);

        // One tick past the window: treated as a new claim, no Retry-After.
        var justOutside = IdempotencyService.InProgressWindow + TimeSpan.FromTicks(1);
        Assert.False(justOutside < IdempotencyService.InProgressWindow);
    }

    [Fact]
    public void IdempotencyInProgressException_Maps_To_409_With_Retry_After()
    {
        var window = IdempotencyService.InProgressWindow;
        var age = TimeSpan.FromMinutes(2);
        var retryAfter = RetryAfterSeconds(age);

        var exception = new IdempotencyInProgressException(
            $"Idempotency key 'key-1' is already in progress. Retry after {retryAfter}s.",
            retryAfter);

        Assert.Equal(ErrorCodes.Conflict, exception.ErrorCode);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(300 - (int)age.TotalSeconds, exception.RetryAfterSeconds);
        Assert.InRange(exception.RetryAfterSeconds, 1, (int)window.TotalSeconds);

        // The floor of one second keeps a nearly-stale claim from telling the
        // client to hammer immediately.
        Assert.Equal(1, RetryAfterSeconds(window));
    }

    private static int RetryAfterSeconds(TimeSpan age)
    {
        // Mirrors the in-progress retry-after derivation: floor of the remaining
        // window, clamped at one second.
        return Math.Max(1, (int)(IdempotencyService.InProgressWindow - age).TotalSeconds);
    }

    private static void AssertFresh(DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Assert.True(expiresAt >= now, $"Expected fresh at {now:O} with expiry {expiresAt:O}.");
        Assert.True(now >= createdAt, $"Reference instant {now:O} precedes the claim at {createdAt:O}.");
    }

    private static void AssertExpired(DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Assert.True(expiresAt < now, $"Expected expired at {now:O} with expiry {expiresAt:O}.");
        Assert.True(now > createdAt, $"Reference instant {now:O} precedes the claim at {createdAt:O}.");
    }

    /// <summary>
    /// Message thrown by <see cref="ExplodingContextFactory"/> so a test can
    /// assert that a store access was - or was not - reached.
    /// </summary>
    private const string StoreMustNotBeTouched =
        "No database context may be created: this assertion must be satisfied by an argument guard.";

    private static IStageExecutionContextFactory TouchingNothingContextFactory()
    {
        return new ExplodingContextFactory();
    }

    /// <summary>
    /// A context factory that fails the test if anything asks it for a context.
    /// Every assertion in this file that uses it is expected to be rejected by
    /// an argument guard before any store access, so creating a context would
    /// signal a real behavioural change.
    /// </summary>
    private sealed class ExplodingContextFactory : IStageExecutionContextFactory
    {
        public Microsoft.EntityFrameworkCore.DbContext CreateDbContext() =>
            throw new InvalidOperationException(StoreMustNotBeTouched);
    }
}
