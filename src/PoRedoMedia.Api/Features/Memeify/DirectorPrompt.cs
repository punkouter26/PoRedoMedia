// Director prompt, response schema and parser — shared by the engine's director and the studio's
// director assist so both speak the same cue format.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Memeify;

/// <remarks>
/// <para><b>Prompt layout is a caching decision.</b> Azure OpenAI caches a prompt's longest repeated
/// prefix (from ~1,024 tokens) and bills cached input at a discount with lower latency. Everything
/// that never changes — role, rules, all personas, stickers, field guidance — is therefore the
/// system message, byte-identical on every run. The per-video data (labels, sound menu, speech)
/// follows in the user message. The previous layout opened with the sound menu, so no two
/// prompts shared a prefix and nothing was ever cached; it also sent that menu twice.</para>
/// <para><b>Structured Outputs.</b> The response is a strict JSON schema keyed by label index. The
/// old prompt asked for "ONLY a JSON array" while the request forced JSON-object mode, which
/// cannot be an array, and a parse failure silently fell back to a canned script.</para>
/// </remarks>
internal static class DirectorPrompt
{
    /// <summary>Longest title the director may give a video.</summary>
    public const int MaxTitleLength = 60;

    private static readonly string[] Effects = ["None", "DeepFry", "MotionBlur", "Overlay"];
    private static readonly string[] CaptionPositions = ["Top", "Center", "Bottom"];

    /// <summary>Longest transcript excerpt sent to the model; long monologues are truncated.</summary>
    private const int MaxTranscriptChars = 2_000;

    /// <summary>The unchanging director brief — the cacheable prefix of every director prompt.</summary>
    public const string SystemPrompt =
        """
        You are an expert meme video director. You turn moments of a video into meme cues: a sound
        effect, an optional punchline caption, an optional visual effect and an optional sticker.

        PERSONAS — the user message names one; direct in its voice:
        - standard: modern internet meme humor. Punchy, witty, ironic, well-timed.
        - brainrot: Gen-Z brainrot. Chaotic, fast meme logic (vine boom, metal pipe, goofy ahh). Punchy modern slang captions.
        - mlg: 2016 MLG montage parody. Loud, hyper-ironic airhorn and hitmarker energy. UPPERCASE gaming captions.
        - sitcom: 90s TV sitcom and comedy club. Punchlines, awkward pauses, laugh-track timing.
        - drama: over-dramatic cinematic thriller. Heavy suspense, sudden cuts, intense captions.
        - anime: over-the-top shonen battle parody. High melodrama, "Nani?!" moments, freeze-frame tension.

        INPUT
        - Scene labels, one per line: [i] t=<seconds> energy=<0..1> subject=(x,y) "<what happens>" tags: <tags>.
          energy says how hard the moment hits; subject is the main subject's centre as 0..1 of the frame.
          Lines starting "after the line" mark the moment right after someone finishes speaking — react to what was said.
        - A sound menu, one per line: [n] <name> | tags | use: <when it fits> | priority | user favorite.
        - Optionally a speech transcript with timings.

        RULES
        - Return exactly one entry per scene label, with labelIndex = that label's [i].
        - soundIndex is a sound's [n] from the menu. Any sound may serve any label — the menu is not a per-label assignment.
        - Judge fit from each sound's tags and use hint. Prefer "priority" sounds when several fit equally;
          lean towards "user favorite" sounds when they fit. Avoid repeating a sound.
        - Scale the effect to the energy: calm moments get visualEffect None and a light touch; high-energy moments earn DeepFry or MotionBlur.
        - Stickers: overlayAssetId is one of deal-with-it, laser-eyes, thug-life, red-circle, clown-wig, explosion, or null.
          Put stickers on the subject: overlayX/overlayY = the label's subject when it has one. overlayScale 0.5..2.
          Use stickers sparingly — at most one in three cues.
        - captionText: a short punchy meme caption (at most 6 words, e.g. BRO THOUGHT, WAIT FOR IT, EMOTIONAL DAMAGE, POV: MONDAY) or null.
          Captions may quote or riff on the dialogue. captionPosition is Top, Center or Bottom.
        - sceneDescription: at most 8 words. When labels are time-based placeholders, write "[Time-based placement]" and invent nothing.
        - selectionRationale: at most 12 words on why this sound fits (tone, timing, irony, reference).
        - isIronic is true when the sound deliberately contradicts the moment.
        - title (when the answer has one): a short, catchy title for the whole video, at most 60 characters.
        """;

