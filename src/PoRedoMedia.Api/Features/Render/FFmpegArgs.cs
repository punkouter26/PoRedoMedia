using System.Globalization;
using System.Text;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Render;

/// <summary>
/// Every ffmpeg command line the app runs, as pure functions of their inputs — the highest-risk
/// string building in the render path, so it is kept apart from the process and queue plumbing
/// and unit-tested directly.
/// </summary>
internal static class FFmpegArgs
{
    /// <summary>PCM rate decoded for the envelope. Loudness needs no more; it keeps the decode cheap.</summary>
    internal const int EnvelopeSampleRate = 8000;

    /// <summary>
    /// One decode, two outputs: the speech MP3 (mono 16 kHz at 48 kbps — what speech recognition
    /// uses, ~360 KB a minute, far under the 25 MB transcription upload limit) and raw 8 kHz PCM
    /// for the loudness envelope. The whole source is decoded, not the trim window: the analysis
    /// starts right after upload, before the user has picked a trim, and is shifted later.
    /// </summary>
    internal static string BuildSourceAudioArgs(string sourcePath, string? speechDestinationPath, string pcmPath)
    {
        var sb = new StringBuilder($"-i \"{sourcePath}\"");
        if (!string.IsNullOrWhiteSpace(speechDestinationPath))
            sb.Append(CultureInfo.InvariantCulture, $" -map 0:a:0 -vn -ac 1 -ar 16000 -c:a libmp3lame -b:a 48k -y \"{speechDestinationPath}\"");
        sb.Append(CultureInfo.InvariantCulture, $" -map 0:a:0 -vn -ac 1 -ar {EnvelopeSampleRate} -f s16le -y \"{pcmPath}\"");
        return sb.ToString();
    }

