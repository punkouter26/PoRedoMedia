using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using System.Diagnostics;

namespace PoRedoMedia.Api.Features.RapRoast;

/// <summary>
/// Produces the scene description the roast is written from, combining machine-extracted facts with
/// a vision model's interpretation.
/// </summary>
/// <remarks>
/// Azure Computer Vision alone is not enough. Its Caption feature is region-limited, and when it is
/// unavailable <c>AzureVisionService</c> synthesises a description by joining the top eight tags —
/// "A photo showing person, clothing, food, man, indoor, fast food, wall, meal". That is a keyword
/// list, and a lyric writer given keywords can only produce generic bars.
/// <para>
/// The pipeline is therefore: <see cref="ISceneDetailProvider"/> supplies ground truth (OCR text,
/// region captions, objects) → a vision-language model interprets the image <em>with those facts in
/// hand</em> and returns a structured <see cref="SceneSnapshot"/> → optionally a second model is
/// consulted and the two are merged. Each stage degrades independently; losing all of them returns
/// the original tag-derived text rather than failing.
/// </para>
/// </remarks>
public sealed class SceneDescriber(
    IChatCompletionService chat,
    ISceneDetailProvider sceneDetails,
    IConfiguration configuration,
    ILogger<SceneDescriber> logger)
{
    private const string SystemPrompt =
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
    private const string SceneSchema = """
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
    public Task<SceneDescription> DescribeAsync(
        byte[] image,
        string fallbackDescription,
        IReadOnlyList<string> tags,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        // Ground truth first — OCR in particular, because a language model will happily invent the
        // text on a sign it cannot actually read.
        return DescribeCoreAsync(image, fallbackDescription, tags, details: null, ct);
    }

    /// <summary>
    /// Describes the image using scene detail the CALLER already has.
    /// </summary>
    /// <remarks>
    /// The orchestrator can now obtain description, tags and detail from one combined Computer
    /// Vision request (see <see cref="ICombinedVisionAnalyzer"/>). When it does, re-fetching the
    /// detail here would reinstate exactly the duplicate call that change removed.
    /// </remarks>
    public Task<SceneDescription> DescribeAsync(
        byte[] image,
        string fallbackDescription,
        IReadOnlyList<string> tags,
        SceneDetails details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return DescribeCoreAsync(image, fallbackDescription, tags, details, ct);
    }

    private async Task<SceneDescription> DescribeCoreAsync(
        byte[] image,
        string fallbackDescription,
        IReadOnlyList<string> tags,
        SceneDetails? details,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        details ??= await SafeDetailsAsync(image, ct);

        if (!chat.IsConfigured)
        {
            sw.Stop();
            const string reason =
                "No vision model is configured, so the bars are working from image labels alone. "
                + "Set OpenAI:Endpoint and OpenAI:Key for a detailed read of the scene.";

            // Even with no vision model, OCR and objects beat a bare tag list.
            if (details.HasAny)
            {
                logger.LogInformation("No vision model configured; describing from extracted detail alone.");
                return new SceneDescription(
                    $"{fallbackDescription}. {details.ToPromptBlock()}",
                    Detailed: false, SceneSnapshot.Empty, details, sw.ElapsedMilliseconds, reason);
            }

            logger.LogInformation("No vision model or scene detail available; using the tag-derived description.");
            return new SceneDescription(
                fallbackDescription, Detailed: false, SceneSnapshot.Empty, details, sw.ElapsedMilliseconds, reason);
        }

        var (snapshot, failure) = await ExtractAsync(image, tags, details, ct);

        if (SecondOpinionEnabled)
        {
            snapshot = await MergeSecondOpinionAsync(image, snapshot, ct);
        }

        sw.Stop();

        if (!snapshot.HasSubstance)
        {
            logger.LogWarning("Vision model returned nothing usable; falling back to extracted detail.");
            var text = details.HasAny ? $"{fallbackDescription}. {details.ToPromptBlock()}" : fallbackDescription;
            return new SceneDescription(
                text, Detailed: false, SceneSnapshot.Empty, details, sw.ElapsedMilliseconds,
                failure ?? "The vision model returned nothing usable, so the bars are working from "
                    + "image labels alone. Roasting the photo again usually fixes it.");
        }

        return new SceneDescription(
            snapshot.ToProse(), Detailed: true, snapshot, details, sw.ElapsedMilliseconds, FallbackReason: null);
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
    /// cost of the vision step for a marginal gain once OCR is supplying the facts.
    /// </summary>
    private bool SecondOpinionEnabled =>
        configuration.GetValue<bool>(ConfigKeys.VisionSecondOpinion);

    private async Task<SceneDetails> SafeDetailsAsync(byte[] image, CancellationToken ct)
    {
        try
        {
            return await sceneDetails.GetDetailsAsync(image, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scene detail extraction failed; continuing without it.");
            return SceneDetails.Empty;
        }
    }

    /// <summary>
    /// Runs the vision read. Returns the snapshot plus, when the call failed, a user-facing reason —
    /// the caller cannot tell "model said nothing" from "model was throttled" without it.
    /// </summary>
    private async Task<(SceneSnapshot Snapshot, string? Failure)> ExtractAsync(
        byte[] image, IReadOnlyList<string> tags, SceneDetails details, CancellationToken ct)
    {
        try
        {
            var user = BuildUserPrompt(tags, details);
            var result = await chat.CompleteAsync(SystemPrompt, user, image, SceneSchema, ct);

            var snapshot = SceneSnapshot.Parse(result.Content);

            // OCR is authoritative for text. If the model missed lines the reader found, add them —
            // and prefer the read values over anything the model claims to have seen.
            if (details.TextLines.Count > 0)
            {
                snapshot = snapshot with
                {
                    TextInImage = [.. details.TextLines
                        .Concat(snapshot.TextInImage)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(12)],
                };
            }

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

    private static string BuildUserPrompt(IReadOnlyList<string> tags, SceneDetails details)
    {
        var parts = new List<string>();

        if (details.HasAny)
        {
            parts.Add(
                "Machine-extracted facts about this image. Treat these as TRUE and incorporate them; "
                + "the OCR text in particular is exact and must not be paraphrased or invented over:\n"
                + details.ToPromptBlock());
        }

        if (tags.Count > 0)
            parts.Add($"Detected labels (corroboration only, do not simply repeat them): {string.Join(", ", tags)}.");

        parts.Add("Analyse the image and return the JSON object.");
        return string.Join("\n\n", parts);
    }
}

/// <summary>The description handed to the lyric writer, plus how it was produced.</summary>
/// <param name="Text">Prose description.</param>
/// <param name="Detailed">True when a vision model produced a structured snapshot.</param>
/// <param name="Snapshot">Structured slots; <see cref="SceneSnapshot.Empty"/> when unavailable.</param>
/// <param name="Details">Machine-extracted ground truth.</param>
/// <param name="ElapsedMs">Wall-clock time taken.</param>
/// <param name="FallbackReason">
/// User-facing explanation of why <paramref name="Detailed"/> is false, or null when it is true.
/// </param>
public sealed record SceneDescription(
    string Text,
    bool Detailed,
    SceneSnapshot Snapshot,
    SceneDetails Details,
    long ElapsedMs,
    string? FallbackReason);
