// Task 039C: options validation unit gap closure.

using System.ComponentModel.DataAnnotations;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.UnitTests.Options;

/// <summary>
/// Rule-by-rule validation matrix for the application options records under
/// <c>src/DubbingPlatform.Application/Options</c>. For every type this asserts
/// (a) the shipped defaults pass, and (b) at least one invalid value per rule
/// fails: <c>[Range]</c>/<c>[Required]</c>/<c>[MinLength]</c>/<c>[MaxLength]</c>
/// DataAnnotations, the hand-written <c>IValidateOptions&lt;T&gt;</c> checks,
/// the cross-field rules and the static helpers (endpoint allowlist, artifact
/// hash, device profile, scenario name, provider-name parsing).
/// <para>
/// <c>OptionsValidationTests</c> already asserts defaults-pass for Auth,
/// AuthRateLimit, Deployment, Provider and Quota; that pass-case is repeated
/// here (and extended to the remaining 12 types) so every region below is
/// self-contained. Its per-type invalid cases are <em>not</em> repeated: the
/// same rules below use different values, and the rules it does not cover
/// (endpoint allowlist, DataAnnotations, model registry, provider keys) are
/// covered for the first time.
/// </para>
/// Pure in-process assertions: no containers, no network, no database, no
/// clock, no file IO. All keys/paths below are obvious placeholders.
/// </summary>
public sealed class OptionsValidationMatrixTests
{
    /// <summary>The unnamed options instance every validator is called with.</summary>
    private static readonly string Name = Microsoft.Extensions.Options.Options.DefaultName;

    /// <summary>64 valid hex characters - a syntactically valid fake artifact hash.</summary>
    private static readonly string ValidArtifactHash = new('a', 64);

    /// <summary>32+ characters - minimum accepted fake HS256 test signing key.</summary>
    private const string FakeSigningKey = "CHANGE_ME_test-only-hs256-key-000000";

    /// <summary>Placeholder provider key material; never a real credential.</summary>
    private const string FakeProviderKey = "CHANGE_ME_not-a-real-provider-key";

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void Shipped_Defaults_Pass_Every_Matrixed_Validator()
    {
        Assert.False(new AuthOptionsValidator().Validate(Name, new AuthOptions()).Failed);
        Assert.False(new AuthRateLimitOptionsValidator().Validate(Name, new AuthRateLimitOptions()).Failed);
        Assert.False(new AzureProviderOptionsValidator().Validate(Name, new AzureProviderOptions()).Failed);
        Assert.False(new CorsOptionsValidator().Validate(Name, new CorsOptions()).Failed);
        Assert.False(new DeploymentOptionsValidator().Validate(Name, new DeploymentOptions()).Failed);
        Assert.False(new GoogleProviderOptionsValidator().Validate(Name, new GoogleProviderOptions()).Failed);
        Assert.False(new LocalInferenceOptionsValidator().Validate(Name, new LocalInferenceOptions()).Failed);
        Assert.False(new MixingOptionsValidator().Validate(Name, new MixingOptions()).Failed);
        Assert.False(new MockBehaviorOptionsValidator().Validate(Name, new MockBehaviorOptions()).Failed);
        Assert.False(new OpenAiProviderOptionsValidator().Validate(Name, new OpenAiProviderOptions()).Failed);
        Assert.False(new PreviewOptionsValidator().Validate(Name, new PreviewOptions()).Failed);
        Assert.False(new ProviderOptionsValidator().Validate(Name, new ProviderOptions()).Failed);
        Assert.False(new QuotaOptionsValidator().Validate(Name, new QuotaOptions()).Failed);
        Assert.False(new SecurityOptionsValidator().Validate(Name, new SecurityOptions()).Failed);
        Assert.False(new SegmentOptionsValidator().Validate(Name, new SegmentOptions()).Failed);
        Assert.False(new TranslationOptionsValidator().Validate(Name, new TranslationOptions()).Failed);
        Assert.False(new TtsOptionsValidator().Validate(Name, new TtsOptions()).Failed);
    }

    [Fact]
    public void Section_Names_Are_Stable_Configuration_Keys()
    {
        Assert.Equal("Auth", AuthOptions.SectionName);
        Assert.Equal("AuthRateLimit", AuthRateLimitOptions.SectionName);
        Assert.Equal("Azure", AzureProviderOptions.SectionName);
        Assert.Equal("Cors", CorsOptions.SectionName);
        Assert.Equal("Deployment", DeploymentOptions.SectionName);
        Assert.Equal("Google", GoogleProviderOptions.SectionName);
        Assert.Equal("LocalInference", LocalInferenceOptions.SectionName);
        Assert.Equal("Mixing", MixingOptions.SectionName);
        Assert.Equal("Providers:Mock", MockBehaviorOptions.SectionName);
        Assert.Equal("OpenAI", OpenAiProviderOptions.SectionName);
        Assert.Equal("Preview", PreviewOptions.SectionName);
        Assert.Equal("Providers", ProviderOptions.SectionName);
        Assert.Equal("Quota", QuotaOptions.SectionName);
        Assert.Equal("Security", SecurityOptions.SectionName);
        Assert.Equal("Segment", SegmentOptions.SectionName);
        Assert.Equal("Translation", TranslationOptions.SectionName);
        Assert.Equal("Tts", TtsOptions.SectionName);
        Assert.Equal("https://api.openai.com/v1", OpenAiProviderOptions.DefaultBaseUrl);
    }