    /// <summary>
    /// The sticker's PNG on disk, or null when the id is not one of the stickers that ship. The id
    /// becomes part of a file path, so only a whitelisted one is ever combined into it.
    /// </summary>
    internal static string? ResolveOverlayAssetPath(string? overlayAssetId)
    {
        if (!DirectorScripts.Stickers.Contains(overlayAssetId))
            return null;

        var fileName = overlayAssetId + ".png";
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "overlays", fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The whole render command: inputs, the -filter_complex, and the encode settings.
    /// Audio: keeps the original video audio and layers each meme sound on top (adelay + amix),
    /// with a limiter to prevent clipping.
    /// Video: chains the base look and captions, then applies each cue's windowed visual effect and
    /// sticker overlay as a separate pass so they only affect their own time range.
    /// </summary>
    internal static string BuildFFmpegArgs(
        string sourcePath,
        IReadOnlyList<RenderVisualEntry> entries,
        string outputPath,
        bool aggressiveVisuals,
        double sourceDurationSeconds,
        bool sourceHasAudio,
        string? aspectRatio = null,
        IReadOnlyList<TranscriptSegmentDto>? subtitles = null,
        double trimStartSeconds = 0)
    {
        var sb = new StringBuilder();

        // Input 0: source video. A trim seeks the input, so every time in the filter graph already
        // counts from the trimmed start; the -t further down ends it.
        if (trimStartSeconds > 0)
            sb.Append(CultureInfo.InvariantCulture, $"-ss {trimStartSeconds:0.###} ");
        sb.Append(CultureInfo.InvariantCulture, $"-i \"{sourcePath}\"");

        // Inputs 1..N: sound files
        foreach (var entry in entries)
            sb.Append(CultureInfo.InvariantCulture, $" -i \"{entry.SoundPath}\"");

        // Overlay inputs (distinct file paths to avoid duplicate inputs)
        var overlayEntries = entries.Where(e => !string.IsNullOrEmpty(e.OverlayPath)).ToList();
        var distinctOverlays = overlayEntries.Select(e => e.OverlayPath!).Distinct().ToList();
        var overlayInputMap = new Dictionary<string, int>();
        for (var i = 0; i < distinctOverlays.Count; i++)
        {
            var inputIndex = entries.Count + 1 + i;
            overlayInputMap[distinctOverlays[i]] = inputIndex;
            sb.Append(CultureInfo.InvariantCulture, $" -i \"{distinctOverlays[i]}\"");
        }

        var videoChain = BuildVideoFilterChain(entries, aggressiveVisuals, aspectRatio, subtitles);
        var hasBaseVideoFilters = !string.IsNullOrWhiteSpace(videoChain);

        // Cue-scoped visual effects. Each is applied on a full-size branch that is overlaid back
        // only inside its cue's window — see the note on BuildVideoFilterChain.
        var effectCues = entries.Where(e => ResolveWindowedEffectFilter(e.VisualEffect) is not null).ToList();
        var hasAdvancedVideo = effectCues.Count > 0 || overlayEntries.Count > 0;
        var hasVideoFilters = hasBaseVideoFilters || hasAdvancedVideo;

        var buildAudioMix = entries.Count > 0;

        if (hasVideoFilters || buildAudioMix)
        {
            var fc = new StringBuilder();

            if (hasVideoFilters)
            {
                if (hasAdvancedVideo)
                {
                    var currentLabel = "v0";
                    if (hasBaseVideoFilters)
                        fc.Append(CultureInfo.InvariantCulture, $"[0:v]{videoChain}[{currentLabel}]");
                    else
                        fc.Append(CultureInfo.InvariantCulture, $"[0:v]null[{currentLabel}]");

                    var step = 0;
                    foreach (var cue in effectCues)
                    {
                        var nextLabel = $"v{step + 1}";
                        var effectFilter = ResolveWindowedEffectFilter(cue.VisualEffect)!;
                        var startSec = (cue.TimestampMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        var endSec = (cue.TimestampMs / 1000.0 + EffectWindowSeconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

                        // Both branches stay full-size, so the effect frame lays straight over the
                        // base at 0,0 and only while the window is open.
                        fc.Append(CultureInfo.InvariantCulture, $";[{currentLabel}]split[vb{step}][vz{step}];[vz{step}]{effectFilter}[vzs{step}];[vb{step}][vzs{step}]overlay=enable='between(t\\,{startSec}\\,{endSec})'[{nextLabel}]");
                        currentLabel = nextLabel;
                        step++;
                    }

                    foreach (var ovl in overlayEntries)
                    {
                        var nextLabel = $"v{step + 1}";
                        var inputIdx = overlayInputMap[ovl.OverlayPath!];
                        var ox = (ovl.OverlayX ?? 0.5).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        var oy = (ovl.OverlayY ?? 0.3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        var startSec = (ovl.TimestampMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        var endSec = (ovl.TimestampMs / 1000.0 + 2.5).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

                        if (ovl.OverlayScale.HasValue && Math.Abs(ovl.OverlayScale.Value - 1.0) > 0.05)
                        {
                            var scaleStr = ovl.OverlayScale.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                            fc.Append(CultureInfo.InvariantCulture, $";[{inputIdx}:v]scale=iw*{scaleStr}:-1[ovs{step}];[{currentLabel}][ovs{step}]overlay=x='min(max(0,W*{ox}-w/2),W-w)':y='min(max(0,H*{oy}-h/2),H-h)':enable='between(t\\,{startSec}\\,{endSec})'[{nextLabel}]");
                        }
                        else
                        {
                            fc.Append(CultureInfo.InvariantCulture, $";[{currentLabel}][{inputIdx}:v]overlay=x='min(max(0,W*{ox}-w/2),W-w)':y='min(max(0,H*{oy}-h/2),H-h)':enable='between(t\\,{startSec}\\,{endSec})'[{nextLabel}]");
                        }
                        currentLabel = nextLabel;
                        step++;
                    }

                    fc.Append(CultureInfo.InvariantCulture, $";[{currentLabel}]null[vout]");
                }
                else
                {
                    fc.Append(CultureInfo.InvariantCulture, $"[0:v]{videoChain}[vout]");
                }
            }

            if (buildAudioMix)
            {
                // A roast is speech: with one, everything else is the bed under it. The meme sounds
                // drop to a third, and the whole bed — source and sounds — ducks while a joke is said.
                // Ducking only the source left the sounds at full level on top of the punchline.
                var voices = Enumerable.Range(0, entries.Count).Where(i => entries[i].Voice).ToList();

                // Original audio sits slightly under the meme sounds so the effects cut through.
                if (sourceHasAudio)
                {
                    if (fc.Length > 0)
                        fc.Append(';');
                    fc.Append("[0:a]volume=0.85[aorig]");
                }

                for (var i = 0; i < entries.Count; i++)
                {
                    var delayMs = entries[i].TimestampMs;
                    var gain = entries[i].Voice ? ",volume=2" : voices.Count > 0 ? ",volume=0.3" : string.Empty;
                    if (fc.Length > 0)
                        fc.Append(';');
                    fc.Append(CultureInfo.InvariantCulture, $"[{i + 1}:a]adelay={delayMs}|{delayMs}{gain}[a{i}]");
                }

                var labels = new List<string>();
                if (sourceHasAudio)
                    labels.Add("[aorig]");
                labels.AddRange(Enumerable.Range(0, entries.Count).Where(i => !entries[i].Voice).Select(i => $"[a{i}]"));

                if (voices.Count > 0 && labels.Count > 0)
                {
                    // The voices become one bus: one copy is heard, the other keys the compressor
                    // on the bed. apad keeps the key running to the end — sidechaincompress stops
                    // at its shorter input, which cut the bed off after the last joke.
                    fc.Append(CultureInfo.InvariantCulture, $";{string.Concat(voices.Select(i => $"[a{i}]"))}amix=inputs={voices.Count}:normalize=0:duration=longest,asplit[vox][voxdry]");
                    fc.Append(CultureInfo.InvariantCulture, $";[voxdry]apad[voxkey];{string.Concat(labels)}amix=inputs={labels.Count}:normalize=0:duration=longest[bed]");
                    fc.Append(";[bed][voxkey]sidechaincompress=threshold=0.015:ratio=12:attack=15:release=400[ducked]");
                    labels = ["[ducked]", "[vox]"];
                }
                else
                {
                    labels.AddRange(voices.Select(i => $"[a{i}]"));
                }

                var mixInputs = string.Concat(labels);
                // normalize=0 keeps each source at full level; alimiter tames the clipping that
                // summing the original track with overlapping sounds would otherwise cause.
                fc.Append(CultureInfo.InvariantCulture, $";{mixInputs}amix=inputs={labels.Count}:normalize=0:duration=longest,alimiter=limit=0.95[aout]");
            }

            sb.Append(CultureInfo.InvariantCulture, $" -filter_complex \"{fc}\"");
        }

        // Map outputs
        sb.Append(hasVideoFilters ? " -map \"[vout]\"" : " -map 0:v");
        if (buildAudioMix)
            sb.Append(" -map \"[aout]\"");
        else if (sourceHasAudio)
            sb.Append(" -map 0:a");   // no meme sounds — keep the original audio untouched
        else
            sb.Append(" -an");

        // Encoding settings: H.264 video + AAC audio. 'veryfast' trades a little size for a large
        // speedup — important on the constrained B1 host where slower presets stall the render.
        sb.Append(" -c:v libx264 -preset veryfast -crf 23");
        if (buildAudioMix || sourceHasAudio)
            // -ac 2 forces stereo output. Without it amix produces the same channel count as the
            // first input (often 5.1 from phone videos) and Chromium silently fails to decode the
            // audio track, so the resulting MP4 has zero sound when played in <video>.
            sb.Append(" -c:a aac -b:a 192k -ac 2");
        // Cap the output to the source video's duration.
        if (sourceDurationSeconds > 0)
            sb.Append(CultureInfo.InvariantCulture, $" -t {sourceDurationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        sb.Append(" -movflags +faststart");
        sb.Append(CultureInfo.InvariantCulture, $" -y \"{outputPath}\"");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the base video filter chain. Always downscales to ≤720p so heavy 1080p/4K phone clips
    /// encode in reasonable time on constrained hosts. Aggressive visuals (a run-wide opt-in)
    /// enable deep-fry EQ + unsharp. Aspect ratio 9:16 adds vertical framing. Captions are overlaid
    /// using drawtext.
    /// </summary>
    // Cue-level visual effects deliberately do NOT belong here. This chain is comma-joined with no
    // per-filter time bounds, so an effect attached to one cue would apply to the whole video, and
    // scale has no timeline support so it could not be time-gated even if it did. Cue effects go
    // through ResolveWindowedEffectFilter + the split/overlay pass in BuildFFmpegArgs instead.
    internal static string BuildVideoFilterChain(
        IReadOnlyList<RenderVisualEntry> sounds,
        bool aggressiveVisuals,
        string? aspectRatio = null,
        IReadOnlyList<TranscriptSegmentDto>? subtitles = null)
    {
        var filters = new List<string>();

        if (string.Equals(aspectRatio, "9:16", StringComparison.OrdinalIgnoreCase))
        {
            // Vertical 9:16 framing: scale down to fit inside 720x1280 then pad with black bars
            filters.Add("scale=720:1280:force_original_aspect_ratio=decrease,pad=720:1280:(ow-iw)/2:(oh-ih)/2:black");
        }
        else if (string.Equals(aspectRatio, "1:1", StringComparison.OrdinalIgnoreCase))
        {
            // Square 1:1 framing: scale down to fit inside 720x720 then pad
            filters.Add("scale=720:720:force_original_aspect_ratio=decrease,pad=720:720:(ow-iw)/2:(oh-ih)/2:black");
        }
        else
        {
            // Default: Cap height at 720 (never upscale) before any other filter.
            // -2 keeps width even (libx264 needs it) and preserves the aspect ratio.
            filters.Add("scale=-2:min(720\\,ih)");
        }

        if (aggressiveVisuals)
        {
            // Deep-fry: saturate + sharpen
            filters.Add("eq=saturation=3:contrast=1.5:brightness=0.05");
            filters.Add("unsharp=5:5:1.5:5:5:0.0");
        }

        // Add text captions / meme punchline overlays
        var fontArg = ResolveFontArg();
        foreach (var s in sounds)
        {
            if (string.IsNullOrWhiteSpace(s.CaptionText)) continue;
            var sanitized = SanitizeForDrawtext(s.CaptionText.Trim().ToUpperInvariant());
            var startSec = (s.TimestampMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var endSec = (s.TimestampMs / 1000.0 + 2.5).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var yPos = s.CaptionPosition?.ToLowerInvariant() switch
            {
                "top" => "40",
                "center" => "(h-text_h)/2",
                _ => "h-text_h-50"
            };
            filters.Add($"drawtext=text='{sanitized}':expansion=none{fontArg}:fontsize=36:fontcolor=white:borderw=3:bordercolor=black:x=(w-text_w)/2:y={yPos}:enable='between(t\\,{startSec}\\,{endSec})'");
        }

        // Speech subtitles: smaller, boxed, pinned to the very bottom so they sit under the
        // punchline captions (which use h-text_h-50) instead of on top of them.
        if (subtitles is { Count: > 0 })
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var card in SubtitleChunker.Chunk(subtitles))
            {
                var sanitized = SanitizeForDrawtext(card.Text);
                var startSec = card.StartSeconds.ToString("0.###", inv);
                var endSec = card.EndSeconds.ToString("0.###", inv);
                filters.Add($"drawtext=text='{sanitized}':expansion=none{fontArg}:fontsize=26:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=6:x=(w-text_w)/2:y=h-text_h-14:enable='between(t\\,{startSec}\\,{endSec})'");
            }
        }

        return string.Join(',', filters);
    }

    /// <summary>
    /// How long a cue's visual effect covers, starting at the cue timestamp. Matches the caption
    /// window so the punch and its text appear together.
    /// </summary>
    internal const double EffectWindowSeconds = 2.5;

    /// <summary>
    /// Maps a cue's visual effect to the filter applied on its windowed branch, or null when the
    /// effect is not a windowed video effect — None, and Overlay, which the sticker path handles.
    /// Each returned filter must keep the frame size unchanged, because the windowed pass overlays
    /// it back onto the base frame at 0,0.
    /// </summary>
    internal static string? ResolveWindowedEffectFilter(string? visualEffect)
        => visualEffect?.ToLowerInvariant() switch
        {
            "deepfry" => "eq=saturation=3:contrast=1.5:brightness=0.05,unsharp=5:5:1.5:5:5:0.0",
            "motionblur" => "tblend=all_mode=average",
            _ => null
        };

    internal static string SanitizeForDrawtext(string text)
    {
        // drawtext wraps the text in single quotes, so a literal apostrophe inside the caption
        // closes the string early (regression: "THAT DIDN'T HIT" became "THAT DIDN" + "T HIT'…" and
        // FFmpeg errored with EINVAL). Within a single-quoted arg FFmpeg does not honour the
        // C-style backslash escape sequence, so we drop apostrophes entirely. Colon is escaped
        // because it's the option delimiter, percent to avoid format-string expansion, and
        // backslash doubled because the drawtext option parser still treats it specially.
        // Double quotes are dropped too: the whole filter graph travels inside one double-quoted
        // process argument, so a quote in a caption ended that argument and the rest of the
        // caption was read as ffmpeg options (an extra output file, for one).
        // ponytail: every command line is still one string. ProcessStartInfo.ArgumentList would
        // remove this quoting layer altogether, at the cost of rewriting each builder here.
        return text
            .Replace("\\", "\\\\")
            .Replace("'", string.Empty)
            .Replace("\"", string.Empty)
            .Replace(":", @"\:")
            .Replace("%", @"\%")
            .Replace("\n", " ")
            .Replace("\r", "");
    }

    private static string ResolveFontArg()
    {
        if (OperatingSystem.IsWindows() && File.Exists(@"C:\Windows\Fonts\impact.ttf"))
            return ":fontfile='C\\:/Windows/Fonts/impact.ttf'";
        if (OperatingSystem.IsWindows() && File.Exists(@"C:\Windows\Fonts\arial.ttf"))
            return ":fontfile='C\\:/Windows/Fonts/arial.ttf'";
        if (File.Exists("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"))
            return ":fontfile='/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'";
        return string.Empty;
    }
}
