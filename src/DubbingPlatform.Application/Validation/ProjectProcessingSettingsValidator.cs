using System.Text.Json;
using FluentValidation;

namespace DubbingPlatform.Application.Validation;

public sealed record GlossaryEntry(string? SourceTerm, string? TargetTerm, string? Notes);

public sealed record ProjectProcessingSettingsDocument(
    int SchemaVersion,
    string? SourceSeparationPolicy,
    string? OutputProfile,
    string? TimingStrictness,
    string? VoicePolicy,
    double? ReviewThreshold,
    IReadOnlyList<GlossaryEntry>? Glossary,
    string? StyleInstructions);

public sealed class ProjectProcessingSettingsValidator : AbstractValidator<string>
{
    public const string UnsupportedVersionCode = "SETTINGS_VERSION_UNSUPPORTED";

    public const int SupportedSchemaVersion = 1;

    public const int MaxGlossaryEntries = 1000;

    public ProjectProcessingSettingsValidator()
    {
        RuleFor(json => json)
            .NotEmpty().WithMessage("Processing settings must not be empty.")
            .Must(BeValidJson).WithMessage("Processing settings must be valid JSON.")
            .WithErrorCode("VALIDATION_FAILED");

        RuleFor(json => json)
            .Must(HaveSupportedSchemaVersion)
            .WithMessage($"Unsupported processing settings schema version. Supported: {SupportedSchemaVersion}.")
            .WithErrorCode(UnsupportedVersionCode)
            .When(json => BeValidJson(json));

        RuleFor(json => json)
            .Must(HaveValidShape)
            .WithMessage("Processing settings have an invalid shape.")
            .WithErrorCode("VALIDATION_FAILED")
            .When(json => BeValidJson(json) && HaveSupportedSchemaVersion(json));
    }

    public static bool BeValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HaveSupportedSchemaVersion(string? json)
    {
        if (!BeValidJson(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json!);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version))
            {
                return false;
            }

            return version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number)
                && number == SupportedSchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HaveValidShape(string? json)
    {
        if (!BeValidJson(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var name in new[] { "sourceSeparationPolicy", "outputProfile", "timingStrictness", "voicePolicy" })
            {
                if (root.TryGetProperty(name, out var value)
                    && value.ValueKind != JsonValueKind.Null
                    && value.ValueKind != JsonValueKind.Undefined)
                {
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return false;
                    }

                    if (value.GetString()!.Length > 64)
                    {
                        return false;
                    }
                }
            }

            if (root.TryGetProperty("reviewThreshold", out var threshold)
                && threshold.ValueKind != JsonValueKind.Null
                && threshold.ValueKind != JsonValueKind.Undefined)
            {
                if (threshold.ValueKind != JsonValueKind.Number || !threshold.TryGetDouble(out var number))
                {
                    return false;
                }

                if (number < 0 || number > 1)
                {
                    return false;
                }
            }

            if (root.TryGetProperty("glossary", out var glossary)
                && glossary.ValueKind != JsonValueKind.Null
                && glossary.ValueKind != JsonValueKind.Undefined)
            {
                if (glossary.ValueKind != JsonValueKind.Array || glossary.GetArrayLength() > MaxGlossaryEntries)
                {
                    return false;
                }

                foreach (var entry in glossary.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }

                    if (!entry.TryGetProperty("sourceTerm", out var source)
                        || source.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(source.GetString())
                        || source.GetString()!.Length > 256)
                    {
                        return false;
                    }

                    if (!entry.TryGetProperty("targetTerm", out var target)
                        || target.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(target.GetString())
                        || target.GetString()!.Length > 256)
                    {
                        return false;
                    }

                    if (entry.TryGetProperty("notes", out var notes)
                        && notes.ValueKind != JsonValueKind.Null
                        && notes.ValueKind != JsonValueKind.Undefined)
                    {
                        if (notes.ValueKind != JsonValueKind.String || notes.GetString()!.Length > 1024)
                        {
                            return false;
                        }
                    }
                }
            }

            if (root.TryGetProperty("styleInstructions", out var style)
                && style.ValueKind != JsonValueKind.Null
                && style.ValueKind != JsonValueKind.Undefined)
            {
                if (style.ValueKind != JsonValueKind.String || style.GetString()!.Length > 4000)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static ProjectProcessingSettingsDocument? Parse(string? json)
    {
        if (!BeValidJson(json) || !HaveSupportedSchemaVersion(json) || !HaveValidShape(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json!);
        var root = document.RootElement;

        static string? OptionalString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        static double? OptionalDouble(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                ? number
                : null;
        }

        List<GlossaryEntry>? glossary = null;
        if (root.TryGetProperty("glossary", out var glossaryElement) && glossaryElement.ValueKind == JsonValueKind.Array)
        {
            glossary = [];
            foreach (var entry in glossaryElement.EnumerateArray())
            {
                glossary.Add(new GlossaryEntry(
                    entry.GetProperty("sourceTerm").GetString(),
                    entry.GetProperty("targetTerm").GetString(),
                    entry.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String ? notes.GetString() : null));
            }
        }

        return new ProjectProcessingSettingsDocument(
            root.GetProperty("schemaVersion").GetInt32(),
            OptionalString(root, "sourceSeparationPolicy"),
            OptionalString(root, "outputProfile"),
            OptionalString(root, "timingStrictness"),
            OptionalString(root, "voicePolicy"),
            OptionalDouble(root, "reviewThreshold"),
            glossary,
            OptionalString(root, "styleInstructions"));
    }
}
