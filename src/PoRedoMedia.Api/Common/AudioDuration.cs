namespace PoRedoMedia.Api.Common;

/// <summary>
/// Reads the playing time of an MP3 or WAV clip from its bytes.
/// </summary>
/// <remarks>
/// The seed manifest ships <c>durationMs: 0</c> for every clip and uploads were stamped with a
/// flat 2500 ms, so the library showed "0.00s" (or a made-up 2.50s) for every sound.
/// Parsing the container here keeps seeding self-contained: the <c>seed-sounds</c> CLI verb runs
/// before the web host exists, so it cannot reach the render slice's ffprobe. Meme clips are a
/// few hundred KB, so walking every MP3 frame header is cheap and exact for VBR files too.
/// </remarks>
public static class AudioDuration
{
    // Bitrates in kbps, indexed [row][bitrateIndex]. Rows: MPEG-1 L1, L2, L3; MPEG-2/2.5 L1; MPEG-2/2.5 L2+L3.
    private static readonly int[][] Bitrates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ];

    private static readonly int[] SampleRatesMpeg1 = [44100, 48000, 32000];

    /// <summary>Duration in whole milliseconds, or 0 when the format is not recognised.</summary>
    public static int EstimateMs(ReadOnlySpan<byte> data, string? fileNameOrExtension = null)
    {
        var ext = Path.GetExtension(fileNameOrExtension ?? string.Empty).ToLowerInvariant();
        if (ext == ".wav" || IsRiffWave(data))
            return WavMs(data);

        return Mp3Ms(data);
    }

    private static bool IsRiffWave(ReadOnlySpan<byte> d) =>
        d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WAVE"u8);

    private static int WavMs(ReadOnlySpan<byte> d)
    {
        if (!IsRiffWave(d))
            return 0;

        var byteRate = 0;
        var pos = 12;
        while (pos + 8 <= d.Length)
        {
            var id = d.Slice(pos, 4);
            var size = (int)Math.Min(BitConverter.ToUInt32(d.Slice(pos + 4, 4)), int.MaxValue);
            var body = pos + 8;

            if (id.SequenceEqual("fmt "u8) && body + 12 <= d.Length)
                byteRate = BitConverter.ToInt32(d.Slice(body + 8, 4));
            else if (id.SequenceEqual("data"u8) && byteRate > 0)
                return (int)Math.Round(Math.Min(size, d.Length - body) * 1000.0 / byteRate);

            // Chunks are word-aligned.
            pos = body + size + (size & 1);
        }

        return 0;
    }

    private static int Mp3Ms(ReadOnlySpan<byte> d)
    {
        var pos = SkipId3v2(d);
        double seconds = 0;
        var frames = 0;

        while (pos + 4 <= d.Length)
        {
            if (!TryReadFrame(d.Slice(pos, 4), out var frameLength, out var samples, out var sampleRate))
            {
                pos++; // resynchronise on the next byte
                continue;
            }

            seconds += (double)samples / sampleRate;
            frames++;
            pos += frameLength;
        }

        // A lone "frame" is almost certainly a false sync inside non-MP3 data.
        return frames < 2 ? 0 : (int)Math.Round(seconds * 1000);
    }

    private static int SkipId3v2(ReadOnlySpan<byte> d)
    {
        if (d.Length < 10 || !d[..3].SequenceEqual("ID3"u8))
            return 0;

        // Synchsafe size: 7 bits per byte. A footer (flag 0x10) adds another 10 bytes.
        var size = (d[6] << 21) | (d[7] << 14) | (d[8] << 7) | d[9];
        var footer = (d[5] & 0x10) != 0 ? 10 : 0;
        return Math.Min(d.Length, 10 + size + footer);
    }

    private static bool TryReadFrame(ReadOnlySpan<byte> h, out int frameLength, out int samples, out int sampleRate)
    {
        frameLength = samples = sampleRate = 0;
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0)
            return false;

        var version = (h[1] >> 3) & 0x03;       // 0 = MPEG-2.5, 2 = MPEG-2, 3 = MPEG-1
        var layer = (h[1] >> 1) & 0x03;         // 1 = III, 2 = II, 3 = I
        var bitrateIndex = (h[2] >> 4) & 0x0F;
        var sampleRateIndex = (h[2] >> 2) & 0x03;
        var padding = (h[2] >> 1) & 0x01;

        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            return false;

        var mpeg1 = version == 3;
        var row = mpeg1 ? 3 - layer : layer == 3 ? 3 : 4;
        var bitrate = Bitrates[row][bitrateIndex] * 1000;
        sampleRate = SampleRatesMpeg1[sampleRateIndex] >> (mpeg1 ? 0 : version == 2 ? 1 : 2);

        if (layer == 3)
        {
            samples = 384;
            frameLength = (12 * bitrate / sampleRate + padding) * 4;
        }
        else
        {
            var layer3Mpeg2 = layer == 1 && !mpeg1;
            samples = layer3Mpeg2 ? 576 : 1152;
            frameLength = (layer3Mpeg2 ? 72 : 144) * bitrate / sampleRate + padding;
        }

        return frameLength > 4;
    }
}
