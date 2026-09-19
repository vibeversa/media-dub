using DubbingPlatform.Api.Models;
using FluentValidation;

namespace DubbingPlatform.Api.Validation;

/// <summary>
/// Validators for project and upload DTOs. Domain rules (2-3 letter codes,
/// filename traversal, size bounds, part ranges) are enforced here for
/// fail-fast 400s; services re-validate for defense in depth.
/// </summary>
public sealed class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.SourceLanguage)
            .NotEmpty().WithMessage("SourceLanguage must not be empty.")
            .Length(2, 3).WithMessage("SourceLanguage must be a 2-3 letter code.")
            .Matches("^[A-Za-z]{2,3}$").WithMessage("SourceLanguage must be a 2-3 letter code.");
        RuleFor(x => x.TargetLanguage)
            .NotEmpty().WithMessage("TargetLanguage must not be empty.")
            .Length(2, 3).WithMessage("TargetLanguage must be a 2-3 letter code.")
            .Matches("^[A-Za-z]{2,3}$").WithMessage("TargetLanguage must be a 2-3 letter code.");
        RuleFor(x => x)
            .Must(x => !string.Equals(x.SourceLanguage?.Trim(), x.TargetLanguage?.Trim(), StringComparison.OrdinalIgnoreCase))
            .WithMessage("SourceLanguage and TargetLanguage must differ.");
    }
}

/// <summary>
/// Validates upload creation.
/// </summary>
public sealed class CreateUploadRequestValidator : AbstractValidator<CreateUploadRequest>
{
    public CreateUploadRequestValidator()
    {
        RuleFor(x => x.FileName)
            .NotEmpty().WithMessage("FileName must not be empty.")
            .MaximumLength(256).WithMessage("FileName must be at most 256 chars.")
            .Must(name => !name.Contains('/', StringComparison.Ordinal) && !name.Contains('\\') && !name.Contains("..", StringComparison.Ordinal))
            .WithMessage("FileName must not contain path traversal.");
        RuleFor(x => x.ContentType)
            .NotEmpty().WithMessage("ContentType must not be empty.")
            .MaximumLength(256).WithMessage("ContentType must be at most 256 chars.");
        RuleFor(x => x.DeclaredSize)
            .GreaterThan(0).WithMessage("DeclaredSize must be at least 1 byte.");
    }
}

/// <summary>
/// Validates part-URL requests.
/// </summary>
public sealed class GetPartUrlsRequestValidator : AbstractValidator<GetPartUrlsRequest>
{
    public GetPartUrlsRequestValidator()
    {
        RuleFor(x => x.PartNumbers)
            .NotNull().WithMessage("PartNumbers must not be null.")
            .Must(p => p.Length >= 1 && p.Length <= 1000).WithMessage("PartNumbers must contain 1..1000 entries.");
        RuleForEach(x => x.PartNumbers)
            .InclusiveBetween(1, 10000).WithMessage("PartNumber must be in 1..10000.");
    }
}

/// <summary>
/// Validates export creation.
/// </summary>
public sealed class CreateExportRequestValidator : AbstractValidator<CreateExportRequest>
{
    public CreateExportRequestValidator()
    {
        RuleFor(x => x.Format)
            .NotEmpty().WithMessage("Format must not be empty.")
            .MaximumLength(64).WithMessage("Format must be at most 64 chars.");
    }
}
