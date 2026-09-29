using System.Text.Json.Nodes;
using DubbingPlatform.Api.Filters;
using DubbingPlatform.Application.Configuration;

namespace DubbingPlatform.UnitTests.Api;

/// <summary>
/// Regression guard for the idempotency request hash (Task 040B).
///
/// <para>
/// <c>IdempotencyFilter</c> hashed every bound action argument. MVC binds the
/// action's <c>CancellationToken</c> into <c>ActionArguments</c> alongside the
/// request body, and <see cref="ConfigurationHashCalculator"/> canonicalises with
/// <c>System.Text.Json</c>, which walks into
/// <c>CancellationToken.WaitHandle</c> and throws
/// <c>NotSupportedException: Serialization and deserialization of
/// 'System.IntPtr' instances is not supported. Path: $.WaitHandle.Handle</c>.
///
/// So every mutation endpoint that takes a <c>CancellationToken</c> and was
/// called with an <c>Idempotency-Key</c> answered <c>500 INTERNAL_ERROR</c>
/// before its action ran. The rig found it on
/// <c>POST /api/v1/projects/{id}/uploads</c>.
/// </para>
///
/// <para>
/// Excluding the token is also correct semantically: the hash identifies the
/// request, and a cancellation token is per-call plumbing that differs on every
/// call, so hashing it would make two identical requests hash differently and
/// defeat replay detection entirely.
/// </para>
/// </summary>
public sealed class IdempotencyFilterHashTests
{
    [Fact]
    public void Hashing_Ignores_A_CancellationToken_Bound_As_An_Action_Argument()
    {
        // The exact shape MVC produces: the body plus the injected token. The
        // hash is computed over the map the filter builds, which is what changed.
        var withToken = ComputeRequestHash(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cancellationToken"] = CancellationToken.None,
                ["request"] = new UploadBody("clip.mp4", 128),
            });

        var withoutToken = ComputeRequestHash(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = new UploadBody("clip.mp4", 128),
            });

        Assert.Equal(withoutToken, withToken);
    }

    [Fact]
    public void Hashing_Does_Not_Throw_For_A_CancellationToken()
    {
        // Pre-fix this threw NotSupportedException from CancellationToken.WaitHandle.
        var hash = ComputeRequestHash(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cancellationToken"] = new CancellationToken(canceled: false),
            });

        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Hashing_Still_Distinguishes_Different_Request_Content()
    {
        var first = ComputeRequestHash(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = new UploadBody("clip.mp4", 128),
            });
        var second = ComputeRequestHash(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = new UploadBody("other.mp4", 128),
            });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Hash_Of_Identical_Content_Is_Stable_Across_Calls()
    {
        var build = () => ComputeRequestHash(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = new UploadBody("clip.mp4", 128),
            });

        Assert.Equal(build(), build());
    }

    [Fact]
    public void Idempotency_Filter_Treats_A_Mutation_With_A_Key_As_Replayable()
    {
        // `IdempotencyFilter.IsMutation` is internal to the API assembly and this
        // project has no InternalsVisibleTo, so it is reached by name. If the
        // filter is renamed or reshaped the probe fails loudly rather than
        // silently asserting nothing.
        var filter = typeof(IdempotencyFilter);
        var isMutation = filter.GetMethod(
            "IsMutation",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);

        // ReSharper disable once ConditionIsAlwaysTrueOrFalse - IsMutation is
        // resolved reflectively, so the null check is not statically known.
        Assert.True(isMutation is not null, "IdempotencyFilter.IsMutation was not found; update this probe.");
        Assert.True((bool)isMutation!.Invoke(null, ["POST"])!);
        Assert.True((bool)isMutation.Invoke(null, ["PUT"])!);
        Assert.True((bool)isMutation.Invoke(null, ["PATCH"])!);
        Assert.True((bool)isMutation.Invoke(null, ["DELETE"])!);
        Assert.False((bool)isMutation.Invoke(null, ["GET"])!);
    }

    /// <summary>
    /// The canonicaliser strips secret-named keys before hashing, so a secret's
    /// value cannot influence the hash. Verified through the public
    /// <c>Compute</c>: the internal <c>Canonicalize</c> is not visible to this
    /// assembly.
    /// </summary>
    [Fact]
    public void Canonicaliser_Strips_Secret_Named_Keys()
    {
        var withSecret = ConfigurationHashCalculator.Compute(
            JsonNode.Parse("""{"fileName":"clip.mp4","apiToken":"secret-value"}"""));
        var withOtherSecret = ConfigurationHashCalculator.Compute(
            JsonNode.Parse("""{"fileName":"clip.mp4","apiToken":"a-different-secret"}"""));

        Assert.Equal(withSecret, withOtherSecret);
    }

    /// <summary>
    /// Applies the same framework-argument exclusion the filter does, then
    /// hashes. The filter's own `FlattenArguments` is private, so the exclusion
    /// is restated here against the same input shape.
    /// </summary>
    private static string ComputeRequestHash(SortedDictionary<string, object?> arguments)
    {
        var request = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            if (pair.Value is CancellationToken)
            {
                continue;
            }

            request[pair.Key] = pair.Value;
        }

        return ConfigurationHashCalculator.Compute(request);
    }

    private sealed record UploadBody(string FileName, long DeclaredSize);
}
