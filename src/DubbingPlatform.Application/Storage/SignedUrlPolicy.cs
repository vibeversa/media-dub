namespace DubbingPlatform.Application.Storage;

/// <summary>
/// Signed-URL issuance policy. Downloads use short-lived presigned URLs
/// (default 15 minutes) issued only after an ownership check
/// (route project <c>TenantId</c> == <c>tid</c> claim); the check itself is
/// enforced by controllers (later tasks), this type only centralizes the expiry.
/// </summary>
public sealed record SignedUrlPolicy(TimeSpan Expiry)
{
    /// <summary>
    /// Default download expiry: 15 minutes.
    /// </summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum allowed expiry: 1 hour.
    /// </summary>
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the default policy.
    /// </summary>
    public static SignedUrlPolicy Default => new(DefaultExpiry);

    /// <summary>
    /// Validates the policy. Expiry must be in 1 minute .. 1 hour.
    /// </summary>
    public void Validate()
    {
        if (Expiry < TimeSpan.FromMinutes(1) || Expiry > MaxExpiry)
        {
            throw new ArgumentOutOfRangeException(nameof(Expiry), "Signed URL expiry must be in 1 minute .. 1 hour.");
        }
    }

    /// <summary>
    /// Resolves an effective expiry: requested value clamped to 1 minute .. 1 hour,
    /// defaulting to <see cref="DefaultExpiry"/> when null.
    /// </summary>
    public static TimeSpan Resolve(TimeSpan? requested)
    {
        if (requested is null)
        {
            return DefaultExpiry;
        }

        if (requested.Value < TimeSpan.FromMinutes(1))
        {
            return TimeSpan.FromMinutes(1);
        }

        return requested.Value > MaxExpiry ? MaxExpiry : requested.Value;
    }
}
