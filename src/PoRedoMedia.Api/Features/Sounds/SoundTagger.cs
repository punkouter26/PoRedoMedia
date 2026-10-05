using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace PoRedoMedia.Api.Features.Sounds;

/// <summary>Tags and a one-line "when to use it" hint for a sound, written by a small model.</summary>
/// <remarks>
/// Uploads arrived tagged only "custom, user-upload". Matching and the director both read tags
/// and hints, so those sounds were close to invisible. The tagger prefers the library's existing
/// vocabulary so a new sound lands next to the ones like it. It never blocks an import: any failure
/// keeps the tags the sound came with.
/// </remarks>
public sealed partial class SoundTagger
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-tagging failed for sound {Name}; keeping its original tags.")]
    private partial void LogTaggingFailed(Exception ex, string name);

    private const int MaxTags = 6;

    private const string Schema =
        """
        {"type":"object","additionalProperties":false,"required":["tags","useCase"],"properties":{
          "tags":{"type":"array","items":{"type":"string"}},
          "useCase":{"type":"string"}}}
        """;

    private const string SystemPrompt =
        """
        You catalogue meme sound effects for an AI video director. Given a sound's name and current tags,
        return up to 6 lowercase single-word or hyphenated tags describing its mood and the moments it
        fits (prefer tags from the EXISTING TAGS list when they fit), and useCase: one sentence of at
        most 15 words on when a meme editor would drop this sound in.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AiFoundryClient _ai;
    private readonly ILogger<SoundTagger> _logger;

    public SoundTagger(AiFoundryClient ai, ILogger<SoundTagger> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    /// <summary>
    /// Merges generated tags into <paramref name="sound"/> and fills its use hint. Returns false
    /// (leaving the sound untouched) when AI is off or the call fails.
    /// </summary>
    public async Task<bool> TagAsync(SoundAsset sound, IEnumerable<string> existingTags, CancellationToken ct)
    {
        if (!_ai.IsConfigured)
            return false;

        try
        {
            var vocabulary = string.Join(", ", existingTags.Take(200));
            var user = $"Name: {sound.DisplayName}\nCurrent tags: {string.Join(", ", sound.ActionVectorTags)}\n" +
                       $"EXISTING TAGS: {vocabulary}";

            var raw = await _ai.CompleteJsonAsync(
                new AiCall("sound-tagger", _ai.Deployment, "sound_tags", Schema) { MaxOutputTokens = 1_500 },
                [new SystemChatMessage(SystemPrompt), new UserChatMessage(user)],
                ct);
            var answer = JsonSerializer.Deserialize<TagAnswer>(raw, JsonOpts);
            if (answer is null)
                return false;

            Apply(sound, answer.Tags ?? [], answer.UseCase);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogTaggingFailed(ex, sound.DisplayName);
            return false;
        }
    }

    /// <summary>Generated tags join the originals (normalised, deduplicated); a real use hint replaces a placeholder.</summary>
    internal static void Apply(SoundAsset sound, IEnumerable<string> tags, string? useCase)
    {
        var generated = tags
            .Select(t => t.Trim().ToLowerInvariant().Replace(' ', '-'))
            .Where(t => t.Length is > 1 and <= 24)
            .Take(MaxTags);
        sound.ActionVectorTags = [.. sound.ActionVectorTags.Concat(generated).Distinct(StringComparer.OrdinalIgnoreCase)];

        if (!string.IsNullOrWhiteSpace(useCase))
            sound.UseCase = useCase.Trim().Length > 160 ? useCase.Trim()[..160] : useCase.Trim();
    }

    private sealed record TagAnswer(
        [property: JsonPropertyName("tags")] string[]? Tags,
        [property: JsonPropertyName("useCase")] string? UseCase);
}
