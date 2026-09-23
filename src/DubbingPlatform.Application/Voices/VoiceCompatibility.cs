using System.Text.Json;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Application.Voices;

/// <summary>
/// Server-side voice compatibility rule set (Task 010).
/// A voice is selectable for a project only when ALL rules pass:
/// <list type="number">
/// <item>Target-language match required (<c>VoiceProfile.Language</c> equals
/// <c>DubbingProject.TargetLanguage</c>, case-insensitive).</item>
/// <item>Sample-rate/channel floor: <c>ModelRefJson</c> may carry
/// <c>sampleRateHz</c> (or <c>sampleRate</c>/<c>sample_rate</c>) and
/// <c>channels</c> (or <c>channelCount</c>); values below
/// <see cref="MinSampleRateHz"/>/<see cref="MinChannels"/> fail. Absent
/// fields pass (backward compatible with rows written before instrumentation).
/// Project <c>SettingsJson</c> may raise the floor via
/// <c>voiceSampleRateFloor</c>/<c>voiceChannelsFloor</c>.</item>
/// <item>Cloning gate: <c>VoiceType.Cloned</c> or <c>CloningEnabled</c> voices
/// require a covering granted consent (checked by callers via
/// <c>ConsentService</c>; this rule reports the requirement structurally so
/// listing can exclude without DB access when <paramref name="hasCoveringConsent"/>
/// is false).</item>
/// <item>Style-tag allowlist: project <c>SettingsJson</c> may carry
/// <c>allowedVoiceStyles</c> (or <c>voiceStyles</c>/<c>styleAllowlist</c>);
/// when present and non-empty, a voice carrying <c>styleTags</c> (or
/// <c>styles</c>/<c>tags</c> in <c>ModelRefJson</c>) must intersect the
/// allowlist. Voices without tags pass (universal).</item>
/// <item>Gender constraint: project <c>SettingsJson</c> may carry
/// <c>allowedGenders</c>; when present, a voice carrying <c>gender</c> in
/// <c>ModelRefJson</c> must be listed. Voices without gender pass.</item>
/// </list>
/// Pure and side-effect free; all JSON parsing fails closed to "no constraint"
/// for malformed project settings but fails the voice only on explicit rule
/// violations. Never logs or carries audio, keys, or subject identity.
/// </summary>
public static class VoiceCompatibility
{
    /// <summary>Minimum acceptable sample rate in Hz.</summary>
    public const int MinSampleRateHz = 16000;

    /// <summary>Minimum acceptable channel count.</summary>
    public const int MinChannels = 1;

    /// <summary>Maximum reason length (truncation guard).</summary>
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Checks one voice against one project. Returns the list of exclusion
    /// reasons (empty means compatible).
    /// </summary>
    public static IReadOnlyList<string> Check(
        VoiceProfile voice,
        DubbingProject project,
        bool hasCoveringConsent,
        bool cloningGloballyEnabled)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(project);

        var reasons = new List<string>();

        // 1. Target-language match required.
        var voiceLang = (voice.Language ?? string.Empty).Trim();
        var targetLang = (project.TargetLanguage ?? string.Empty).Trim();
        if (!string.Equals(voiceLang, targetLang, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"LANGUAGE_MISMATCH: voice language '{voiceLang}' does not match project target '{targetLang}'.");
        }

        // 2. Sample-rate / channel floor.
        var model = ParseModelRef(voice.ModelRefJson);
        var settings = ParseSettings(project.SettingsJson);
        var rateFloor = settings.SampleRateFloor ?? MinSampleRateHz;
        var channelFloor = settings.ChannelsFloor ?? MinChannels;

        if (model.SampleRateHz.HasValue && model.SampleRateHz.Value < rateFloor)
        {
            reasons.Add($"SAMPLE_RATE_BELOW_FLOOR: voice sample rate {model.SampleRateHz.Value}Hz is below floor {rateFloor}Hz.");
        }

        if (model.Channels.HasValue && model.Channels.Value < channelFloor)
        {
            reasons.Add($"CHANNELS_BELOW_FLOOR: voice channels {model.Channels.Value} is below floor {channelFloor}.");
        }

