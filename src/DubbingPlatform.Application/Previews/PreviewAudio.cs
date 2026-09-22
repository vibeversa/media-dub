using System.Text;

namespace DubbingPlatform.Application.Previews;

/// <summary>
/// Deterministic sine-WAV writer for the preview lane. Mirrors the mock TTS
/// waveform (16kHz mono 16-bit PCM, 440Hz) so Application stays free of
/// Infrastructure references; byte-identical per duration. Pure.
/// Preview audio bytes are locally synthesized proxies, never provider
/// secrets: provider calls are retained for voice audibility checks plus
/// execution telemetry, while persisted bytes stay reproducible without a
/// provider round-trip. Only ids and durations are logged, never text.
/// </summary>
public static class PreviewAudio
{
    /// <summary>Sample rate of synthesized preview audio.</summary>
    public const int SampleRateHz = 16000;

    /// <summary>
    /// Builds a WAV for the given duration, clamped to at least 1ms.
    /// </summary>
    public static byte[] BuildWav(int durationMs)
    {
        const double frequencyHz = 440.0;
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
            var sample = (short)(Math.Sin(2.0 * Math.PI * frequencyHz * t) * 32767.0 * 0.5);
            result[44 + (i * 2)] = (byte)(sample & 0xFF);
            result[44 + (i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return result;
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
