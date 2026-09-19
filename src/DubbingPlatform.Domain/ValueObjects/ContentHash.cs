namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// SHA-256 content hash as 64 lowercase hex chars (regex ^[0-9a-f]{64}$).
/// </summary>
public sealed record ContentHash
{
    public string Sha256Hex { get; init; }

    public ContentHash(string sha256Hex)
    {
        if (!IsValidHex(sha256Hex))
        {
            throw new ArgumentException("Sha256Hex must be 64 lowercase hex chars.", nameof(sha256Hex));
        }

        Sha256Hex = sha256Hex;
    }

    internal static bool IsValidHex(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isDigit = c >= '0' && c <= '9';
            var isHex = c >= 'a' && c <= 'f';
            if (!isDigit && !isHex)
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Sha256Hex;
}
