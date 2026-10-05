using System.Text;
using System.Text.Json;

namespace PoRedoMedia.Api.Features.RapRoast;

/// <summary>
/// A structured read of a photo — named slots rather than a paragraph.
/// </summary>
/// <remarks>
/// Free prose forces the lyric writer to re-parse the description and latch onto whatever it
/// happens to notice, which is how "mismatched outfit, chaotic scene" comes out. Named slots let it
/// target specifics deliberately, let the UI render fields instead of a wall of text, and — unlike
/// prose — can actually be asserted on in a test.
/// </remarks>
/// <param name="Outfit">Specific garments, colours, fit, condition.</param>
/// <param name="Pose">Body position and what the hands are doing.</param>
/// <param name="Expression">Facial expression.</param>
/// <param name="Setting">Where this is, and what is on the walls or surfaces behind.</param>
/// <param name="Props">Objects, food, and their exact state.</param>
/// <param name="TextInImage">Text visible in the frame — signage, slogans, brands.</param>
/// <param name="MostIncongruousDetail">The single funniest or most out-of-place thing present.</param>
public sealed record SceneSnapshot(
    IReadOnlyList<string> Outfit,
    string? Pose,
    string? Expression,
    string? Setting,
    IReadOnlyList<string> Props,
    IReadOnlyList<string> TextInImage,
    string? MostIncongruousDetail)
{
    public static SceneSnapshot Empty { get; } = new([], null, null, null, [], [], null);

    /// <summary>True when enough was extracted to be worth writing from.</summary>
    public bool HasSubstance =>
        Outfit.Count > 0 || Props.Count > 0 || !string.IsNullOrWhiteSpace(Pose)
        || !string.IsNullOrWhiteSpace(Setting) || !string.IsNullOrWhiteSpace(MostIncongruousDetail);

    /// <summary>
    /// Renders the snapshot as prose for the lyric writer and the "what the AI saw" panel.
    /// Empty slots are omitted rather than emitted blank.
    /// </summary>
    public string ToProse()
    {
        var sb = new StringBuilder();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.Append(label).Append(": ").Append(value.Trim()).Append(". ");
        }

        void AddList(string label, IReadOnlyList<string> values)
        {
            if (values.Count > 0) sb.Append(label).Append(": ").Append(string.Join(", ", values)).Append(". ");
        }

        AddList("Wearing", Outfit);
        Add("Pose", Pose);
        Add("Expression", Expression);
        Add("Setting", Setting);
        AddList("Props", Props);
        AddList("Text in frame", TextInImage);
        Add("Most incongruous detail", MostIncongruousDetail);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Parses the model's JSON. Tolerant by design: a malformed or partial response should degrade
    /// to whatever slots did parse, never throw and lose the whole roast.
    /// </summary>
    public static SceneSnapshot Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(Unfence(json));
            var root = doc.RootElement;

            return new SceneSnapshot(
                Outfit: StringList(root, "outfit"),
                Pose: Str(root, "pose"),
                Expression: Str(root, "expression"),
                Setting: Str(root, "setting"),
                Props: StringList(root, "props"),
                TextInImage: StringList(root, "text_in_image"),
                MostIncongruousDetail: Str(root, "most_incongruous_detail"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Strips markdown fences some models emit despite being told not to.</summary>
    private static string Unfence(string content)
    {
        var text = content.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0) text = text[(newline + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        }

        // Some models wrap the object in commentary; take the outermost braces.
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? text[open..(close + 1)] : text;
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()?.Trim() is { Length: > 0 } s ? s : null
            : null;

    private static IReadOnlyList<string> StringList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return [];

        // Accept a bare string where an array was asked for — models do this routinely.
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString()?.Trim() is { Length: > 0 } single ? [single] : [];

        if (el.ValueKind != JsonValueKind.Array) return [];

        return [.. el.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.String)
            .Select(i => i.GetString()?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)];
    }
}