    [Fact]
    public void Every_Validator_Rejects_A_Null_Options_Instance()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new AuthRateLimitOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new AzureProviderOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new CorsOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new DeploymentOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new GoogleProviderOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new LocalInferenceOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new MixingOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new MockBehaviorOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new OpenAiProviderOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new PreviewOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new ProviderOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new QuotaOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new SecurityOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new SegmentOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new TranslationOptionsValidator().Validate(Name, null!));
        Assert.Throws<ArgumentNullException>(() => new TtsOptionsValidator().Validate(Name, null!));
    }

    // -------------------------------------------------------------- AuthOptions

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void AuthOptions_Rejects_Blank_Audience(string audience)
    {
        var validator = new AuthOptionsValidator();

        var result = validator.Validate(Name, new AuthOptions { Audience = audience });

        Assert.True(result.Failed);
        Assert.Contains("Audience", result.FailureMessage!);
    }

    [Fact]
    public void AuthOptions_Rejects_Null_Audience()
    {
        var validator = new AuthOptionsValidator();

        Assert.True(validator.Validate(Name, new AuthOptions { Audience = null! }).Failed);
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    public void AuthOptions_Rejects_NonAbsolute_Authority(string authority)
    {
        var validator = new AuthOptionsValidator();

        var result = validator.Validate(Name, new AuthOptions { Authority = authority });

        Assert.True(result.Failed);
        Assert.Contains("Authority", result.FailureMessage!);
    }

    [Fact]
    public void AuthOptions_Accepts_Absent_Or_Absolute_Authority()
    {
        var validator = new AuthOptionsValidator();

        Assert.False(validator.Validate(Name, new AuthOptions { Authority = string.Empty }).Failed);
        Assert.False(validator.Validate(Name, new AuthOptions { Authority = null! }).Failed);
        Assert.False(validator.Validate(Name, new AuthOptions { Authority = "https://login.example.test/tenant" }).Failed);
        Assert.False(validator.Validate(Name, new AuthOptions { Authority = "http://login.example.test" }).Failed);
    }

    [Fact]
    public void AuthOptions_RequireHttps_Rejects_Cleartext_Authority()
    {
        var validator = new AuthOptionsValidator();

        var result = validator.Validate(
            Name,
            new AuthOptions { RequireHttps = true, Authority = "http://login.example.test" });

        Assert.True(result.Failed);
        Assert.Contains("https", result.FailureMessage!);
    }

    [Fact]
    public void AuthOptions_RequireHttps_Accepts_Https_Case_Insensitively_Or_No_Authority()
    {
        var validator = new AuthOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new AuthOptions { RequireHttps = true, Authority = "HTTPS://login.example.test" }).Failed);
        Assert.False(validator.Validate(
            Name,
            new AuthOptions { RequireHttps = true, Authority = string.Empty }).Failed);
        Assert.False(validator.Validate(
            Name,
            new AuthOptions { RequireHttps = false, Authority = "http://login.example.test" }).Failed);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("CHANGE_ME")]
    [InlineData("1234567890123456789012345678901")] // 31 chars - one short
    public void AuthOptions_Rejects_Short_SigningKey(string signingKey)
    {
        var validator = new AuthOptionsValidator();

        var result = validator.Validate(Name, new AuthOptions { SigningKey = signingKey });

        Assert.True(result.Failed);
        Assert.Contains("SigningKey", result.FailureMessage!);
    }

    [Fact]
    public void AuthOptions_Accepts_Absent_Or_32_Plus_Char_SigningKey()
    {
        var validator = new AuthOptionsValidator();

        Assert.False(validator.Validate(Name, new AuthOptions { SigningKey = string.Empty }).Failed);
        Assert.False(validator.Validate(Name, new AuthOptions { SigningKey = new string('k', 32) }).Failed);
        Assert.False(validator.Validate(Name, new AuthOptions { SigningKey = FakeSigningKey }).Failed);
    }

    // ------------------------------------------------------ AuthRateLimitOptions

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void AuthRateLimitOptions_Rejects_Login_Budget_Outside_1_to_10000(int value)
    {
        var validator = new AuthRateLimitOptionsValidator();

        var result = validator.Validate(Name, new AuthRateLimitOptions { LoginPerMinutePerIp = value });

        Assert.True(result.Failed);
        Assert.Contains("LoginPerMinutePerIp", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10001)]
    public void AuthRateLimitOptions_Rejects_Refresh_Budget_Outside_1_to_10000(int value)
    {
        var validator = new AuthRateLimitOptionsValidator();

        var result = validator.Validate(Name, new AuthRateLimitOptions { RefreshPerMinutePerUser = value });

        Assert.True(result.Failed);
        Assert.Contains("RefreshPerMinutePerUser", result.FailureMessage!);
    }

    [Fact]
    public void AuthRateLimitOptions_Accepts_Boundaries_And_Disabled_Bypass()
    {
        var validator = new AuthRateLimitOptionsValidator();

        Assert.False(validator.Validate(Name, new AuthRateLimitOptions { LoginPerMinutePerIp = 1, RefreshPerMinutePerUser = 1 }).Failed);
        Assert.False(validator.Validate(Name, new AuthRateLimitOptions { LoginPerMinutePerIp = 10000, RefreshPerMinutePerUser = 10000 }).Failed);
        Assert.False(validator.Validate(Name, new AuthRateLimitOptions { Enabled = false, LoginPerMinutePerIp = 5, RefreshPerMinutePerUser = 30 }).Failed);
    }

    // ------------------------------------------------------ AzureProviderOptions

    [Theory]
    [InlineData("http://westus.api.cognitive.test")]
    [InlineData("not-a-url")]
    public void AzureProviderOptions_Rejects_Disallowed_SpeechBaseUrl(string url)
    {
        var validator = new AzureProviderOptionsValidator();

        var result = validator.Validate(Name, new AzureProviderOptions { SpeechBaseUrl = url });

        Assert.True(result.Failed);
        Assert.Contains("is not allowed", result.FailureMessage!);
    }

    [Fact]
    public void AzureProviderOptions_Rejects_Disallowed_TranslatorBaseUrl_After_Valid_SpeechBaseUrl()
    {
        var validator = new AzureProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new AzureProviderOptions
            {
                SpeechBaseUrl = "https://westus.api.cognitive.microsoft.test",
                TranslatorBaseUrl = "http://westeurope.api.cognitive.test",
            });

        Assert.True(result.Failed);
        Assert.Contains("is not allowed", result.FailureMessage!);
    }

    [Fact]
    public void AzureProviderOptions_Accepts_Https_And_Loopback_BaseUrls_And_Blank_Slots()
    {
        var validator = new AzureProviderOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new AzureProviderOptions
            {
                SpeechBaseUrl = "https://westus.api.cognitive.microsoft.test",
                TranslatorBaseUrl = "http://localhost:9099",
            }).Failed);
        Assert.False(validator.Validate(
            Name,
            new AzureProviderOptions { SpeechBaseUrl = string.Empty, TranslatorBaseUrl = "   " }).Failed);
        Assert.False(validator.Validate(
            Name,
            new AzureProviderOptions { SpeechBaseUrl = null, TranslatorBaseUrl = null }).Failed);
    }

    [Fact]
    public void AzureProviderOptions_Rejects_Oversized_SpeechKey()
    {
        var validator = new AzureProviderOptionsValidator();

        var result = validator.Validate(Name, new AzureProviderOptions { SpeechKey = new string('k', 513) });

        Assert.True(result.Failed);
        Assert.Contains("SpeechKey", result.FailureMessage!);
    }

    [Fact]
    public void AzureProviderOptions_Rejects_Oversized_TranslatorKey()
    {
        var validator = new AzureProviderOptionsValidator();

        var result = validator.Validate(Name, new AzureProviderOptions { TranslatorKey = new string('k', 513) });

        Assert.True(result.Failed);
        Assert.Contains("TranslatorKey", result.FailureMessage!);
    }

    [Fact]
    public void AzureProviderOptions_Accepts_Keys_At_Maximum_Length_And_Empty_Keys()
    {
        var validator = new AzureProviderOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new AzureProviderOptions { SpeechKey = new string('k', 512), TranslatorKey = new string('k', 512) }).Failed);
        Assert.False(validator.Validate(
            Name,
            new AzureProviderOptions { SpeechKey = FakeProviderKey, TranslatorKey = FakeProviderKey }).Failed);

        // The length guards skip null keys entirely (a hole in the shape check,
        // not an error): null must still start up so mock-only boots work.
        Assert.False(validator.Validate(
            Name,
            new AzureProviderOptions { SpeechKey = null!, TranslatorKey = null! }).Failed);
    }

    // -------------------------------------------------------------- CorsOptions

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CorsOptions_Rejects_Blank_Origin_Entries(string origin)
    {
        var validator = new CorsOptionsValidator();

        var result = validator.Validate(Name, new CorsOptions { AllowedOrigins = [origin] });

        Assert.True(result.Failed);
        Assert.Contains("empty entries", result.FailureMessage!);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("  *  ")]
    public void CorsOptions_Rejects_Wildcard_Origin(string origin)
    {
        var validator = new CorsOptionsValidator();

        var result = validator.Validate(Name, new CorsOptions { AllowedOrigins = [origin] });

        Assert.True(result.Failed);
        Assert.Contains("wildcard", result.FailureMessage!);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("/relative")]
    public void CorsOptions_Rejects_NonAbsolute_Origin(string origin)
    {
        var validator = new CorsOptionsValidator();

        var result = validator.Validate(Name, new CorsOptions { AllowedOrigins = [origin] });

        Assert.True(result.Failed);
        Assert.Contains("absolute http(s) URI", result.FailureMessage!);
    }

    [Theory]
    [InlineData("ftp://files.example.test")]
    [InlineData("file://host/share")]
    public void CorsOptions_Rejects_NonHttp_Scheme_Origin(string origin)
    {
        var validator = new CorsOptionsValidator();

        var result = validator.Validate(Name, new CorsOptions { AllowedOrigins = [origin] });

        Assert.True(result.Failed);
        Assert.Contains("absolute http(s) URI", result.FailureMessage!);
    }

    [Fact]
    public void CorsOptions_Rejects_Origin_With_A_Path()
    {
        var validator = new CorsOptionsValidator();

        var result = validator.Validate(Name, new CorsOptions { AllowedOrigins = ["https://app.example.test/api"] });

        Assert.True(result.Failed);
        Assert.Contains("must not contain a path", result.FailureMessage!);
    }

    [Fact]
    public void CorsOptions_Rejects_Later_Entry_In_The_Allowlist()
    {
        var validator = new CorsOptionsValidator();

        var result = validator.Validate(
            Name,
            new CorsOptions { AllowedOrigins = ["https://app.example.test", "https://admin.example.test/v1"] });

        Assert.True(result.Failed);
        Assert.Contains("must not contain a path", result.FailureMessage!);
    }

    [Fact]
    public void CorsOptions_Accepts_Empty_Null_And_Root_Path_Origins()
    {
        var validator = new CorsOptionsValidator();

        Assert.False(validator.Validate(Name, new CorsOptions { AllowedOrigins = [] }).Failed);
        Assert.False(validator.Validate(Name, new CorsOptions { AllowedOrigins = null! }).Failed);
        Assert.False(validator.Validate(Name, new CorsOptions { AllowedOrigins = ["https://app.example.test"] }).Failed);
        Assert.False(validator.Validate(Name, new CorsOptions { AllowedOrigins = ["https://app.example.test/"] }).Failed);
        Assert.False(validator.Validate(Name, new CorsOptions { AllowedOrigins = ["  http://localhost:5173  "] }).Failed);
        Assert.False(validator.Validate(
            Name,
            new CorsOptions { AllowedOrigins = ["https://app.example.test", "http://localhost:5173"] }).Failed);
    }

    // --------------------------------------------------------- DeploymentOptions

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DeploymentOptions_Rejects_Blank_Environment(string environment)
    {
        var validator = new DeploymentOptionsValidator();

        var result = validator.Validate(Name, new DeploymentOptions { Environment = environment });

        Assert.True(result.Failed);
        Assert.Contains("Environment", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeploymentOptions_Rejects_Blank_Region(string region)
    {
        var validator = new DeploymentOptionsValidator();

        var result = validator.Validate(Name, new DeploymentOptions { Region = region });

        Assert.True(result.Failed);
        Assert.Contains("Region", result.FailureMessage!);
    }

    [Fact]
    public void DeploymentOptions_Rejects_Null_Environment_And_Region()
    {
        var validator = new DeploymentOptionsValidator();

        Assert.True(validator.Validate(Name, new DeploymentOptions { Environment = null! }).Failed);
        Assert.True(validator.Validate(Name, new DeploymentOptions { Region = null! }).Failed);
    }

    [Fact]
    public void DeploymentOptions_Accepts_Explicit_Values_At_Maximum_Length()
    {
        var validator = new DeploymentOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new DeploymentOptions { Environment = "Production", Region = "eu-central-1" }).Failed);
        Assert.False(validator.Validate(
            Name,
            new DeploymentOptions { Environment = new string('e', 64), Region = new string('r', 64) }).Failed);
    }

    // ------------------------------------------------------ GoogleProviderOptions

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void GoogleProviderOptions_Rejects_Blank_Location(string location)
    {
        var validator = new GoogleProviderOptionsValidator();

        var result = validator.Validate(Name, new GoogleProviderOptions { Location = location });

        Assert.True(result.Failed);
        Assert.Contains("Location", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GoogleProviderOptions_Rejects_Blank_GeminiModel(string model)
    {
        var validator = new GoogleProviderOptionsValidator();

        var result = validator.Validate(Name, new GoogleProviderOptions { GeminiModel = model });

        Assert.True(result.Failed);
        Assert.Contains("GeminiModel", result.FailureMessage!);
    }

    [Theory]
    [InlineData("http://speech.example.test")]
    [InlineData("not-a-url")]
    [InlineData("http://translate.example.test")]
    [InlineData("http://gemini.example.test")]
    [InlineData("http://tts.example.test")]
    public void GoogleProviderOptions_Rejects_Disallowed_BaseUrls(string url)
    {
        var validator = new GoogleProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new GoogleProviderOptions
            {
                SpeechBaseUrl = url,
                TranslateBaseUrl = url,
                GeminiBaseUrl = url,
                TtsBaseUrl = url,
            });

        Assert.True(result.Failed);
        Assert.Contains("is not allowed", result.FailureMessage!);
    }

    [Fact]
    public void GoogleProviderOptions_Rejects_Disallowed_Last_BaseUrl_Slot()
    {
        var validator = new GoogleProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new GoogleProviderOptions
            {
                SpeechBaseUrl = "https://speech.example.test",
                TranslateBaseUrl = "https://translate.example.test",
                GeminiBaseUrl = "https://gemini.example.test",
                TtsBaseUrl = "http://tts.example.test",
            });

        Assert.True(result.Failed);
        Assert.Contains("is not allowed", result.FailureMessage!);
    }

    [Fact]
    public void GoogleProviderOptions_Accepts_Allowed_And_Absent_BaseUrls()
    {
        var validator = new GoogleProviderOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new GoogleProviderOptions
            {
                SpeechBaseUrl = "https://speech.example.test",
                TranslateBaseUrl = "http://localhost:9090",
                GeminiBaseUrl = "   ",
                TtsBaseUrl = null,
            }).Failed);
        Assert.False(validator.Validate(
            Name,
            new GoogleProviderOptions { UseGeminiForTranslation = true, ApiKey = FakeProviderKey, ProjectId = "fake-project" }).Failed);
    }

    // ------------------------------------------------------ LocalInferenceOptions

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LocalInferenceOptions_Rejects_Blank_Endpoint(string? endpoint)
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { Endpoint = endpoint! });

        Assert.True(result.Failed);
        Assert.Contains("Endpoint", result.FailureMessage!);
    }

    [Theory]
    [InlineData("http://inference.example.test:8000")]
    [InlineData("not-a-url")]
    [InlineData("ftp://internal.example.test")]
    public void LocalInferenceOptions_Rejects_Disallowed_Endpoint(string endpoint)
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { Endpoint = endpoint });

        Assert.True(result.Failed);
        Assert.Contains("is not allowed", result.FailureMessage!);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("HTTP")]
    [InlineData("grpc")]
    [InlineData("GRPC")]
    public void LocalInferenceOptions_Accepts_Http_Or_Grpc_Protocol(string protocol)
    {
        var validator = new LocalInferenceOptionsValidator();

        Assert.False(validator.Validate(Name, new LocalInferenceOptions { Protocol = protocol }).Failed);
    }

    [Theory]
    [InlineData("amqp")]
    [InlineData("https")]
    [InlineData("")]
    public void LocalInferenceOptions_Rejects_Unknown_Protocol(string protocol)
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { Protocol = protocol });

        Assert.True(result.Failed);
        Assert.Contains("Protocol", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void LocalInferenceOptions_Rejects_Blank_ModelName(string modelName)
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { ModelName = modelName });

        Assert.True(result.Failed);
        Assert.Contains("ModelName", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    public void LocalInferenceOptions_Rejects_Concurrency_Outside_1_to_16(int value)
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { MaxConcurrency = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxConcurrency", result.FailureMessage!);
    }

    [Fact]
    public void LocalInferenceOptions_Rejects_Null_Model_Registry()
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = null! });

        Assert.True(result.Failed);
        Assert.Contains("Models", result.FailureMessage!);
    }

    [Fact]
    public void LocalInferenceOptions_Rejects_Null_Model_Registry_Entry()
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = [null!] });

        Assert.True(result.Failed);
        Assert.Contains("must not be null", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void LocalInferenceOptions_Rejects_Blank_Model_Id(string id)
    {
        var validator = new LocalInferenceOptionsValidator();

        var entry = ValidModelEntry();
        entry.Id = id;

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("Id", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void LocalInferenceOptions_Rejects_Blank_Model_Version(string version)
    {
        var validator = new LocalInferenceOptionsValidator();

        var entry = ValidModelEntry();
        entry.Version = version;

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("Version", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("abc")] // too short
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 64 non-hex
    public void LocalInferenceOptions_Rejects_Invalid_Artifact_Hash(string hash)
    {
        var validator = new LocalInferenceOptionsValidator();

        var entry = ValidModelEntry();
        entry.ArtifactHash = hash;

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("64 hex chars", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gpu")]
    [InlineData("cuda:16")]
    public void LocalInferenceOptions_Rejects_Invalid_Device_Profile(string profile)
    {
        var validator = new LocalInferenceOptionsValidator();

        var entry = ValidModelEntry();
        entry.DeviceProfile = profile;

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("DeviceProfile", result.FailureMessage!);
    }

    [Fact]
    public void LocalInferenceOptions_Rejects_Unknown_Model_Capability()
    {
        var validator = new LocalInferenceOptionsValidator();

        var entry = ValidModelEntry();
        entry.Capability = "NotACapability";

        var result = validator.Validate(Name, new LocalInferenceOptions { Models = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("Capability", result.FailureMessage!);
    }

    [Fact]
    public void LocalInferenceOptions_Accepts_Complete_Model_Registry()
    {
        var validator = new LocalInferenceOptionsValidator();

        var entry = ValidModelEntry();
        entry.Version = "3";
        entry.DeviceProfile = "cuda:0";
        entry.RuntimeRequirements = "CHANGE_ME";

        var result = validator.Validate(
            Name,
            new LocalInferenceOptions { MaxConcurrency = 1, Models = [entry, ValidModelEntry()] });

        Assert.False(result.Failed);
    }

    /// <summary>
    /// A model registry entry that satisfies every rule, so a test can break
    /// exactly one field at a time and assert on the specific failure message.
    /// </summary>
    private static LocalInferenceModelOptions ValidModelEntry()
    {
        return new LocalInferenceModelOptions
        {
            Id = "local-small",
            Version = "1",
            ArtifactHash = ValidArtifactHash,
            DeviceProfile = "cpu",
            Capability = nameof(ProviderCapability.LocalInference),
        };
    }

    [Fact]
    public void LocalInferenceOptions_RequireMtls_Requires_Https_Endpoint()
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(
            Name,
            new LocalInferenceOptions
            {
                RequireMtls = true,
                Endpoint = "http://localhost:8081",
                ClientCertificatePath = "/etc/certs/CHANGE_ME.pfx",
            });

        Assert.True(result.Failed);
        Assert.Contains("must be https", result.FailureMessage!);
    }

    [Fact]
    public void LocalInferenceOptions_RequireMtls_Requires_Client_Certificate()
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(
            Name,
            new LocalInferenceOptions
            {
                RequireMtls = true,
                Endpoint = "https://sidecar.example.test:8443",
            });

        Assert.True(result.Failed);
        Assert.Contains("ClientCertificatePath", result.FailureMessage!);
    }

    [Fact]
    public void LocalInferenceOptions_RequireMtls_Accepts_Https_With_Certificate()
    {
        var validator = new LocalInferenceOptionsValidator();

        var result = validator.Validate(
            Name,
            new LocalInferenceOptions
            {
                RequireMtls = true,
                Endpoint = "https://sidecar.example.test:8443",
                ClientCertificatePath = "/etc/certs/CHANGE_ME.pfx",
                ClientCertificatePassword = FakeSigningKey,
            });

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("https://sidecar.example.test:8443", true)]
    [InlineData("http://localhost:8081", true)]
    [InlineData("http://127.0.0.1:8081", true)]
    [InlineData("http://local-inference:8000", true)]
    [InlineData("http://LOCAL-INFERENCE:8000", true)]
    [InlineData("http://inference.default.svc:8000", true)] // *.svc
    [InlineData("http://inference.default.svc.cluster.local:8000", true)]
    [InlineData("http://inference.cluster.local:8000", true)]
    [InlineData("http://inference:8000", false)] // bare service name is not cluster DNS
    [InlineData("http://inference.example.test:8000", false)]
    [InlineData("http://[fd00::1]:8000", false)]
    [InlineData("ftp://internal.example.test", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsSidecarEndpointAllowed_Only_Allows_Trusted_Shapes(string? url, bool expected)
    {
        Assert.Equal(expected, LocalInferenceOptions.IsSidecarEndpointAllowed(url));
    }

    [Fact]
    public void RequireSidecarEndpoint_Trims_And_Strips_Trailing_Slash()
    {
        var result = LocalInferenceOptions.RequireSidecarEndpoint(
            "  https://sidecar.example.test:8443/  ",
            nameof(LocalInferenceOptions.Endpoint));

        Assert.Equal("https://sidecar.example.test:8443", result);
    }

    [Fact]
    public void RequireSidecarEndpoint_Throws_For_Disallowed_Endpoint()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => LocalInferenceOptions.RequireSidecarEndpoint(
                "http://inference.example.test:8000",
                nameof(LocalInferenceOptions.Endpoint)));

        Assert.Equal(nameof(LocalInferenceOptions.Endpoint), exception.ParamName);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("abc", false)]
    [InlineData("gfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", false)]
    public void IsValidArtifactHash_Rejects_Malformed_Hashes(string? hash, bool expected)
    {
        Assert.Equal(expected, LocalInferenceOptions.IsValidArtifactHash(hash));
    }

    [Fact]
    public void IsValidArtifactHash_Accepts_64_Hex_Chars_With_Optional_Padding()
    {
        Assert.True(LocalInferenceOptions.IsValidArtifactHash(ValidArtifactHash));
        Assert.True(LocalInferenceOptions.IsValidArtifactHash(new string('F', 64)));
        Assert.True(LocalInferenceOptions.IsValidArtifactHash("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        Assert.True(LocalInferenceOptions.IsValidArtifactHash("  " + ValidArtifactHash + "  "));
        Assert.False(LocalInferenceOptions.IsValidArtifactHash(new string('a', 63)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("gpu", false)]
    [InlineData("cuda:", false)]
    [InlineData("cuda:x", false)]
    [InlineData("cuda:-1", false)]
    [InlineData("cuda:16", false)]
    [InlineData("cpu", true)]
    [InlineData("CPU", true)]
    [InlineData("  cpu  ", true)]
    [InlineData("cuda", true)]
    [InlineData("CUDA", true)]
    [InlineData("cuda:0", true)]
    [InlineData("cuda:15", true)]
    public void IsValidDeviceProfile_Matches_Cpu_And_Cuda_Ordinal_Forms(string? profile, bool expected)
    {
        Assert.Equal(expected, LocalInferenceOptions.IsValidDeviceProfile(profile));
    }

    // ------------------------------------------------------------ MixingOptions

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("webcast")]
    [InlineData("lossless")]
    public void MixingOptions_Rejects_Unknown_Profile(string profile)
    {
        var validator = new MixingOptionsValidator();

        var result = validator.Validate(Name, new MixingOptions { Profile = profile });

        Assert.True(result.Failed);
        Assert.Contains("Profile", result.FailureMessage!);
    }

    [Fact]
    public void MixingOptions_Rejects_Null_Profile()
    {
        var validator = new MixingOptionsValidator();

        Assert.True(validator.Validate(Name, new MixingOptions { Profile = null! }).Failed);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("WEB")]
    [InlineData("  broadcast  ")]
    public void MixingOptions_Accepts_Profile_Case_Insensitively_With_Whitespace(string profile)
    {
        var validator = new MixingOptionsValidator();

        Assert.False(validator.Validate(Name, new MixingOptions { Profile = profile }).Failed);
    }

    [Fact]
    public void MixingOptions_Rejects_NonFinite_Or_Out_Of_Range_DuckDb()
    {
        var validator = new MixingOptionsValidator();

        Assert.True(validator.Validate(Name, new MixingOptions { DuckDb = double.NaN }).Failed);
        Assert.True(validator.Validate(Name, new MixingOptions { DuckDb = double.PositiveInfinity }).Failed);
        Assert.True(validator.Validate(Name, new MixingOptions { DuckDb = double.NegativeInfinity }).Failed);
        Assert.True(validator.Validate(Name, new MixingOptions { DuckDb = 0.5 }).Failed);
        Assert.True(validator.Validate(Name, new MixingOptions { DuckDb = -30.5 }).Failed);
    }

    [Theory]
    [InlineData(-30.0)]
    [InlineData(-12.0)]
    [InlineData(0.0)]
    public void MixingOptions_Accepts_DuckDb_At_Both_Bounds(double duckDb)
    {
        var validator = new MixingOptionsValidator();

        Assert.False(validator.Validate(Name, new MixingOptions { DuckDb = duckDb }).Failed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2001)]
    public void MixingOptions_Rejects_FadeMs_Outside_0_to_2000(int fadeMs)
    {
        var validator = new MixingOptionsValidator();

        var result = validator.Validate(Name, new MixingOptions { FadeMs = fadeMs });

        Assert.True(result.Failed);
        Assert.Contains("FadeMs", result.FailureMessage!);
    }

    [Fact]
    public void MixingOptions_Accepts_FadeMs_Bounds_And_Single_Pass_Flag()
    {
        var validator = new MixingOptionsValidator();

        Assert.False(validator.Validate(Name, new MixingOptions { FadeMs = 0, TwoPass = false }).Failed);
        Assert.False(validator.Validate(Name, new MixingOptions { FadeMs = 2000 }).Failed);
    }

    // ------------------------------------------------------ MockBehaviorOptions

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("nope", false)]
    [InlineData("success", true)]
    [InlineData("  LOW-CONFIDENCE  ", true)]
    [InlineData("quota-exhausted", true)]
    public void IsKnownScenario_Normalizes_And_Matches_Known_Names(string? scenario, bool expected)
    {
        Assert.Equal(expected, MockBehaviorOptions.IsKnownScenario(scenario));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(" Rate-Limited ", "rate-limited")]
    [InlineData("SUCCESS", "success")]
    public void NormalizeScenario_Trims_And_Lowercases(string? scenario, string expected)
    {
        Assert.Equal(expected, MockBehaviorOptions.NormalizeScenario(scenario));
    }

    [Fact]
    public void MockBehaviorOptions_Known_Scenarios_Are_Stable()
    {
        Assert.Equal(10, MockBehaviorOptions.KnownScenarios.Length);
        Assert.Equal("success", MockBehaviorOptions.Success);
        Assert.Equal("low-confidence", MockBehaviorOptions.LowConfidence);
        Assert.Equal("rate-limited", MockBehaviorOptions.RateLimited);
        Assert.Equal("timeout", MockBehaviorOptions.Timeout);
        Assert.Equal("malformed", MockBehaviorOptions.Malformed);
        Assert.Equal("async-job", MockBehaviorOptions.AsyncJob);
        Assert.Equal("duplicate", MockBehaviorOptions.Duplicate);
        Assert.Equal("partial", MockBehaviorOptions.Partial);
        Assert.Equal("expired", MockBehaviorOptions.Expired);
        Assert.Equal("quota-exhausted", MockBehaviorOptions.QuotaExhausted);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_Unknown_Root_Scenario()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(Name, new MockBehaviorOptions { Scenario = "explode" });

        Assert.True(result.Failed);
        Assert.Contains("unknown scenario", result.FailureMessage!);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_FailRate_Outside_0_to_1()
    {
        var validator = new MockBehaviorOptionsValidator();

        Assert.True(validator.Validate(Name, new MockBehaviorOptions { FailRate = double.NaN }).Failed);
        Assert.True(validator.Validate(Name, new MockBehaviorOptions { FailRate = -0.01 }).Failed);
        Assert.True(validator.Validate(Name, new MockBehaviorOptions { FailRate = 1.01 }).Failed);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_Unknown_FailWith()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(
            Name,
            new MockBehaviorOptions { FailWith = "kaboom", FailRate = 0.5 });

        Assert.True(result.Failed);
        Assert.Contains("FailWith", result.FailureMessage!);
    }

    [Fact]
    public void MockBehaviorOptions_Accepts_Known_FailWith_And_FailRate_Bounds()
    {
        var validator = new MockBehaviorOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new MockBehaviorOptions { FailWith = MockBehaviorOptions.Timeout, FailRate = 1.0 }).Failed);
        Assert.False(validator.Validate(
            Name,
            new MockBehaviorOptions { FailWith = "   ", FailRate = 0.0 }).Failed);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_Null_Behaviors_Dictionary()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(Name, new MockBehaviorOptions { Behaviors = null! });

        Assert.True(result.Failed);
        Assert.Contains("Behaviors", result.FailureMessage!);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_Blank_Behavior_Key()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(
            Name,
            new MockBehaviorOptions { Behaviors = new(StringComparer.OrdinalIgnoreCase) { ["  "] = new MockBehaviorOptions() } });

        Assert.True(result.Failed);
        Assert.Contains("empty capability names", result.FailureMessage!);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_Null_Behavior_Value()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(
            Name,
            new MockBehaviorOptions { Behaviors = new(StringComparer.OrdinalIgnoreCase) { ["Tts"] = null! } });

        Assert.True(result.Failed);
        Assert.Contains("must not be null", result.FailureMessage!);
    }

    [Fact]
    public void MockBehaviorOptions_Rejects_Invalid_Nested_Behavior()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(
            Name,
            new MockBehaviorOptions
            {
                Behaviors = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Transcription"] = new MockBehaviorOptions { Scenario = "explode" },
                },
            });

        Assert.True(result.Failed);
        Assert.Contains("Behaviors['Transcription']", result.FailureMessage!);
    }

    [Fact]
    public void MockBehaviorOptions_Accepts_Valid_Per_Capability_Overrides()
    {
        var validator = new MockBehaviorOptionsValidator();

        var result = validator.Validate(
            Name,
            new MockBehaviorOptions
            {
                Scenario = MockBehaviorOptions.Timeout,
                Behaviors = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Transcription"] = new MockBehaviorOptions
                    {
                        Scenario = MockBehaviorOptions.LowConfidence,
                        FailRate = 0.25,
                        FailWith = MockBehaviorOptions.Malformed,
                    },
                    ["translation"] = new MockBehaviorOptions { Scenario = MockBehaviorOptions.Success },
                },
            });

        Assert.False(result.Failed);
    }

    // ------------------------------------------------- OpenAiProviderOptions

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenAiProviderOptions_Rejects_Blank_Model(string model)
    {
        var validator = new OpenAiProviderOptionsValidator();

        var result = validator.Validate(Name, new OpenAiProviderOptions { Model = model });

        Assert.True(result.Failed);
        Assert.Contains("Model", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void OpenAiProviderOptions_Rejects_Blank_ChatModel(string model)
    {
        var validator = new OpenAiProviderOptionsValidator();

        var result = validator.Validate(Name, new OpenAiProviderOptions { ChatModel = model });

        Assert.True(result.Failed);
        Assert.Contains("ChatModel", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenAiProviderOptions_Rejects_Blank_TtsModel(string model)
    {
        var validator = new OpenAiProviderOptionsValidator();

        var result = validator.Validate(Name, new OpenAiProviderOptions { TtsModel = model });

        Assert.True(result.Failed);
        Assert.Contains("TtsModel", result.FailureMessage!);
    }

    [Fact]
    public void OpenAiProviderOptions_Rejects_Blank_BaseUrl()
    {
        var validator = new OpenAiProviderOptionsValidator();

        var result = validator.Validate(Name, new OpenAiProviderOptions { BaseUrl = "  " });

        Assert.True(result.Failed);
        Assert.Contains("BaseUrl", result.FailureMessage!);
    }

    [Fact]
    public void OpenAiProviderOptions_Rejects_Disallowed_BaseUrl()
    {
        var validator = new OpenAiProviderOptionsValidator();

        var result = validator.Validate(Name, new OpenAiProviderOptions { BaseUrl = "http://api.example.test/v1" });

        Assert.True(result.Failed);
        Assert.Contains("is not allowed", result.FailureMessage!);
    }

    [Fact]
    public void OpenAiProviderOptions_Accepts_Default_And_Loopback_BaseUrl()
    {
        var validator = new OpenAiProviderOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new OpenAiProviderOptions { ApiKey = FakeProviderKey }).Failed);
        Assert.False(validator.Validate(
            Name,
            new OpenAiProviderOptions { BaseUrl = "http://localhost:9091/v1" }).Failed);
    }

    // ------------------------------------------------------------ PreviewOptions

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PreviewOptions_Rejects_NonPositive_Daily_Cap(int value)
    {
        var validator = new PreviewOptionsValidator();

        var result = validator.Validate(Name, new PreviewOptions { MaxPreviewsPerDayPerTenant = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxPreviewsPerDayPerTenant", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void PreviewOptions_Rejects_NonPositive_Minute_Cap(int value)
    {
        var validator = new PreviewOptionsValidator();

        var result = validator.Validate(Name, new PreviewOptions { MaxPreviewsPerMinutePerTenant = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxPreviewsPerMinutePerTenant", result.FailureMessage!);
    }

    [Fact]
    public void PreviewOptions_Accepts_Minimum_Caps_And_Video_Flag_Toggle()
    {
        var validator = new PreviewOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new PreviewOptions { MaxPreviewsPerDayPerTenant = 1, MaxPreviewsPerMinutePerTenant = 1, VideoPreviewEnabled = true }).Failed);
    }

    // --------------------------------------------------------- ProviderOptions

    [Fact]
    public void ProviderOptions_Rejects_Null_And_Empty_DefaultProvider()
    {
        var validator = new ProviderOptionsValidator();

        Assert.True(validator.Validate(Name, new ProviderOptions { DefaultProvider = string.Empty }).Failed);
        Assert.True(validator.Validate(Name, new ProviderOptions { DefaultProvider = null! }).Failed);
    }

    [Fact]
    public void ProviderOptions_Rejects_Null_RoutePriority()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(Name, new ProviderOptions { RoutePriority = null! });

        Assert.True(result.Failed);
        Assert.Contains("RoutePriority", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Blank_Route_Capability_Key()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { [" "] = ["mock"] },
            });

        Assert.True(result.Failed);
        Assert.Contains("empty capability names", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Empty_Route_Provider_List()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["tts"] = [] },
            });

        Assert.True(result.Failed);
        Assert.Contains("at least one provider", result.FailureMessage!);

        var nullList = validator.Validate(
            Name,
            new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["tts"] = null! },
            });

        Assert.True(nullList.Failed);
        Assert.Contains("at least one provider", nullList.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Blank_Provider_Name_In_Route()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["tts"] = ["mock", " "] },
            });

        Assert.True(result.Failed);
        Assert.Contains("empty provider names", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Null_Enabled_Map()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(Name, new ProviderOptions { Enabled = null! });

        Assert.True(result.Failed);
        Assert.Contains("Enabled", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Blank_Enabled_Key()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions { Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { [""] = true } });

        Assert.True(result.Failed);
        Assert.Contains("empty provider names", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Null_Descriptor_Collection()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(Name, new ProviderOptions { Descriptors = null! });

        Assert.True(result.Failed);
        Assert.Contains("Descriptors", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Null_Descriptor_Entry()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(Name, new ProviderOptions { Descriptors = [null!] });

        Assert.True(result.Failed);
        Assert.Contains("null entries", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Unknown_Descriptor_Provider()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions { Descriptors = [new ProviderDescriptorOption { Provider = "wat" }] });

        Assert.True(result.Failed);
        Assert.Contains("unknown provider", result.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Rejects_Unknown_Descriptor_Capability()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions { Descriptors = [new ProviderDescriptorOption { Capability = "NotACapability" }] });

        Assert.True(result.Failed);
        Assert.Contains("unknown capability", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ProviderOptions_Rejects_Blank_Descriptor_Model(string model)
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions { Descriptors = [new ProviderDescriptorOption { Model = model }] });

        Assert.True(result.Failed);
        Assert.Contains("model must not be empty", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ProviderOptions_Rejects_Descriptor_Version_Below_One(int version)
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions { Descriptors = [new ProviderDescriptorOption { Version = version }] });

        Assert.True(result.Failed);
        Assert.Contains("version must be >= 1", result.FailureMessage!);
    }

    [Theory]
    [InlineData(-1, 0, "limits")]
    [InlineData(0, -1, "limits")]
    public void ProviderOptions_Rejects_Negative_Descriptor_Limits(long maxInputBytes, int maxDurationMs, string expected)
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions
            {
                Descriptors =
                [
                    new ProviderDescriptorOption
                    {
                        MaxInputBytes = maxInputBytes,
                        MaxDurationMs = maxDurationMs,
                    },
                ],
            });

        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ProviderOptions_Rejects_Blank_Descriptor_Region_Or_PrivacyClass(string blank)
    {
        var validator = new ProviderOptionsValidator();

        var blankRegion = validator.Validate(
            Name,
            new ProviderOptions
            {
                Descriptors = [new ProviderDescriptorOption { Region = blank, PrivacyClass = "standard" }],
            });

        Assert.True(blankRegion.Failed);
        Assert.Contains("region/privacyClass", blankRegion.FailureMessage!);

        var blankPrivacyClass = validator.Validate(
            Name,
            new ProviderOptions
            {
                Descriptors = [new ProviderDescriptorOption { PrivacyClass = blank }],
            });

        Assert.True(blankPrivacyClass.Failed);
        Assert.Contains("region/privacyClass", blankPrivacyClass.FailureMessage!);
    }

    [Fact]
    public void ProviderOptions_Accepts_A_Complete_Descriptor_And_Aliases()
    {
        var validator = new ProviderOptionsValidator();

        var descriptor = new ProviderDescriptorOption
        {
            Provider = "local",
            Capability = "Vad",
            Model = "vad-1",
            Version = 1,
            SupportedLanguages = ["en"],
            SupportedFormats = ["wav"],
            MaxInputBytes = 0,
            MaxDurationMs = 0,
            WordTimestamps = true,
            Diarization = true,
            VoiceCloning = true,
            Region = "eu-central-1",
            PrivacyClass = "restricted",
        };

        var result = validator.Validate(Name, new ProviderOptions { Descriptors = [descriptor] });

        Assert.False(result.Failed);
        Assert.True(descriptor.WordTimestamps);
        Assert.True(descriptor.Diarization);
        Assert.True(descriptor.VoiceCloning);
    }

    [Fact]
    public void ProviderOptions_Requires_Azure_Key_When_Azure_Is_Enabled()
    {
        var validator = new ProviderOptionsValidator();

        var missing = validator.Validate(
            Name,
            new ProviderOptions { Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { ["azure"] = true } });
        Assert.True(missing.Failed);
        Assert.Contains("AzureApiKey", missing.FailureMessage!);

        var present = validator.Validate(
            Name,
            new ProviderOptions
            {
                Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { ["azure"] = true },
                AzureApiKey = FakeProviderKey,
            });
        Assert.False(present.Failed);
    }

    [Fact]
    public void ProviderOptions_Requires_OpenAi_Key_When_OpenAi_Is_Routed()
    {
        var validator = new ProviderOptionsValidator();

        var missing = validator.Validate(
            Name,
            new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["transcription"] = ["openai"] },
            });
        Assert.True(missing.Failed);
        Assert.Contains("OpenAIApiKey", missing.FailureMessage!);

        var present = validator.Validate(
            Name,
            new ProviderOptions
            {
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["transcription"] = ["openai"] },
                OpenAIApiKey = FakeProviderKey,
            });
        Assert.False(present.Failed);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("gemini")]
    [InlineData("Google")]
    public void ProviderOptions_Requires_Google_Key_For_Google_Or_Gemini(string providerName)
    {
        var validator = new ProviderOptionsValidator();

        var missing = validator.Validate(
            Name,
            new ProviderOptions
            {
                Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { [providerName] = true },
            });
        Assert.True(missing.Failed);
        Assert.Contains("GoogleApiKey", missing.FailureMessage!);

        var present = validator.Validate(
            Name,
            new ProviderOptions
            {
                Enabled = new Dictionary<string, bool>(StringComparer.Ordinal) { [providerName] = true },
                GoogleApiKey = FakeProviderKey,
            });
        Assert.False(present.Failed);
    }

    [Fact]
    public void ProviderOptions_Ignores_Providers_Enabled_False_And_Not_Routed()
    {
        var validator = new ProviderOptionsValidator();

        var result = validator.Validate(
            Name,
            new ProviderOptions
            {
                Enabled = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["azure"] = false,
                    ["openai"] = false,
                    ["google"] = false,
                },
                RoutePriority = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["transcription"] = ["mock"],
                },
            });

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("mock", ProviderType.Mock)]
    [InlineData("Mock", ProviderType.Mock)]
    [InlineData("azure", ProviderType.Azure)]
    [InlineData("openai", ProviderType.OpenAI)]
    [InlineData("google", ProviderType.Google)]
    [InlineData("localinference", ProviderType.LocalInference)]
    [InlineData("LOCAL", ProviderType.LocalInference)]
    [InlineData("gemini", ProviderType.Google)]
    [InlineData("  Gemini  ", ProviderType.Google)]
    public void TryParseProvider_Resolves_Names_And_Aliases(string name, ProviderType expected)
    {
        Assert.True(ProviderOptionNames.TryParseProvider(name, out var provider));
        Assert.Equal(expected, provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wat")]
    [InlineData("42")] // parses numerically but is not a defined member
    public void TryParseProvider_Rejects_Unknown_Or_Blank_Names(string? name)
    {
        Assert.False(ProviderOptionNames.TryParseProvider(name, out _));
    }

    [Fact]
    public void NormalizeProvider_Lowercases_The_Enum_Name()
    {
        Assert.Equal("mock", ProviderOptionNames.NormalizeProvider(ProviderType.Mock));
        Assert.Equal("openai", ProviderOptionNames.NormalizeProvider(ProviderType.OpenAI));
        Assert.Equal("localinference", ProviderOptionNames.NormalizeProvider(ProviderType.LocalInference));
    }

    // ------------------------------------------------------------- QuotaOptions

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void QuotaOptions_Rejects_NonPositive_Active_Projects(int value)
    {
        var validator = new QuotaOptionsValidator();

        var result = validator.Validate(Name, new QuotaOptions { MaxActiveProjects = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxActiveProjects", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QuotaOptions_Rejects_NonPositive_Daily_Projects(int value)
    {
        var validator = new QuotaOptionsValidator();

        var result = validator.Validate(Name, new QuotaOptions { MaxProjectsPerDay = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxProjectsPerDay", result.FailureMessage!);
    }

    [Fact]
    public void QuotaOptions_Rejects_NonPositive_Project_Cost()
    {
        var validator = new QuotaOptionsValidator();

        Assert.True(validator.Validate(Name, new QuotaOptions { MaxCostPerProject = 0.0 }).Failed);
        Assert.True(validator.Validate(Name, new QuotaOptions { MaxCostPerProject = -1.0 }).Failed);
        Assert.True(validator.Validate(Name, new QuotaOptions { MaxCostPerProject = double.NaN }).Failed);
    }

    [Fact]
    public void QuotaOptions_Rejects_NonPositive_Segment_Cost()
    {
        var validator = new QuotaOptionsValidator();

        Assert.True(validator.Validate(Name, new QuotaOptions { MaxCostPerSegment = 0.0 }).Failed);
        Assert.True(validator.Validate(Name, new QuotaOptions { MaxCostPerSegment = double.NaN }).Failed);
    }

    [Fact]
    public void QuotaOptions_Rejects_Segment_Cost_Above_Project_Cost()
    {
        var validator = new QuotaOptionsValidator();

        // Same rule as OptionsValidationTests.Invalid_QuotaOptions_Fails_When_Segment_Cost_Exceeds_Project_Cost
        // (1.0/2.0 there); different values here.
        var result = validator.Validate(
            Name,
            new QuotaOptions { MaxCostPerProject = 10.0, MaxCostPerSegment = 25.0 });

        Assert.True(result.Failed);
        Assert.Contains("must not exceed", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-9)]
    public void QuotaOptions_Rejects_NonPositive_Segment_Count(int value)
    {
        var validator = new QuotaOptionsValidator();

        var result = validator.Validate(Name, new QuotaOptions { MaxSegmentCount = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxSegmentCount", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void QuotaOptions_Rejects_NonPositive_Storage_Bytes(long value)
    {
        var validator = new QuotaOptionsValidator();

        var result = validator.Validate(Name, new QuotaOptions { MaxStorageBytes = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxStorageBytes", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void QuotaOptions_Rejects_NonPositive_Concurrent_Stages(int value)
    {
        var validator = new QuotaOptionsValidator();

        var result = validator.Validate(Name, new QuotaOptions { MaxConcurrentStagesPerTenant = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxConcurrentStagesPerTenant", result.FailureMessage!);
    }

    [Fact]
    public void QuotaOptions_Accepts_Cost_Equality_And_Minimum_Storage()
    {
        var validator = new QuotaOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new QuotaOptions { MaxCostPerProject = 2.0, MaxCostPerSegment = 2.0, MaxStorageBytes = 1 }).Failed);
        Assert.False(validator.Validate(
            Name,
            new QuotaOptions { MaxActiveProjects = 1, MaxProjectsPerDay = 1, MaxSegmentCount = 1, MaxConcurrentStagesPerTenant = 1 }).Failed);
    }

    // ---------------------------------------------------------- SecurityOptions

    [Fact]
    public void SecurityOptions_Rejects_Mtls_Without_CaPath()
    {
        var validator = new SecurityOptionsValidator();

        var result = validator.Validate(Name, new SecurityOptions { MtlsEnabled = true });

        Assert.True(result.Failed);
        Assert.Contains("CaPath", result.FailureMessage!);
    }

    [Fact]
    public void SecurityOptions_Rejects_Mtls_Without_CertPath()
    {
        var validator = new SecurityOptionsValidator();

        var result = validator.Validate(
            Name,
            new SecurityOptions { MtlsEnabled = true, CaPath = "/etc/ssl/certs/CHANGE_ME-ca.crt" });

        Assert.True(result.Failed);
        Assert.Contains("CertPath", result.FailureMessage!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SecurityOptions_Rejects_Blank_Mtls_Paths(string path)
    {
        var validator = new SecurityOptionsValidator();

        Assert.True(validator.Validate(
            Name,
            new SecurityOptions { MtlsEnabled = true, CaPath = path, CertPath = "/etc/ssl/certs/CHANGE_ME.pfx" }).Failed);
        Assert.True(validator.Validate(
            Name,
            new SecurityOptions { MtlsEnabled = true, CaPath = "/etc/ssl/certs/CHANGE_ME-ca.crt", CertPath = path }).Failed);
    }

    [Fact]
    public void SecurityOptions_Accepts_Mtls_With_Both_Paths_And_Ignores_Paths_When_Disabled()
    {
        var validator = new SecurityOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new SecurityOptions
            {
                MtlsEnabled = true,
                CaPath = "/etc/ssl/certs/CHANGE_ME-ca.crt",
                CertPath = "/etc/ssl/certs/CHANGE_ME.pfx",
            }).Failed);
        Assert.False(validator.Validate(Name, new SecurityOptions { MtlsEnabled = false }).Failed);
    }

    // ---------------------------------------------------------- SegmentOptions

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void SegmentOptions_Rejects_MergePauseMs_Outside_0_to_5000(int value)
    {
        var validator = new SegmentOptionsValidator();

        var result = validator.Validate(Name, new SegmentOptions { MergePauseMs = value });

        Assert.True(result.Failed);
        Assert.Contains("MergePauseMs", result.FailureMessage!);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(300001)]
    public void SegmentOptions_Rejects_MaxSegmentMs_Outside_1000_to_300000(int value)
    {
        var validator = new SegmentOptionsValidator();

        var result = validator.Validate(Name, new SegmentOptions { MaxSegmentMs = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxSegmentMs", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000001)]
    public void SegmentOptions_Rejects_MaxSegments_Outside_1_to_1000000(int value)
    {
        var validator = new SegmentOptionsValidator();

        var result = validator.Validate(Name, new SegmentOptions { MaxSegments = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxSegments", result.FailureMessage!);
    }

    [Fact]
    public void SegmentOptions_Accepts_All_Boundaries()
    {
        var validator = new SegmentOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new SegmentOptions { MergePauseMs = 0, MaxSegmentMs = 1000, MaxSegments = 1 }).Failed);
        Assert.False(validator.Validate(
            Name,
            new SegmentOptions { MergePauseMs = 5000, MaxSegmentMs = 300000, MaxSegments = 1000000 }).Failed);
    }

    // ------------------------------------------------------- TranslationOptions

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void TranslationOptions_Rejects_MaxCandidates_Outside_1_to_10(int value)
    {
        var validator = new TranslationOptionsValidator();

        var result = validator.Validate(Name, new TranslationOptions { MaxCandidates = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxCandidates", result.FailureMessage!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public void TranslationOptions_Rejects_MaxTokens_Outside_1_to_100000(int value)
    {
        var validator = new TranslationOptionsValidator();

        var result = validator.Validate(Name, new TranslationOptions { MaxTokens = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxTokens", result.FailureMessage!);
    }

    [Fact]
    public void TranslationOptions_Rejects_MaxCost_Outside_0_to_100000()
    {
        var validator = new TranslationOptionsValidator();

        Assert.True(validator.Validate(Name, new TranslationOptions { MaxCost = double.NaN }).Failed);
        Assert.True(validator.Validate(Name, new TranslationOptions { MaxCost = -0.01 }).Failed);
        Assert.True(validator.Validate(Name, new TranslationOptions { MaxCost = 100000.01 }).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void TranslationOptions_Rejects_MaxWallClockSec_Outside_1_to_600(int value)
    {
        var validator = new TranslationOptionsValidator();

        var result = validator.Validate(Name, new TranslationOptions { MaxWallClockSec = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxWallClockSec", result.FailureMessage!);
    }

    [Fact]
    public void TranslationOptions_Rejects_QualityThreshold_Outside_0_to_1()
    {
        var validator = new TranslationOptionsValidator();

        Assert.True(validator.Validate(Name, new TranslationOptions { QualityThreshold = double.NaN }).Failed);
        Assert.True(validator.Validate(Name, new TranslationOptions { QualityThreshold = -0.1 }).Failed);
        Assert.True(validator.Validate(Name, new TranslationOptions { QualityThreshold = 1.1 }).Failed);
    }

    [Fact]
    public void TranslationOptions_Accepts_All_Boundaries()
    {
        var validator = new TranslationOptionsValidator();

        Assert.False(validator.Validate(
            Name,
            new TranslationOptions
            {
                MaxCandidates = 1,
                MaxTokens = 1,
                MaxCost = 0.0,
                MaxWallClockSec = 1,
                QualityThreshold = 0.0,
            }).Failed);
        Assert.False(validator.Validate(
            Name,
            new TranslationOptions
            {
                MaxCandidates = 10,
                MaxTokens = 100000,
                MaxCost = 100000.0,
                MaxWallClockSec = 600,
                QualityThreshold = 1.0,
            }).Failed);
    }

    // -------------------------------------------------------------- TtsOptions

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(11)]
    public void TtsOptions_Rejects_MaxAttempts_Outside_1_to_10(int value)
    {
        var validator = new TtsOptionsValidator();

        var result = validator.Validate(Name, new TtsOptions { MaxAttempts = value });

        Assert.True(result.Failed);
        Assert.Contains("MaxAttempts", result.FailureMessage!);
    }

    [Fact]
    public void TtsOptions_Accepts_Attempt_Bounds_And_Flag_Toggles()
    {
        var validator = new TtsOptionsValidator();

        Assert.False(validator.Validate(Name, new TtsOptions { MaxAttempts = 1, EstimatorEnabled = false, PreviewEnabled = false }).Failed);
        Assert.False(validator.Validate(Name, new TtsOptions { MaxAttempts = 10 }).Failed);
    }

    // --------------------------------------------------------- DataAnnotations

    [Fact]
    public void DataAnnotations_Accept_Every_Default_Instance()
    {
        object[] defaults =
        [
            new AuthOptions(),
            new AuthRateLimitOptions(),
            new AzureProviderOptions(),
            new CorsOptions(),
            new DeploymentOptions(),
            new GoogleProviderOptions(),
            new LocalInferenceOptions(),
            new LocalInferenceModelOptions(),
            new MixingOptions(),
            new MockBehaviorOptions(),
            new OpenAiProviderOptions(),
            new PreviewOptions(),
            new ProviderOptions(),
            new ProviderDescriptorOption(),
            new QuotaOptions(),
            new SecurityOptions(),
            new SegmentOptions(),
            new TranslationOptions(),
            new TtsOptions(),
        ];

        foreach (var instance in defaults)
        {
            Assert.True(
                DataAnnotationsPass(instance),
                $"{instance.GetType().Name} defaults must satisfy its DataAnnotations.");
        }
    }

    [Fact]
    public void DataAnnotations_Reject_OutOfRange_Required_And_Length_Values()
    {
        // Auth
        Assert.False(DataAnnotationsPass(new AuthOptions { Authority = new string('a', 2049) }));
        Assert.False(DataAnnotationsPass(new AuthOptions { Audience = null! }));
        Assert.False(DataAnnotationsPass(new AuthOptions { Audience = new string('a', 257) }));
        Assert.False(DataAnnotationsPass(new AuthOptions { SigningKey = new string('k', 513) }));

        // AuthRateLimit
        Assert.False(DataAnnotationsPass(new AuthRateLimitOptions { LoginPerMinutePerIp = 0 }));
        Assert.False(DataAnnotationsPass(new AuthRateLimitOptions { LoginPerMinutePerIp = 10001 }));
        Assert.False(DataAnnotationsPass(new AuthRateLimitOptions { RefreshPerMinutePerUser = 0 }));
        Assert.False(DataAnnotationsPass(new AuthRateLimitOptions { RefreshPerMinutePerUser = 10001 }));

        // Azure
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { SpeechKey = new string('k', 513) }));
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { SpeechRegion = new string('r', 129) }));
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { TranslatorKey = new string('k', 513) }));
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { TranslatorRegion = new string('r', 129) }));
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { SpeechBaseUrl = new string('u', 2049) }));
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { TranslatorBaseUrl = new string('u', 2049) }));
        Assert.False(DataAnnotationsPass(new AzureProviderOptions { ApiVersion = new string('v', 33) }));

        // Deployment
        Assert.False(DataAnnotationsPass(new DeploymentOptions { Environment = null! }));
        Assert.False(DataAnnotationsPass(new DeploymentOptions { Environment = new string('e', 65) }));
        Assert.False(DataAnnotationsPass(new DeploymentOptions { Region = new string('r', 65) }));

        // Google
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { ApiKey = new string('k', 513) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { ProjectId = new string('p', 129) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { Location = new string('l', 129) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { GeminiModel = new string('g', 129) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { SpeechBaseUrl = new string('u', 2049) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { TranslateBaseUrl = new string('u', 2049) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { GeminiBaseUrl = new string('u', 2049) }));
        Assert.False(DataAnnotationsPass(new GoogleProviderOptions { TtsBaseUrl = new string('u', 2049) }));

        // LocalInference
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { Endpoint = new string('e', 2049) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { Protocol = new string('p', 17) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { ModelName = new string('m', 129) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { ModelVersion = new string('v', 65) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { ModelHash = new string('h', 129) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { Device = new string('d', 65) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { MaxConcurrency = 0 }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { MaxConcurrency = 17 }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { WarmupTimeoutSec = 0 }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { WarmupTimeoutSec = 301 }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { ClientCertificatePath = new string('p', 1025) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceOptions { ClientCertificatePassword = new string('p', 513) }));

        // LocalInferenceModel
        Assert.False(DataAnnotationsPass(new LocalInferenceModelOptions { Id = new string('i', 129) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceModelOptions { Version = new string('v', 65) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceModelOptions { ArtifactHash = new string('h', 129) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceModelOptions { DeviceProfile = new string('d', 65) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceModelOptions { Capability = new string('c', 65) }));
        Assert.False(DataAnnotationsPass(new LocalInferenceModelOptions { RuntimeRequirements = new string('r', 1025) }));

        // Mixing
        Assert.False(DataAnnotationsPass(new MixingOptions { FadeMs = -1 }));
        Assert.False(DataAnnotationsPass(new MixingOptions { FadeMs = 2001 }));

        // OpenAI
        Assert.False(DataAnnotationsPass(new OpenAiProviderOptions { ApiKey = new string('k', 513) }));
        Assert.False(DataAnnotationsPass(new OpenAiProviderOptions { Model = new string('m', 129) }));
        Assert.False(DataAnnotationsPass(new OpenAiProviderOptions { ChatModel = new string('m', 129) }));
        Assert.False(DataAnnotationsPass(new OpenAiProviderOptions { TtsModel = new string('m', 129) }));
        Assert.False(DataAnnotationsPass(new OpenAiProviderOptions { BaseUrl = new string('u', 2049) }));

        // Preview
        Assert.False(DataAnnotationsPass(new PreviewOptions { MaxPreviewsPerDayPerTenant = 0 }));
        Assert.False(DataAnnotationsPass(new PreviewOptions { MaxPreviewsPerMinutePerTenant = 0 }));

        // Provider
        Assert.False(DataAnnotationsPass(new ProviderOptions { DefaultProvider = null! }));
        Assert.False(DataAnnotationsPass(new ProviderOptions { DefaultProvider = new string('p', 129) }));
        Assert.False(DataAnnotationsPass(new ProviderOptions { AzureApiKey = new string('k', 513) }));
        Assert.False(DataAnnotationsPass(new ProviderOptions { OpenAIApiKey = new string('k', 513) }));
        Assert.False(DataAnnotationsPass(new ProviderOptions { GoogleApiKey = new string('k', 513) }));

        // Quota
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxActiveProjects = 0 }));
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxProjectsPerDay = 0 }));
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxCostPerProject = 0.0 }));
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxCostPerSegment = 0.0 }));
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxSegmentCount = 0 }));
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxStorageBytes = 0 }));
        Assert.False(DataAnnotationsPass(new QuotaOptions { MaxConcurrentStagesPerTenant = 0 }));

        // Security
        Assert.False(DataAnnotationsPass(new SecurityOptions { CaPath = new string('p', 513) }));
        Assert.False(DataAnnotationsPass(new SecurityOptions { CertPath = new string('p', 513) }));

        // Segment
        Assert.False(DataAnnotationsPass(new SegmentOptions { MergePauseMs = -1 }));
        Assert.False(DataAnnotationsPass(new SegmentOptions { MaxSegmentMs = 999 }));
        Assert.False(DataAnnotationsPass(new SegmentOptions { MaxSegments = 0 }));

        // Translation
        Assert.False(DataAnnotationsPass(new TranslationOptions { MaxCandidates = 0 }));
        Assert.False(DataAnnotationsPass(new TranslationOptions { MaxTokens = 0 }));
        Assert.False(DataAnnotationsPass(new TranslationOptions { MaxCost = -1.0 }));
        Assert.False(DataAnnotationsPass(new TranslationOptions { MaxWallClockSec = 0 }));
        Assert.False(DataAnnotationsPass(new TranslationOptions { QualityThreshold = 1.1 }));

        // Tts
        Assert.False(DataAnnotationsPass(new TtsOptions { MaxAttempts = 0 }));
    }

    /// <summary>
    /// Runs <see cref="Validator.TryValidateObject(object, ValidationContext, ICollection{ValidationResult}, bool)"/>
    /// for every annotated property of <paramref name="instance"/>.
    /// </summary>
    private static bool DataAnnotationsPass(object instance)
    {
        return Validator.TryValidateObject(
            instance,
            new ValidationContext(instance),
            validationResults: null,
            validateAllProperties: true);
    }
}
