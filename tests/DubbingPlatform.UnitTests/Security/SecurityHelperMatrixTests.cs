// Task 039C: exports unit gap closure (security helper area).
using DubbingPlatform.Application.Security;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Security;

/// <summary>
/// Redis key conventions. Every key is tenant-scoped and leads with the
/// tenant id, so a prefix scan or a hand-built key can never cross tenants.
/// </summary>
public sealed class RedisKeysMatrixTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid Project = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    [Fact]
    public void ProgressSegment_Segment_Is_Frozen()
    {
        Assert.Equal("progress", RedisKeys.ProgressSegment);
    }

    [Fact]
    public void TenantKey_Leads_With_The_Tenant_And_Trims_Rest()
    {
        var key = RedisKeys.TenantKey(TenantA, "  progress:xyz  ");

        Assert.Equal(TenantA.ToString("N") + ":progress:xyz", key);
        Assert.StartsWith(TenantA.ToString("N") + ":", key, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantKey_Rejects_Empty_Tenant()
    {
        var ex = Assert.Throws<DomainException>(() => RedisKeys.TenantKey(Guid.Empty, "progress:x"));
        Assert.Equal("TenantId must not be empty.", ex.Message);
    }

    [Fact]
    public void TenantKey_Rejects_Blank_Rest()
    {
        Assert.Throws<ArgumentException>(() => RedisKeys.TenantKey(TenantA, "   "));
        Assert.Throws<ArgumentException>(() => RedisKeys.TenantKey(TenantA, string.Empty));
        Assert.Throws<ArgumentNullException>(() => RedisKeys.TenantKey(TenantA, null!));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("progress:one progress:two")]
    public void TenantKey_Rejects_Whitespace_In_The_Suffix(string rest)
    {
        var ex = Assert.Throws<DomainException>(() => RedisKeys.TenantKey(TenantA, rest));
        Assert.Equal("Redis key rest must not contain whitespace.", ex.Message);
    }

    [Fact]
    public void TenantKey_Whitespace_Check_Runs_After_Trimming()
    {
        // Padding around the suffix is allowed; whitespace inside it is not.
        Assert.Equal(
            TenantA.ToString("N") + ":progress:abc",
            RedisKeys.TenantKey(TenantA, "\tprogress:abc\n"));
        Assert.Throws<DomainException>(() => RedisKeys.TenantKey(TenantA, "progress:a b"));
    }

    [Fact]
    public void ProgressKey_Shape_Is_Tenant_First()
    {
        var key = RedisKeys.ProgressKey(TenantA, Project);

        Assert.Equal(
            TenantA.ToString("N") + ":progress:" + Project.ToString("N"),
            key);
    }

    [Fact]
    public void ProgressKey_Rejects_Empty_Project()
    {
        var ex = Assert.Throws<DomainException>(() => RedisKeys.ProgressKey(TenantA, Guid.Empty));
        Assert.Equal("ProjectId must not be empty.", ex.Message);
    }

    [Fact]
    public void ProgressKey_Rejects_Empty_Tenant_Via_TenantKey()
    {
        // ProjectId is checked first inside ProgressKey; a valid project then
        // flows into TenantKey, which rejects the empty tenant.
        Assert.Throws<DomainException>(() => RedisKeys.ProgressKey(Guid.Empty, Project));
    }

    [Fact]
    public void Tenant_Isolation_Key_For_A_Never_Equals_Key_For_B()
    {
        var keyA = RedisKeys.ProgressKey(TenantA, Project);
        var keyB = RedisKeys.ProgressKey(TenantB, Project);

        Assert.NotEqual(keyA, keyB);
        Assert.StartsWith(TenantA.ToString("N") + ":", keyA, StringComparison.Ordinal);
        Assert.StartsWith(TenantB.ToString("N") + ":", keyB, StringComparison.Ordinal);
        Assert.False(keyA.StartsWith(TenantB.ToString("N"), StringComparison.Ordinal));
        Assert.False(keyB.StartsWith(TenantA.ToString("N"), StringComparison.Ordinal));

        // Identical everything except the tenant.
        Assert.Equal(
            keyA.Substring(TenantA.ToString("N").Length + 1),
            keyB.Substring(TenantB.ToString("N").Length + 1));
    }

    [Fact]
    public void IsTenantScoped_Is_True_Only_For_The_Owning_Tenant()
    {
        var keyA = RedisKeys.ProgressKey(TenantA, Project);
        var keyB = RedisKeys.ProgressKey(TenantB, Project);

        Assert.True(RedisKeys.IsTenantScoped(keyA, TenantA));
        // Negative: tenant B must never claim tenant A's key.
        Assert.False(RedisKeys.IsTenantScoped(keyA, TenantB));
        Assert.False(RedisKeys.IsTenantScoped(keyB, TenantA));
        Assert.True(RedisKeys.IsTenantScoped(keyB, TenantB));
    }

    [Fact]
    public void IsTenantScoped_Requires_The_Colon_Delimiter()
    {
        // A bare tenant id, a longer id that merely starts with the tenant, and
        // a suffix-embedded tenant must all be rejected.
        Assert.False(RedisKeys.IsTenantScoped(TenantA.ToString("N"), TenantA));
        Assert.False(RedisKeys.IsTenantScoped(TenantA.ToString("N") + "x:progress", TenantA));
        Assert.False(RedisKeys.IsTenantScoped("progress:" + TenantA.ToString("N"), TenantA));
        Assert.False(RedisKeys.IsTenantScoped("PROGRESS", TenantA));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsTenantScoped_Fails_Closed_On_Empty_Key(string? key)
    {
        Assert.False(RedisKeys.IsTenantScoped(key, TenantA));
    }

    [Fact]
    public void IsTenantScoped_Fails_Closed_On_Empty_Tenant()
    {
        var key = RedisKeys.ProgressKey(TenantA, Project);

        Assert.False(RedisKeys.IsTenantScoped(key, Guid.Empty));
        Assert.False(RedisKeys.IsTenantScoped(null, Guid.Empty));
    }

    [Fact]
    public void Prefix_Scan_By_Tenant_Never_Overlaps()
    {
        // "tenantA:" is not a prefix of "tenantB:" and vice versa because both
        // are fixed 32-hex ids, so eviction/metrics scoped by prefix is safe.
        var keys = new[]
        {
            RedisKeys.ProgressKey(TenantA, Project),
            RedisKeys.ProgressKey(TenantB, Project),
        };

        var aOnly = keys.Where(k => RedisKeys.IsTenantScoped(k, TenantA)).ToArray();
        var bOnly = keys.Where(k => RedisKeys.IsTenantScoped(k, TenantB)).ToArray();

        Assert.Single(aOnly);
        Assert.Single(bOnly);
        Assert.Equal(keys[0], aOnly[0]);
        Assert.Equal(keys[1], bOnly[0]);
        Assert.Empty(aOnly.Intersect(bOnly, StringComparer.Ordinal));
    }
}

/// <summary>
/// Secret classification and redaction. Fake credentials only; the assertions
/// are "the value is gone", never "the value looks redacted".
/// </summary>
public sealed class SecretRedactorMatrixTests
{
    [Fact]
    public void Redacted_Marker_Is_Frozen()
    {
        Assert.Equal("[REDACTED]", SecretRedactor.RedactedValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_Passes_Through_Null_And_Empty(string? value)
    {
        Assert.Equal(value, SecretRedactor.Redact(value));
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("SECRET")]
    [InlineData("Secret")]
    [InlineData("clientSecret")]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("user_password")]
    [InlineData("accessToken")]
    [InlineData("refresh_token")]
    [InlineData("apiKey")]
    [InlineData("API_KEY")]
    [InlineData("privateKey")]
    [InlineData("credentials")]
    [InlineData("awsCredentialProvider")]
    public void IsSensitiveKey_Denotes_Secret_Material(string name)
    {
        Assert.True(SecretRedactor.IsSensitiveKey(name));
    }

    [Theory]
    [InlineData("route")]
    [InlineData("correlationId")]
    [InlineData("tenantId")]
    [InlineData("projectId")]
    [InlineData("endpoint")]
    [InlineData("timeoutSeconds")]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("status")]
    // The free-text pattern also knows "passwd"/"pwd", but the key classifier
    // does not: only secret/password/token/key/credential count as a secret
    // property. Pinned deliberately so the two definitions cannot drift.
    [InlineData("passwd")]
    [InlineData("pwd")]
    public void IsSensitiveKey_Ignores_Non_Secret_Names(string name)
    {
        Assert.False(SecretRedactor.IsSensitiveKey(name));
    }

    [Fact]
    public void IsSensitiveKey_Is_Substring_Based_And_Over_Broad_On_Purpose()
    {
        // "key" as a substring wins: fail closed over leaking.
        Assert.True(SecretRedactor.IsSensitiveKey("monkey"));
        Assert.True(SecretRedactor.IsSensitiveKey("keyboardLayout"));
        Assert.True(SecretRedactor.IsSensitiveKey("publicKeyPem"));
    }

    [Theory]
    [InlineData("api_key=sk-test-DO-NOT-USE")]
    [InlineData("API_KEY=sk-test-DO-NOT-USE")]
    [InlineData("apiKey:sk-test-DO-NOT-USE")]
    [InlineData("password=sk-test-DO-NOT-USE")]
    [InlineData("client_secret = sk-test-DO-NOT-USE")]
    [InlineData("token=sk-test-DO-NOT-USE")]
    [InlineData("credential=sk-test-DO-NOT-USE")]
    public void Redact_Removes_Assignment_Values(string input)
    {
        var redacted = SecretRedactor.Redact(input);

        Assert.NotNull(redacted);
        Assert.DoesNotContain("sk-test-DO-NOT-USE", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Handles_Quoted_Values()
    {
        var redacted = SecretRedactor.Redact("password=\"sk-test-DO-NOT-USE\"");

        Assert.NotNull(redacted);
        Assert.DoesNotContain("sk-test-DO-NOT-USE", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Never_Reintroduces_A_Secret_When_Applied_Repeatedly()
    {
        var once = SecretRedactor.Redact("api_key=sk-test-DO-NOT-USE");
        Assert.Equal("api_key=[REDACTED]", once);

        // Further passes keep the secret gone (the ']' marker is not re-matched,
        // which is why the loop checks for the value rather than full equality).
        var current = once;
        for (var i = 0; i < 3; i++)
        {
            current = SecretRedactor.Redact(current);
            Assert.NotNull(current);
            Assert.DoesNotContain("sk-test-DO-NOT-USE", current, StringComparison.Ordinal);
        }

        Assert.StartsWith("api_key=[REDACTED]", current, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Removes_Bearer_Tokens()
    {
        var redacted = SecretRedactor.Redact("Authorization: Bearer sk-test.DO-NOT-USE.1234");

        Assert.NotNull(redacted);
        Assert.DoesNotContain("sk-test.DO-NOT-USE.1234", redacted, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Handles_Multiple_Secrets_In_One_Line()
    {
        var redacted = SecretRedactor.Redact(
            "api_key=sk-test-DO-NOT-USE password=hunter2-DO-NOT-USE correlationId=abc-123");

        Assert.NotNull(redacted);
        Assert.DoesNotContain("sk-test-DO-NOT-USE", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-DO-NOT-USE", redacted, StringComparison.Ordinal);
        Assert.Contains("correlationId=abc-123", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Reaches_Bracketed_And_Nested_Keys()
    {
        // Dotted and bracketed key paths are part of the matched key charset, so
        // secrets assigned through a nested configuration are still removed.
        var redacted = SecretRedactor.Redact(
            "Config[apiKey]=sk-test-DO-NOT-USE db.password=hunter2-DO-NOT-USE host=localhost");

        Assert.NotNull(redacted);
        Assert.DoesNotContain("sk-test-DO-NOT-USE", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-DO-NOT-USE", redacted, StringComparison.Ordinal);
        Assert.Contains("host=localhost", redacted, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(redacted, SecretRedactor.RedactedValue));
    }

    [Theory]
    [InlineData("correlationId=abc-123")]
    [InlineData("route=/api/v1/projects")]
    [InlineData("no secrets here")]
    [InlineData("status=Completed")]
    [InlineData("plain text without assignments")]
    public void Redact_Leaves_Unknown_Keys_Alone(string input)
    {
        Assert.Equal(input, SecretRedactor.Redact(input));
    }

    [Fact]
    public void RedactDetails_Null_Yields_Empty_Dictionary()
    {
        var redacted = SecretRedactor.RedactDetails(null);

        Assert.NotNull(redacted);
        Assert.Empty(redacted);
    }

    [Fact]
    public void RedactDetails_Redacts_Sensitive_Keys_And_Keeps_The_Rest()
    {
        var redacted = SecretRedactor.RedactDetails(
        [
            new KeyValuePair<string, object?>("ApiKey", "sk-test-DO-NOT-USE"),
            new KeyValuePair<string, object?>("Password", "hunter2-DO-NOT-USE"),
            new KeyValuePair<string, object?>("AccessToken", "tok-DO-NOT-USE"),
            new KeyValuePair<string, object?>("CorrelationId", "corr-1"),
            new KeyValuePair<string, object?>("TenantId", "aaaaaaaa-1111-1111-1111-111111111111"),
        ]);

        Assert.Equal(5, redacted.Count);
        Assert.Equal("[REDACTED]", redacted["ApiKey"]);
        Assert.Equal("[REDACTED]", redacted["Password"]);
        Assert.Equal("[REDACTED]", redacted["AccessToken"]);
        Assert.Equal("corr-1", redacted["CorrelationId"]);
        Assert.Equal("aaaaaaaa-1111-1111-1111-111111111111", redacted["TenantId"]);
    }

    [Fact]
    public void RedactDetails_Redacts_Even_When_The_Value_Is_Null()
    {
        var redacted = SecretRedactor.RedactDetails([new KeyValuePair<string, object?>("Secret", null)]);

        Assert.Equal("[REDACTED]", redacted["Secret"]);
    }

    [Fact]
    public void RedactDetails_Unknown_Keys_Keep_Null_Values()
    {
        var redacted = SecretRedactor.RedactDetails([new KeyValuePair<string, object?>("Route", null)]);

        Assert.True(redacted.ContainsKey("Route"));
        Assert.Null(redacted["Route"]);
    }

    [Fact]
    public void RedactDetails_Uses_Ordinal_Key_Comparison()
    {
        var redacted = SecretRedactor.RedactDetails(
        [
            new KeyValuePair<string, object?>("ApiKey", "sk-test-DO-NOT-USE"),
            new KeyValuePair<string, object?>("apikey", "sk-test-DO-NOT-USE"),
        ]);

        // Two distinct ordinal keys, both classified as secret.
        Assert.Equal(2, redacted.Count);
        Assert.Equal("[REDACTED]", redacted["ApiKey"]);
        Assert.Equal("[REDACTED]", redacted["apikey"]);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

/// <summary>
/// <see cref="SecretPolicy"/> shares one definition of "secret" with the
/// redactor and fails closed at hash-input construction sites.
/// </summary>
public sealed class SecretPolicyMatrixTests
{
    [Theory]
    [InlineData("apiKey", true)]
    [InlineData("PASSWORD", true)]
    [InlineData("client_secret", true)]
    [InlineData("correlationId", false)]
    [InlineData("route", false)]
    [InlineData("passwd", false)]
    public void IsSecretProperty_Mirrors_The_Redactor(string name, bool expected)
    {
        Assert.Equal(expected, SecretPolicy.IsSecretProperty(name));
        Assert.Equal(SecretRedactor.IsSensitiveKey(name), expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsSecretProperty_Rejects_Blank_Names(string name)
    {
        Assert.Throws<ArgumentException>(() => SecretPolicy.IsSecretProperty(name));
    }

    [Fact]
    public void IsSecretProperty_Rejects_Null_Names()
    {
        Assert.Throws<ArgumentNullException>(() => SecretPolicy.IsSecretProperty(null!));
    }

    [Fact]
    public void FindSecretNames_Rejects_Null_Input()
    {
        Assert.Throws<ArgumentNullException>(() => SecretPolicy.FindSecretNames(null!));
    }

    [Fact]
    public void FindSecretNames_Selects_Only_Secret_Names()
    {
        var found = SecretPolicy.FindSecretNames(
            ["tenantId", "ApiKey", "route", "password", "count", "apiKey"]);

        Assert.Equal(3, found.Count);
        Assert.Equal("ApiKey", found[0]);
        Assert.Equal("password", found[1]);
        Assert.Equal("apiKey", found[2]);
    }

    [Fact]
    public void FindSecretNames_Skips_Null_And_Blank_Entries()
    {
        var found = SecretPolicy.FindSecretNames(["", "   ", "apiKey", null!]);

        Assert.Single(found);
        Assert.Equal("apiKey", found[0]);
    }

    [Fact]
    public void FindSecretNames_Empty_For_All_Safe_Names()
    {
        Assert.Empty(SecretPolicy.FindSecretNames(["tenantId", "route", "projectId", "pageSize"]));
    }

    [Fact]
    public void ThrowIfSecretNames_Passes_For_Safe_Names()
    {
        SecretPolicy.ThrowIfSecretNames(["tenantId", "route", "sourceLang", "targetLang"]);
    }

    [Fact]
    public void ThrowIfSecretNames_Fails_Closed_And_Lists_The_Offenders()
    {
        var ex = Assert.Throws<DomainException>(
            () => SecretPolicy.ThrowIfSecretNames(["tenantId", "ApiKey", "password"]));

        Assert.Equal("Hash inputs must not contain secret material: ApiKey,password.", ex.Message);
        Assert.DoesNotContain("sk-test-DO-NOT-USE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_Delegates_To_The_Redactor()
    {
        Assert.Equal(
            SecretRedactor.Redact("api_key=sk-test-DO-NOT-USE"),
            SecretPolicy.Redact("api_key=sk-test-DO-NOT-USE"));
        Assert.Null(SecretPolicy.Redact(null));
        Assert.Equal(string.Empty, SecretPolicy.Redact(string.Empty));
    }

    [Fact]
    public void RedactDetails_Delegates_To_The_Redactor()
    {
        var redacted = SecretPolicy.RedactDetails(
            [new KeyValuePair<string, object?>("ApiKey", "sk-test-DO-NOT-USE")]);

        Assert.Equal("[REDACTED]", redacted["ApiKey"]);
        Assert.Empty(SecretPolicy.RedactDetails(null));
    }
}