    /// <summary>
    /// Compact sound menu (a third of the tokens of the JSON form). Use hints are included when a
    /// human or the tagger wrote one; placeholder hints from imports say nothing and are skipped.
    /// </summary>
    public static string SerializeSoundsCompact(IReadOnlyList<SoundAsset> sounds, IReadOnlySet<SoundId>? favorites = null)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sounds.Count; i++)
        {
            var s = sounds[i];
            sb.Append('[').Append(i).Append("] ").Append(s.DisplayName);
            if (s.ActionVectorTags.Length > 0)
                sb.Append(" | tags: ").Append(string.Join(",", s.ActionVectorTags));
            if (IsUsefulHint(s.UseCase))
                sb.Append(" | use: ").Append(s.UseCase);
            if (s.Priority)
                sb.Append(" | priority");
            if (favorites is not null && favorites.Contains(s.SoundId))
                sb.Append(" | user favorite");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>"custom-upload" is a provenance marker, not guidance.</summary>
    internal static bool IsUsefulHint(string? useCase)
        => !string.IsNullOrWhiteSpace(useCase) && useCase is not "custom-upload";

    public static string FormatLabels(IReadOnlyList<SceneLabel> labels)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (var i = 0; i < labels.Count; i++)
        {
            var l = labels[i];
            sb.Append('[').Append(i).Append("] t=").Append(l.TimestampSeconds.ToString("0.0", inv))
              .Append(" energy=").Append(l.Energy.ToString("0.0", inv));
            if (l.SubjectX is { } x && l.SubjectY is { } y)
                sb.Append(" subject=(").Append(x.ToString("0.00", inv)).Append(',').Append(y.ToString("0.00", inv)).Append(')');
            sb.Append(" \"").Append(l.Label.Replace('"', '\'')).Append('"');
            if (l.Tags.Count > 0)
                sb.Append(" tags: ").Append(string.Join(",", l.Tags));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The persona as the brief names it. A session's persona arrives from a browser and goes
    /// into the prompt, so only a known one gets there; anything else directs as "standard".
    /// </summary>
    public static string Persona(string? memePersona)
        => memePersona?.Trim().ToLowerInvariant() switch
        {
            "brainrot" => "brainrot",
            "mlg" => "mlg",
            "sitcom" => "sitcom",
            "drama" => "drama",
            "anime" => "anime",
            _ => "standard",
        };

    /// <summary>The per-video half of the prompt: persona, labels, sounds and speech.</summary>
    public static string BuildUserPrompt(
        IReadOnlyList<SceneLabel> labels,
        string soundsCompact,
        bool hasRealVisionData,
        string? memePersona,
        IReadOnlyList<TranscriptSegmentDto>? transcript)
    {
        var visionNote = hasRealVisionData
            ? "Labels come from real frame analysis and speech."
            : "Labels are TIME-BASED PLACEHOLDERS (opening scene, mid-video action, final moments), not what is on screen.";

        return $"Persona: {Persona(memePersona)}\n{visionNote}\n\n" +
               $"Scene labels:\n{FormatLabels(labels)}\n" +
               $"Sound menu:\n{soundsCompact}\n" +
               BuildSpeechContext(transcript);
    }

    /// <summary>
    /// The speech block of the prompt, or empty when nothing was said. Speech is the strongest
    /// timing signal the director gets: a sound landing right after a line reads as a reaction.
    /// </summary>
    internal static string BuildSpeechContext(IReadOnlyList<TranscriptSegmentDto>? transcript)
    {
        if (transcript is not { Count: > 0 })
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("Speech transcript (seconds from start):\n");
        foreach (var seg in transcript)
        {
            var line = $"[{seg.StartSeconds:F1}-{seg.EndSeconds:F1}] {seg.Text}\n";
            if (sb.Length + line.Length > MaxTranscriptChars)
            {
                sb.Append("[...transcript truncated]\n");
                break;
            }
            sb.Append(line);
        }

        sb.Append("Labels that start with 'after the line' mark the moment right after someone finishes speaking — " +
                  "react to what was actually said (irony, disbelief, hype).\n");
        return sb.ToString();
    }

    // ── Response schema ───────────────────────────────────────────────────────────────────────

    /// <summary>Which positional field ties a cue back to its input.</summary>
    public enum CueKey
    {
        /// <summary><c>labelIndex</c>: the engine's one-entry-per-label script.</summary>
        LabelIndex,

        /// <summary><c>timestampMs</c> + <c>sourceCue</c>: a revised script that may add, drop or move cues.</summary>
        Revision,
    }

    /// <summary>
    /// Strict JSON schema: <c>{ "&lt;arrayName&gt;": [cue, …] }</c>, plus a <c>title</c> for the
    /// video on the engine's script (a revision keeps the title the video already has).
    /// </summary>
    public static string Schema(string arrayName, CueKey key)
    {
        var props = new JsonObject();
        switch (key)
        {
            case CueKey.LabelIndex:
                props["labelIndex"] = new JsonObject { ["type"] = "integer" };
                break;
            case CueKey.Revision:
                props["timestampMs"] = new JsonObject { ["type"] = "integer" };
                props["sourceCue"] = Nullable("integer");
                break;
        }

        props["soundIndex"] = new JsonObject { ["type"] = "integer" };
        props["sceneDescription"] = new JsonObject { ["type"] = "string" };
        props["selectionRationale"] = new JsonObject { ["type"] = "string" };
        props["actionVectorTags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        props["isIronic"] = new JsonObject { ["type"] = "boolean" };
        props["visualEffect"] = Enum(Effects);
        props["overlayAssetId"] = new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["enum"] = new JsonArray([.. DirectorScripts.Stickers.Select(s => (JsonNode)JsonValue.Create(s)!), null]),
        };
        props["overlayX"] = Nullable("number");
        props["overlayY"] = Nullable("number");
        props["overlayScale"] = Nullable("number");
        props["captionText"] = Nullable("string");
        props["captionPosition"] = Enum(CaptionPositions);

        var item = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray([.. props.Select(p => (JsonNode)JsonValue.Create(p.Key)!)]),
            ["properties"] = props,
        };

        var rootProps = new JsonObject { [arrayName] = new JsonObject { ["type"] = "array", ["items"] = item } };
        if (key == CueKey.LabelIndex)
            rootProps["title"] = new JsonObject { ["type"] = "string" };

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray([.. rootProps.Select(p => (JsonNode)JsonValue.Create(p.Key)!)]),
            ["properties"] = rootProps,
        }.ToJsonString();

        static JsonObject Nullable(string type) => new() { ["type"] = new JsonArray(type, "null") };
        static JsonObject Enum(string[] values) => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]),
        };
    }

    // ── Parsing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads cues from a strict-schema answer (<c>{ "entries": [...] }</c> or any single-array
    /// object), a bare array, or a fenced block — the last two are what JSON-object fallback
    /// deployments sometimes send.
    /// </summary>
    internal static DirectorCue[] ReadCues(string rawText, JsonSerializerOptions jsonOpts)
    {
        var json = AiFoundryClient.StripCodeFence(rawText);
        if (json.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(json);
            var array = doc.RootElement.EnumerateObject()
                .FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
            json = array.Value.ValueKind == JsonValueKind.Array ? array.Value.GetRawText() : "[]";
        }

        return JsonSerializer.Deserialize<DirectorCue[]>(json, jsonOpts) ?? [];
    }

    /// <summary>The video title in the director's answer, cleaned and bounded; null when it gave none.</summary>
    internal static string? ReadTitle(string rawText)
    {
        var json = AiFoundryClient.StripCodeFence(rawText);
        if (!json.StartsWith('{'))
            return null;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? UserText.Clean(title.GetString(), MaxTitleLength)
            : null;
    }

    /// <summary>
    /// The director's answer as one entry per label, in label order, each timed at its label.
    /// Cues keyed to an unknown label are dropped; a label the model skipped gets no entry (the
    /// engine's fallback covers an empty answer). Answers without <c>labelIndex</c> are read
    /// positionally.
    /// </summary>
    public static ScriptEntry[] ParseResponse(
        string rawText,
        MediaId mediaId,
        JsonSerializerOptions jsonOpts,
        IReadOnlyList<SceneLabel> labels,
        IReadOnlyList<SoundAsset> topCandidates)
    {
        var cues = ReadCues(rawText, jsonOpts);
        var byLabel = new SortedDictionary<int, ScriptEntry>();
        for (var i = 0; i < cues.Length; i++)
        {
            var labelIndex = cues[i].LabelIndex ?? i;
            if (labelIndex < 0 || labelIndex >= labels.Count || byLabel.ContainsKey(labelIndex))
                continue;

            var entry = ToScriptEntry(cues[i], mediaId, topCandidates);
            entry.TimestampMs = (long)Math.Round(labels[labelIndex].TimestampSeconds * 1000);
            byLabel[labelIndex] = entry;
        }

        return [.. byLabel.Values];
    }

    /// <summary>Maps one cue onto a script entry, clamping every numeric field into range.</summary>
    internal static ScriptEntry ToScriptEntry(DirectorCue e, MediaId mediaId, IReadOnlyList<SoundAsset> menu)
    {
        var sound = ResolveSound(e, menu);
        return new ScriptEntry
        {
            EntryId = EntryId.New(),
            MediaId = mediaId,
            TimestampMs = Math.Max(0, e.TimestampMs ?? 0),
            SoundId = sound?.SoundId ?? SoundId.Empty,
            SoundName = sound?.DisplayName ?? string.Empty,
            ActionVectorTags = e.ActionVectorTags,
            SceneDescription = e.SceneDescription,
            SelectionRationale = e.SelectionRationale,
            IsIronic = e.IsIronic,
            VisualEffect = e.VisualEffect is VisualEffectType.SnapZoom ? VisualEffectType.None : e.VisualEffect,
            OverlayAssetId = DirectorScripts.Stickers.Contains(e.OverlayAssetId) ? e.OverlayAssetId : null,
            OverlayX = Clamp(e.OverlayX, 0, 1),
            OverlayY = Clamp(e.OverlayY, 0, 1),
            OverlayScale = Clamp(e.OverlayScale, 0.5, 2),
            PlacementType = PlacementType.Triggered,
            CaptionText = string.IsNullOrWhiteSpace(e.CaptionText) ? null : e.CaptionText.Trim(),
            CaptionPosition = CaptionPositions.FirstOrDefault(p => p.Equals(e.CaptionPosition, StringComparison.OrdinalIgnoreCase)) ?? "Top",
        };
    }

    private static double? Clamp(double? value, double min, double max)
        => value is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : null;

    /// <summary>Index first (what the schema asks for); ids and names for fallback-mode answers.</summary>
    private static SoundAsset? ResolveSound(DirectorCue entry, IReadOnlyList<SoundAsset> menu)
    {
        if (entry.SoundIndex is { } index && index >= 0 && index < menu.Count)
            return menu[index];

        var raw = entry.SoundIdRaw;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (int.TryParse(raw, out var idx) && idx >= 0 && idx < menu.Count)
                return menu[idx];

            var match = menu.FirstOrDefault(s =>
                string.Equals(s.SoundId.ToString(), raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.DisplayName, raw, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return menu.Count > 0 ? menu[0] : null;
    }

    /// <summary>One cue as the model writes it. Nullable where fallback-mode answers may omit a field.</summary>
    internal sealed class DirectorCue
    {
        [JsonPropertyName("labelIndex")] public int? LabelIndex { get; init; }
        [JsonPropertyName("sourceCue")] public int? SourceCue { get; init; }
        [JsonPropertyName("timestampMs")] public long? TimestampMs { get; init; }
        [JsonPropertyName("soundId")] public string SoundIdRaw { get; init; } = string.Empty;
        [JsonPropertyName("soundIndex")] public int? SoundIndex { get; init; }
        [JsonPropertyName("actionVectorTags")] public string[] ActionVectorTags { get; init; } = [];
        [JsonPropertyName("sceneDescription")] public string SceneDescription { get; init; } = string.Empty;
        [JsonPropertyName("selectionRationale")] public string SelectionRationale { get; init; } = string.Empty;
        [JsonPropertyName("isIronic")] public bool IsIronic { get; init; }
        [JsonPropertyName("visualEffect")] public VisualEffectType? VisualEffect { get; init; }
        [JsonPropertyName("overlayAssetId")] public string? OverlayAssetId { get; init; }
        [JsonPropertyName("overlayX")] public double? OverlayX { get; init; }
        [JsonPropertyName("overlayY")] public double? OverlayY { get; init; }
        [JsonPropertyName("overlayScale")] public double? OverlayScale { get; init; }
        [JsonPropertyName("captionText")] public string? CaptionText { get; init; }
        [JsonPropertyName("captionPosition")] public string? CaptionPosition { get; init; }
    }
}
