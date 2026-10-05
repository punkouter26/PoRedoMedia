namespace PoRedoMedia.Api.Features.Memeify;

/// <summary>A sudden rise in loudness: a hit, a slam, a shout, a beat.</summary>
public readonly record struct AudioOnset(long TimestampMs, double Strength);

/// <summary>
/// Moves cues onto nearby audio hits. Frames are sampled every 3 s, so a vision cue can be up to
/// 1.5 s from the action it describes; the soundtrack knows exactly when the slam happened.
/// No model involved — a loudness envelope and some arithmetic.
/// </summary>
public static class CueSnapping
{
    /// <summary>How far a cue may move to reach a hit.</summary>
    public const long WindowMs = 750;

    /// <summary>Snapped cues keep at least this much air between them.</summary>
    public const long MinSpacingMs = 1_200;

    /// <summary>A rise smaller than this (dB over the preceding 60 ms) is not a hit.</summary>
    private const double MinRiseDb = 6;

    /// <summary>Quieter than this (RMS, 0–1) is background noise, whatever it does.</summary>
    private const float MinLevel = 0.02f;

    /// <summary>Onsets closer than this (either side) collapse into the strongest.</summary>
    private const int PeakRadiusFrames = 5;

    public static IReadOnlyList<AudioOnset> DetectOnsets(AudioEnvelope envelope)
    {
        var rms = envelope.Rms;
        if (rms.Length < 4)
            return [];

        var db = rms.Select(r => 20 * Math.Log10(r + 1e-4)).ToArray();
        var rise = new double[db.Length];
        for (var i = 3; i < db.Length; i++)
        {
            var before = (db[i - 1] + db[i - 2] + db[i - 3]) / 3;
            rise[i] = rms[i] >= MinLevel ? Math.Max(0, db[i] - before) : 0;
        }

        var frameMs = 1000.0 / envelope.FramesPerSecond;
        var onsets = new List<AudioOnset>();
        for (var i = 0; i < rise.Length; i++)
        {
            if (rise[i] < MinRiseDb)
                continue;

            var isPeak = true;
            for (var j = Math.Max(0, i - PeakRadiusFrames); j <= Math.Min(rise.Length - 1, i + PeakRadiusFrames) && isPeak; j++)
                isPeak = j == i || rise[j] < rise[i] || (rise[j] == rise[i] && j > i);

            if (isPeak)
                onsets.Add(new AudioOnset((long)Math.Round(i * frameMs), rise[i]));
        }

        return onsets;
    }

    /// <summary>
    /// Snaps each cue flagged in <paramref name="snappable"/> to the best onset within
    /// <see cref="WindowMs"/> — strongest wins, discounted by distance — as long as it keeps
    /// <see cref="MinSpacingMs"/> from its neighbours. Input must be in time order; so is the output.
    /// </summary>
    public static long[] Snap(IReadOnlyList<long> cueMs, IReadOnlyList<bool> snappable, IReadOnlyList<AudioOnset> onsets, long maxMs)
    {
        var result = cueMs.ToArray();
        if (onsets.Count == 0)
            return result;

        for (var i = 0; i < result.Length; i++)
        {
            if (!snappable[i])
                continue;

            var lower = i > 0 ? result[i - 1] + MinSpacingMs : 0;
            var upper = i < result.Length - 1 ? result[i + 1] - MinSpacingMs : maxMs;
            var best = onsets
                .Where(o => Math.Abs(o.TimestampMs - result[i]) <= WindowMs && o.TimestampMs >= lower && o.TimestampMs <= upper)
                .Select(o => (Onset: o, Score: o.Strength * (1 - 0.5 * Math.Abs(o.TimestampMs - result[i]) / WindowMs)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();

            if (best.Onset != default)
                result[i] = best.Onset.TimestampMs;
        }

        return result;
    }
}
