// Task 039C: voice-preview quota and consent unit gap closure.

using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Voices;

namespace DubbingPlatform.UnitTests.Voices;

/// <summary>
/// Per-exception contract for <c>VoiceApiExceptions.cs</c>: every type's
/// catalogued code, the HTTP status derived from it, and its public message.
/// <see cref="VoiceCompatibilityTests"/> already pins the incompatible-voice
/// happy path and its <c>voiceId</c> detail, so this file covers the other
/// four types plus the reasons-collection shape. Pure value carriers: no HTTP,
/// no database.
/// </summary>
public sealed class VoiceApiExceptionsTests
{
    [Fact]
    public void Incompatible_Carries_The_Full_Reason_List()
    {
        string[] reasons = ["LANGUAGE_MISMATCH: es != fr", "CONSENT_REQUIRED: no covering consent"];
        var ex = new VoiceIncompatibleException("mock-voice-1", reasons);

        Assert.Equal(ErrorCodes.VoiceIncompatible, ex.ErrorCode);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("Voice 'mock-voice-1' is incompatible with this project.", ex.Message);
        Assert.Equal(reasons, ex.Reasons);

        var details = ex.GetErrorDetails();
        Assert.Equal("mock-voice-1", details["voiceId"]);
        Assert.Equal(reasons, Assert.IsType<string[]>(details["reasons"]));
    }

    [Fact]
    public void Incompatible_Normalises_A_Null_Reason_List_To_Empty()
    {
        var ex = new VoiceIncompatibleException("mock-voice-1", null!);

        Assert.Empty(ex.Reasons);
        Assert.Empty(Assert.IsType<string[]>(ex.GetErrorDetails()["reasons"]));
    }

    [Fact]
    public void Incompatible_Accepts_An_Empty_Reason_List()
    {
        var ex = new VoiceIncompatibleException("mock-voice-1", []);

        Assert.Empty(ex.Reasons);
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Consent_Required_Code_Status_And_Message()
    {
        var message = "VOICE_CONSENT_REQUIRED: voice 'cloned-1' requires recorded consent before preview.";
        var ex = new VoiceConsentRequiredException(message);

        Assert.Equal(ErrorCodes.VoiceConsentRequired, ex.ErrorCode);
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Preview_Quota_Exceeded_Code_Status_And_Message()
    {
        var message = "PREVIEW_QUOTA_EXCEEDED: daily cap of 200 previews exceeded; no provider call was made.";
        var ex = new PreviewQuotaExceededException(message);

        Assert.Equal(ErrorCodes.PreviewQuotaExceeded, ex.ErrorCode);
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Voice_Not_Found_Code_Status_And_Message()
    {
        var message = "Voice 'voice_missing' was not found.";
        var ex = new VoiceNotFoundException(message);

        Assert.Equal(ErrorCodes.VoiceNotFound, ex.ErrorCode);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Preview_Text_Invalid_Code_Status_And_Message()
    {
        var message = "PREVIEW_TEXT_INVALID: preview text must be at most 500 chars.";
        var ex = new PreviewTextInvalidException(message);

        Assert.Equal(ErrorCodes.PreviewTextInvalid, ex.ErrorCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Only_The_Incompatible_Variant_Carries_Envelope_Details()
    {
        Assert.IsAssignableFrom<IErrorDetailsProvider>(new VoiceIncompatibleException("v", ["x"]));

        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(VoiceConsentRequiredException)));
        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(PreviewQuotaExceededException)));
        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(VoiceNotFoundException)));
        Assert.False(typeof(IErrorDetailsProvider).IsAssignableFrom(typeof(PreviewTextInvalidException)));
    }

    [Fact]
    public void Every_Code_Is_Catalogued_Status_Matches_And_None_Alias()
    {
        AppException[] exceptions =
        [
            new VoiceIncompatibleException("v", ["x"]),
            new VoiceConsentRequiredException("x"),
            new PreviewQuotaExceededException("x"),
            new VoiceNotFoundException("x"),
            new PreviewTextInvalidException("x"),
        ];

        foreach (var exception in exceptions)
        {
            Assert.True(ErrorCodes.IsKnown(exception.ErrorCode), exception.ErrorCode);
            Assert.Equal(ErrorCodes.StatusFor(exception.ErrorCode), exception.StatusCode);
        }

        Assert.Equal(5, exceptions.Select(e => e.ErrorCode).Distinct(StringComparer.Ordinal).Count());

        // 422 / 403 / 429 / 404 / 400 stay distinguishable so a client can branch
        // on "incompatible" vs "needs consent" vs "try later".
        Assert.Equal(422, exceptions[0].StatusCode);
        Assert.Equal(403, exceptions[1].StatusCode);
        Assert.Equal(429, exceptions[2].StatusCode);
        Assert.Equal(404, exceptions[3].StatusCode);
        Assert.Equal(400, exceptions[4].StatusCode);
    }
}
