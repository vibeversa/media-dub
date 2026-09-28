// Task 039C: DTO request-validation unit gap closure.
using DubbingPlatform.Api.Models;
using DubbingPlatform.Api.Validation;

namespace DubbingPlatform.UnitTests.Validation;

/// <summary>
/// Fail-fast 400 validation for the project, upload, part-URL, and export
/// request bodies. Every documented bound is asserted on both sides, including
/// the negative boundary cases that must not slip through. In-process
/// FluentValidation only — no container, no network, no database.
/// </summary>
public sealed class DtoValidatorTests
{
    [Theory]
    [InlineData("en", "es")]
    [InlineData("en", "spa")]
    [InlineData("eng", "es")]
    [InlineData("EN", "es")]
    [InlineData("En", "Esm")]
    public void CreateProject_AcceptsTwoOrThreeLetterCodes(string source, string target)
    {
        var result = new CreateProjectRequestValidator().Validate(new CreateProjectRequest
        {
            SourceLanguage = source,
            TargetLanguage = target,
        });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", "es", "SourceLanguage must not be empty.")]
    [InlineData("   ", "es", "SourceLanguage must not be empty.")]
    [InlineData("e", "es", "SourceLanguage must be a 2-3 letter code.")]
    [InlineData("abcd", "es", "SourceLanguage must be a 2-3 letter code.")]
    [InlineData("e1", "es", "SourceLanguage must be a 2-3 letter code.")]
    [InlineData("12", "es", "SourceLanguage must be a 2-3 letter code.")]
    [InlineData("en", "", "TargetLanguage must not be empty.")]
    [InlineData("en", "   ", "TargetLanguage must not be empty.")]
    [InlineData("en", "s", "TargetLanguage must be a 2-3 letter code.")]
    [InlineData("en", "spain", "TargetLanguage must be a 2-3 letter code.")]
    [InlineData("en", "e5", "TargetLanguage must be a 2-3 letter code.")]
    public void CreateProject_RejectsBadLanguageCodes_WithDocumentedMessages(string source, string target, string expected)
    {
        var result = new CreateProjectRequestValidator().Validate(new CreateProjectRequest
        {
            SourceLanguage = source,
            TargetLanguage = target,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en", "EN")]
    [InlineData("  en  ", "en")]
    public void CreateProject_RejectsIdenticalSourceAndTarget(string source, string target)
    {
        var result = new CreateProjectRequestValidator().Validate(new CreateProjectRequest
        {
            SourceLanguage = source,
            TargetLanguage = target,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("must differ", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateUpload_AcceptsAWellFormedRequest()
    {
        var result = new CreateUploadRequestValidator().Validate(new CreateUploadRequest
        {
            FileName = "clip_01.wav",
            ContentType = "audio/wav",
            DeclaredSize = 1024,
        });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", "audio/wav", 1024, "FileName must not be empty.")]
    [InlineData("   ", "audio/wav", 1024, "FileName must not be empty.")]
    [InlineData("dir/clip.wav", "audio/wav", 1024, "FileName must not contain path traversal.")]
    [InlineData("dir\\clip.wav", "audio/wav", 1024, "FileName must not contain path traversal.")]
    [InlineData("../clip.wav", "audio/wav", 1024, "FileName must not contain path traversal.")]
    [InlineData("..\\clip.wav", "audio/wav", 1024, "FileName must not contain path traversal.")]
    [InlineData("clip.wav", "", 1024, "ContentType must not be empty.")]
    [InlineData("clip.wav", "   ", 1024, "ContentType must not be empty.")]
    [InlineData("clip.wav", "audio/wav", 0, "DeclaredSize must be at least 1 byte.")]
    [InlineData("clip.wav", "audio/wav", -1, "DeclaredSize must be at least 1 byte.")]
    public void CreateUpload_RejectsUnsafeOrUndersizedFields(string fileName, string contentType, long size, string expected)
    {
        var result = new CreateUploadRequestValidator().Validate(new CreateUploadRequest
        {
            FileName = fileName,
            ContentType = contentType,
            DeclaredSize = size,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void CreateUpload_RejectsOverlongFields()
    {
        var validator = new CreateUploadRequestValidator();

        var longName = validator.Validate(new CreateUploadRequest
        {
            FileName = new string('a', 257),
            ContentType = "audio/wav",
            DeclaredSize = 10,
        });
        Assert.False(longName.IsValid);
        Assert.Contains(longName.Errors, e => e.ErrorMessage.Contains("FileName must be at most 256", StringComparison.Ordinal));

        var longType = validator.Validate(new CreateUploadRequest
        {
            FileName = "clip.wav",
            ContentType = new string('a', 257),
            DeclaredSize = 10,
        });
        Assert.False(longType.IsValid);
        Assert.Contains(longType.Errors, e => e.ErrorMessage.Contains("ContentType must be at most 256", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateUpload_AcceptsExactBoundaries()
    {
        var validator = new CreateUploadRequestValidator();

        Assert.True(validator.Validate(new CreateUploadRequest
        {
            FileName = new string('a', 256),
            ContentType = new string('a', 256),
            DeclaredSize = 1,
        }).IsValid);
    }

    [Theory]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 1, 2, 3 })]
    [InlineData(new[] { 10000 })]
    public void GetPartUrls_AcceptsPartNumbersInRange(int[] parts)
    {
        var result = new GetPartUrlsRequestValidator().Validate(new GetPartUrlsRequest { PartNumbers = parts });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void GetPartUrls_RejectsEmptyArray_AndPinsTheNullArrayBehaviour()
    {
        var validator = new GetPartUrlsRequestValidator();

        var emptyResult = validator.Validate(new GetPartUrlsRequest { PartNumbers = [] });
        Assert.False(emptyResult.IsValid);
        Assert.Contains(emptyResult.Errors, e => e.ErrorMessage.Contains("1..1000", StringComparison.Ordinal));

        // Documented residual: a null array is reported by the NotNull rule, but
        // the RuleForEach collection rule then dereferences it. Pinned here so the
        // behaviour cannot change silently.
        Assert.Throws<NullReferenceException>(() => validator.Validate(new GetPartUrlsRequest { PartNumbers = null! }));
    }

    [Fact]
    public void GetPartUrls_RejectsTooManyParts()
    {
        var result = new GetPartUrlsRequestValidator().Validate(new GetPartUrlsRequest
        {
            PartNumbers = Enumerable.Range(1, 1001).ToArray(),
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("1..1000", StringComparison.Ordinal));
    }

    [Fact]
    public void GetPartUrls_RejectsOutOfRangePartNumbers()
    {
        var validator = new GetPartUrlsRequestValidator();

        var zero = validator.Validate(new GetPartUrlsRequest { PartNumbers = [0] });
        Assert.False(zero.IsValid);
        Assert.Contains(zero.Errors, e => e.ErrorMessage.Contains("1..10000", StringComparison.Ordinal));

        var negative = validator.Validate(new GetPartUrlsRequest { PartNumbers = [1, -5] });
        Assert.False(negative.IsValid);
        Assert.Contains(negative.Errors, e => e.ErrorMessage.Contains("1..10000", StringComparison.Ordinal));

        var tooHigh = validator.Validate(new GetPartUrlsRequest { PartNumbers = [10001] });
        Assert.False(tooHigh.IsValid);
        Assert.Contains(tooHigh.Errors, e => e.ErrorMessage.Contains("1..10000", StringComparison.Ordinal));
    }

    [Fact]
    public void GetPartUrls_AcceptsExactlyOneThousandParts()
    {
        var result = new GetPartUrlsRequestValidator().Validate(new GetPartUrlsRequest
        {
            PartNumbers = Enumerable.Range(1, 1000).ToArray(),
        });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("mp4")]
    public void CreateExport_AcceptsKebabCaseFormats(string format)
    {
        var result = new CreateExportRequestValidator().Validate(new CreateExportRequest { Format = format });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CreateExport_RejectsEmptyAndOverlongFormats()
    {
        var validator = new CreateExportRequestValidator();

        var empty = validator.Validate(new CreateExportRequest { Format = string.Empty });
        Assert.False(empty.IsValid);
        Assert.Contains(empty.Errors, e => e.ErrorMessage.Contains("Format must not be empty.", StringComparison.Ordinal));

        var blank = validator.Validate(new CreateExportRequest { Format = "   " });
        Assert.False(blank.IsValid);

        var overlong = validator.Validate(new CreateExportRequest { Format = new string('a', 65) });
        Assert.False(overlong.IsValid);
        Assert.Contains(overlong.Errors, e => e.ErrorMessage.Contains("Format must be at most 64", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateExport_AcceptsExactFormatBoundary()
    {
        var result = new CreateExportRequestValidator().Validate(new CreateExportRequest
        {
            Format = new string('a', 64),
        });

        Assert.True(result.IsValid);
    }
}
