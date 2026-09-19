using System.Security.Cryptography;
using System.Text;

namespace DubbingPlatform.Infrastructure.Providers.Mock;

/// <summary>
/// Stable deterministic helpers for mocks. All randomness derives from
/// SHA-256 of the input parts (never <see cref="Random.Shared"/> or time),
/// so the same input yields byte-identical output across runs and replicas.
/// </summary>
internal static class MockDeterminism
{
    public const int SampleRateHz = 16000;

    public const double SineFrequencyHz = 440.0;

    /// <summary>
    /// Computes a stable 32-bit seed from the given parts.
    /// </summary>
    public static int StableSeed(params string?[] parts)
    {
        var hash = StableHashBytes(parts);
        return BitConverter.ToInt32(hash, 0);
    }

    /// <summary>
    /// Returns lowercase hex (default 16 chars) stably derived from parts.
    /// </summary>
    public static string StableHex(int length, params string?[] parts)
    {
        if (length <= 0 || length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be in 1..64.");
        }

        var hash = StableHashBytes(parts);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..length];
    }

    /// <summary>
    /// Creates a deterministically seeded random from parts.
    /// </summary>
    public static Random SeededRandom(params string?[] parts)
    {
        return new Random(StableSeed(parts));
    }

    /// <summary>
    /// SHA-256 hex (64 lowercase chars) of the given bytes.
    /// </summary>
    public static string ContentHash(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a deterministic 16kHz mono 16-bit PCM sine WAV.
    /// Same duration yields byte-identical output.
    /// </summary>
    public static byte[] GenerateSineWav(int durationMs)
    {
        var clamped = Math.Max(1, durationMs);
        var sampleCount = (int)((long)SampleRateHz * clamped / 1000);
        var dataBytes = sampleCount * 2;
        var result = new byte[44 + dataBytes];

        WriteAscii(result, 0, "RIFF");
        WriteInt32Le(result, 4, 36 + dataBytes);
        WriteAscii(result, 8, "WAVE");
        WriteAscii(result, 12, "fmt ");
        WriteInt32Le(result, 16, 16);
        WriteInt16Le(result, 20, 1);
        WriteInt16Le(result, 22, 1);
        WriteInt32Le(result, 24, SampleRateHz);
        WriteInt32Le(result, 28, SampleRateHz * 2);
        WriteInt16Le(result, 32, 2);
        WriteInt16Le(result, 34, 16);
        WriteAscii(result, 36, "data");
        WriteInt32Le(result, 40, dataBytes);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / SampleRateHz;
            var sample = (short)(Math.Sin(2.0 * Math.PI * SineFrequencyHz * t) * 32767.0 * 0.5);
            result[44 + (i * 2)] = (byte)(sample & 0xFF);
            result[44 + (i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return result;
    }

    /// <summary>
    /// Computes TTS duration: wordCount*400ms clamped to 500..5000ms.
    /// </summary>
    public static int TtsDurationMs(string text)
    {
        var words = SplitWords(text);
        var duration = words.Count * 400;
        return Math.Clamp(duration, 500, 5000);
    }

    /// <summary>
    /// Splits text into words (whitespace). Empty text yields one placeholder word.
    /// </summary>
    public static List<string> SplitWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ["mock"];
        }

        return text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static byte[] StableHashBytes(params string?[] parts)
    {
        var joined = string.Join("\0", parts.Select(p => p ?? string.Empty));
        return SHA256.HashData(Encoding.UTF8.GetBytes(joined));
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static void WriteInt32Le(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16Le(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
