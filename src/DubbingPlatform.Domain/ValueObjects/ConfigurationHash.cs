namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// Hash of normalized configuration (64 lowercase hex, regex ^[0-9a-f]{64}$).
/// Distinct type from <see cref="ContentHash"/>.
/// </summary>
public sealed record ConfigurationHash
{
    public string Sha256Hex { get; init; }

    public ConfigurationHash(string sha256Hex)
    {
        if (!ContentHash.IsValidHex(sha256Hex))
        {
            throw new ArgumentException("Sha256Hex must be 64 lowercase hex chars.", nameof(sha256Hex));
        }

        Sha256Hex = sha256Hex;
    }

    public override string ToString() => Sha256Hex;
}