        // 3. Cloning gate.
        var isCloning = voice.Type == VoiceType.Cloned || voice.CloningEnabled;
        if (isCloning)
        {
            if (!cloningGloballyEnabled)
            {
                reasons.Add("CLONING_DISABLED: cloned voices require Voices:CloningEnabled=true.");
            }
            else if (!hasCoveringConsent)
            {
                reasons.Add("CONSENT_REQUIRED: cloned voice requires a granted consent covering this project and voice.");
            }
        }

        // 4. Style-tag allowlist.
        if (settings.AllowedStyles.Count > 0 && model.StyleTags.Count > 0)
        {
            var allowed = new HashSet<string>(settings.AllowedStyles, StringComparer.OrdinalIgnoreCase);
            var intersects = false;
            foreach (var tag in model.StyleTags)
            {
                if (allowed.Contains(tag))
                {
                    intersects = true;
                    break;
                }
            }

            if (!intersects)
            {
                reasons.Add($"STYLE_NOT_ALLOWED: voice styles [{string.Join(",", model.StyleTags)}] are not in the project allowlist.");
            }
        }

        // 5. Gender constraint.
        if (settings.AllowedGenders.Count > 0 && !string.IsNullOrWhiteSpace(model.Gender))
        {
            var allowed = new HashSet<string>(settings.AllowedGenders, StringComparer.OrdinalIgnoreCase);
            if (!allowed.Contains(model.Gender!.Trim()))
            {
                reasons.Add($"GENDER_NOT_ALLOWED: voice gender '{model.Gender!.Trim()}' is not allowed by this project.");
            }
        }

        var truncated = new List<string>(reasons.Count);
        foreach (var reason in reasons)
        {
            truncated.Add(reason.Length > MaxReasonLength ? reason.Substring(0, MaxReasonLength) : reason);
        }

        return truncated;
    }

    /// <summary>
    /// Whether the voice structurally requires consent (cloned type or flag).
    /// Pure.
    /// </summary>
    public static bool RequiresConsent(VoiceProfile voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        return voice.Type == VoiceType.Cloned || voice.CloningEnabled;
    }

    private sealed record ModelInfo(int? SampleRateHz, int? Channels, string? Gender, IReadOnlyList<string> StyleTags);

    private sealed record SettingsInfo(
        int? SampleRateFloor,
        int? ChannelsFloor,
        IReadOnlyList<string> AllowedStyles,
        IReadOnlyList<string> AllowedGenders);

    private static ModelInfo ParseModelRef(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ModelInfo(null, null, null, []);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new ModelInfo(null, null, null, []);
            }

            var root = doc.RootElement;
            int? rate = TryInt(root, "sampleRateHz") ?? TryInt(root, "sampleRate") ?? TryInt(root, "sample_rate");
            int? channels = TryInt(root, "channels") ?? TryInt(root, "channelCount") ?? TryInt(root, "channel_count");
            string? gender = TryString(root, "gender");
            var tags = TryStringArray(root, "styleTags") ?? TryStringArray(root, "styles") ?? TryStringArray(root, "tags") ?? [];

            return new ModelInfo(rate, channels, gender, tags);
        }
        catch (JsonException)
        {
            return new ModelInfo(null, null, null, []);
        }
    }

    private static SettingsInfo ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SettingsInfo(null, null, [], []);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new SettingsInfo(null, null, [], []);
            }

            var root = doc.RootElement;
            int? rateFloor = TryInt(root, "voiceSampleRateFloor");
            int? channelFloor = TryInt(root, "voiceChannelsFloor");
            var styles = TryStringArray(root, "allowedVoiceStyles")
                ?? TryStringArray(root, "voiceStyles")
                ?? TryStringArray(root, "styleAllowlist")
                ?? [];
            var genders = TryStringArray(root, "allowedGenders") ?? [];

            return new SettingsInfo(rateFloor, channelFloor, styles, genders);
        }
        catch (JsonException)
        {
            return new SettingsInfo(null, null, [], []);
        }
    }

    private static int? TryInt(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var element))
        {
            if (element.ValueKind is JsonValueKind.Number && element.TryGetInt32(out var value))
            {
                return value;
            }

            if (element.ValueKind is JsonValueKind.String
                && int.TryParse(element.GetString()?.Trim(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? TryString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static IReadOnlyList<string>? TryStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.String)
        {
            var single = element.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        if (element.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String)
            {
                var value = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value!);
                }
            }
        }

        return result;
    }
}
