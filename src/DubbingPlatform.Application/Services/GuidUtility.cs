using System.Security.Cryptography;
using System.Text;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Deterministic GUID factory for stable pipeline identity.
/// Computes SHA-1 over the UTF-8 input string, takes the first 16 bytes as the
/// GUID payload, then sets the RFC 4122 version (5, name-based SHA-1) and
/// variant (10xx) bits so outputs are valid GUIDs and never collide with random
/// <c>Guid.NewGuid()</c> rows. Same input always yields the same GUID on any
/// machine; different inputs avalanche via SHA-1. Segment ids use
/// <c>SegmentId(runId, sequence)</c> (<c>{run:N}:segment:{sequence}</c>);
/// overlap groups/members extend the same namespace
/// (<c>{run:N}:overlap:{group}</c>, <c>{run:N}:overlap:{group}:member:{order}</c>).
/// No secrets are ever passed here (run ids and sequence numbers only).
/// </summary>
public static class GuidUtility
{
    /// <summary>
    /// Deterministic GUID for one segment sequence within a run.
    /// </summary>
    public static Guid SegmentId(Guid runId, int sequence)
    {
        if (runId == Guid.Empty)
        {
            throw new Domain.Exceptions.DomainException("RunId must not be empty.");
        }

        if (sequence < 0)
        {
            throw new Domain.Exceptions.DomainException("Sequence must be >= 0.");
        }

        return From(string.Concat(runId.ToString("N"), ":segment:", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// SHA-1 name-based GUID from an arbitrary stable key. Empty keys throw.
    /// </summary>
    public static Guid From(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new Domain.Exceptions.DomainException("Key must not be empty.");
        }

        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
