using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;

namespace DubbingPlatform.Application.Auth;

/// <summary>
/// Auth/session/preference failures for Task 006. Each carries a catalogued
/// public code from <see cref="ErrorCodes"/> so envelopes stay structured;
/// messages carry no credential, token, or user-enumeration detail.
/// </summary>
public sealed class InvalidCredentialsException : AppException
{
    public InvalidCredentialsException(string message)
        : base(ErrorCodes.InvalidCredentials, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Refresh token expired or unknown. Frontend redirects to login.
/// </summary>
public sealed class TokenExpiredException : AppException
{
    public TokenExpiredException(string message)
        : base(ErrorCodes.TokenExpired, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Refresh-token reuse: a rotated (revoked) token was presented again.
/// The whole token family is revoked (theft detection).
/// </summary>
public sealed class TokenReusedException : AppException
{
    public TokenReusedException(string message)
        : base(ErrorCodes.TokenReused, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Authenticated user is disabled.
/// </summary>
public sealed class UserDisabledException : AppException
{
    public UserDisabledException(string message)
        : base(ErrorCodes.UserDisabled, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Preference key outside the Task 001 whitelist.
/// </summary>
public sealed class PreferenceKeyUnknownException : AppException
{
    public PreferenceKeyUnknownException(string message)
        : base(ErrorCodes.PreferenceKeyUnknown, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Preference value exceeds <c>UserPreference.MaxValueBytes</c>.
/// </summary>
public sealed class PreferenceValueTooLargeException : AppException
{
    public PreferenceValueTooLargeException(string message)
        : base(ErrorCodes.PreferenceValueTooLarge, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}

/// <summary>
/// Required tenant claim is missing or invalid.
/// </summary>
public sealed class TenantRequiredException : AppException
{
    public TenantRequiredException(string message)
        : base(ErrorCodes.TenantRequired, message)
    {
    }

    public override int StatusCode => ErrorCodes.StatusFor(ErrorCode);
}
