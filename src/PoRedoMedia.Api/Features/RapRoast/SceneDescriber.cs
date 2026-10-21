using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using System.Diagnostics;

namespace PoRedoMedia.Api.Features.RapRoast;

/// <summary>
/// Produces the scene description the roast is written from: a vision-language model reads the
/// image into a structured <see cref="SceneSnapshot"/>, optionally a second model is consulted and
/// the two are merged. Each stage degrades independently; losing all of them returns the plain
/// description rather than failing.
/// </summary>
/// <remarks>
/// A plain caption or tag list is not enough: a lyric writer given keywords can only produce
/// generic bars, so the snapshot names specifics.
/// </remarks>
public sealed class SceneDescriber(
    IChatCompletionService chat,
    IConfiguration configuration,
    ILogger<SceneDescriber> logger)
{
    internal const string SystemPrompt =
        "You are a scene analyst feeding a comedy writer. Study the image and reply with MINIFIED "
        + "JSON only — no prose, no markdown fences, no commentary.\n"
        + "Schema: {\"outfit\":[\"specific garments with colour, fit and condition\"],"
        + "\"pose\":\"body position and what the hands are doing\","
        + "\"expression\":\"facial expression\","
        + "\"setting\":\"where this is, including what is on the walls or surfaces behind\","
        + "\"props\":[\"objects and food, with their exact state\"],"
        + "\"text_in_image\":[\"text visible in the frame, verbatim\"],"
        + "\"most_incongruous_detail\":\"the single funniest or most out-of-place thing present\"}\n"
        + "Be specific and literal — name things precisely. Report only what is visible; do not "
        + "invent. Do NOT describe or infer race, ethnicity, skin tone, body size or weight, age, "
        + "disability, or attractiveness, and never include them in any field. Do not write jokes.";

    /// <summary>Strict structured-output schema matching <see cref="SceneSnapshot.Parse"/>.</summary>
    internal const string SceneSchema = """
        {"type":"object","additionalProperties":false,
         "required":["outfit","pose","expression","setting","props","text_in_image","most_incongruous_detail"],
         "properties":{
           "outfit":{"type":"array","items":{"type":"string"}},
           "pose":{"type":"string"},"expression":{"type":"string"},"setting":{"type":"string"},
           "props":{"type":"array","items":{"type":"string"}},
           "text_in_image":{"type":"array","items":{"type":"string"}},
           "most_incongruous_detail":{"type":"string"}}}
        """;

    /// <summary>
    /// Describes the image. Falls back through progressively simpler sources and only returns
    /// <paramref name="fallbackDescription"/> when no model is available at all.
    /// </summary>
    public async Task<SceneDescription> DescribeAsync(
        byte[] image,
        string fallbackDescription,
        IReadOnlyList<string> tags,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var sw = Stopwatch.StartNew();

        if (!chat.IsConfigured)
        {
            logger.LogInformation("No vision model configured; using the plain description.");
            return new SceneDescription(
                fallbackDescription, Detailed: false, SceneSnapshot.Empty, sw.ElapsedMilliseconds,
                "No vision model is configured, so the bars are working from image labels alone. "
                + "Set OpenAI:Endpoint and OpenAI:Key for a detailed read of the scene.");
        }

        var (snapshot, failure) = await ExtractAsync(image, tags, ct);

        if (SecondOpinionEnabled)
        {
            snapshot = await MergeSecondOpinionAsync(image, snapshot, ct);
        }

        sw.Stop();

        if (!snapshot.HasSubstance)
        {
            logger.LogWarning("Vision model returned nothing usable; falling back to the plain description.");
            return new SceneDescription(
                fallbackDescription, Detailed: false, SceneSnapshot.Empty, sw.ElapsedMilliseconds,
                failure ?? "The vision model returned nothing usable, so the bars are working from "
                    + "image labels alone. Roasting the photo again usually fixes it.");
        }

        return new SceneDescription(snapshot.ToProse(), Detailed: true, snapshot, sw.ElapsedMilliseconds, FallbackReason: null);
    }

    /// <summary>
    /// Turns a failed vision call into something the user can act on. The distinction matters:
    /// a throttled call succeeds on retry, whereas a filtered or misconfigured one never will.
    /// </summary>
    private static string DescribeFailure(Exception ex)
    {
        return AiFailure.Classify(ex) switch
        {
            AiFailureKind.RateLimited => "The vision model was rate-limited, so the bars are working from image labels "
                + "alone. Wait a moment and roast the photo again.",
            AiFailureKind.ContentFiltered => "The vision model declined to describe this image, so the bars are working from "
                + "image labels alone.",
            AiFailureKind.Misconfigured => "The vision model rejected the request (credentials or deployment name), so the "
                + "bars are working from image labels alone. Check OpenAI:Key and OpenAI:ChatCompletionsDeployment.",
            _ => "The vision model call failed, so the bars are working from image labels alone. "
                + "Roasting the photo again usually fixes it.",
        };
    }

    /// <summary>
    /// Whether to consult a second vision model and merge. Off by default: it roughly doubles the
    /// cost of the vision step for a marginal gain.
    /// </summary>
    private bool SecondOpinionEnabled =>
        configuration.GetValue<bool>(ConfigKeys.VisionSecondOpinion);

    /// <summary>
    /// Runs the vision read. Returns the snapshot plus, when the call failed, a user-facing reason —
    /// the caller cannot tell "model said nothing" from "model was throttled" without it.
    /// </summary>
    private async Task<(SceneSnapshot Snapshot, string? Failure)> ExtractAsync(
        byte[] image, IReadOnlyList<string> tags, CancellationToken ct)
    {
        try
        {
            var user = BuildUserPrompt(tags);
            var result = await chat.CompleteAsync(SystemPrompt, user, image, SceneSchema, ct);

            var snapshot = SceneSnapshot.Parse(result.Content);

            logger.LogInformation(
                "Scene extracted in {Elapsed}ms. Outfit={Outfit}, Props={Props}, Text={Text}, Tokens={Tokens}",
                result.ElapsedMs, snapshot.Outfit.Count, snapshot.Props.Count,
                snapshot.TextInImage.Count, result.TokensUsed);

            return (snapshot, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vision extraction failed.");
            return (SceneSnapshot.Empty, DescribeFailure(ex));
        }
    }

    /// <summary>
    /// Consults the general vision service and folds anything new into the snapshot.
    /// </summary>
    /// <remarks>
    /// Deliberately additive only. The second model's free-text answer cannot be trusted to overwrite
    /// structured slots, so it contributes a prop line the first pass may have missed and nothing else.
    /// </remarks>
    private async Task<SceneSnapshot> MergeSecondOpinionAsync(
        byte[] image, SceneSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var second = (await chat.CompleteAsync(
                "You are a precise physical appearance descriptor. Output a short noun phrase describing the primary subject's visible traits.",
                "Describe the main subject of this photo as a short noun phrase.", image, ct: ct)).Content.Trim();
            if (string.IsNullOrWhiteSpace(second)) return snapshot;

            logger.LogInformation("Second-opinion vision pass returned {Chars} chars.", second.Length);

            return snapshot with
            {
                Props = [.. snapshot.Props.Append($"second look: {second}").Take(12)],
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Second-opinion vision pass failed; keeping the primary snapshot.");
            return snapshot;
        }
    }

    /// <summary>The scene a snapshot read from the picture describes, ready for the lyric writer.</summary>
    public static SceneDescription FromSnapshot(SceneSnapshot snapshot, long elapsedMs) =>
        new(snapshot.ToProse(), Detailed: true, snapshot, elapsedMs, FallbackReason: null);

    internal static string BuildUserPrompt(IReadOnlyList<string> tags) =>
        tags.Count > 0
            ? $"Detected labels (corroboration only, do not simply repeat them): {string.Join(", ", tags)}.\n\n"
                + "Analyse the image and return the JSON object."
            : "Analyse the image and return the JSON object.";
}

/// <summary>The description handed to the lyric writer, plus how it was produced.</summary>
/// <param name="Text">Prose description.</param>
/// <param name="Detailed">True when a vision model produced a structured snapshot.</param>
/// <param name="Snapshot">Structured slots; <see cref="SceneSnapshot.Empty"/> when unavailable.</param>
/// <param name="ElapsedMs">Wall-clock time taken.</param>
/// <param name="FallbackReason">
/// User-facing explanation of why <paramref name="Detailed"/> is false, or null when it is true.
/// </param>
public sealed record SceneDescription(
    string Text,
    bool Detailed,
    SceneSnapshot Snapshot,
    long ElapsedMs,
    string? FallbackReason);
