using Microsoft.Extensions.AI;
using PoRedoMedia.Api.Common.Ai;

namespace PoRedoMedia.Api.Features.MemeCaption;

public sealed record MemeCaptionText(string Top, string Bottom);

/// <summary>Writes the two lines of a meme from a description of the picture.</summary>
public interface ICaptionWriter
{
    Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default);
}

/// <param name="judge">When present, several captions are drafted in one call and the judge keeps the best.</param>
public sealed class ChatCaptionWriter(IChatClient chat, AiJudge? judge = null) : ICaptionWriter
{
    /// <summary>Drafts asked for in the one call. Each is a dozen output tokens; the judge is one more small call.</summary>
    internal const int Drafts = 4;

    internal const string Rubric =
        "Funny on first read; the joke lands on the most specific, surprising detail of THIS scene, not a "
        + "generic label; the bottom line is the punchline; 3-7 words per line. Safety: never about anyone's "
        + "appearance, body, age or identity; no slurs, profanity or sexual content.";

    private const string SystemPrompt =
        "You are a meme caption generator. Write a top and a bottom caption, 3-7 words each. "
        + "The joke lands on the most specific, surprising thing in the scene, not on a generic "
        + "label, so use the description's details. Make it humorous and relatable."
        + " Joke about the situation, the objects, and the absurdity of the scene; never about any "
        + "person's appearance, body, age, or identity. "
        + "Never reference or imply race, ethnicity, skin tone, disability, body weight or size, "
        + "age, gender identity, sexual orientation, religion, or medical conditions. "
        + "No slurs, no profanity, no sexual content, no insults directed at a person. "
        + "If the elements describe children, keep it wholesome.";

    public async Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        // The description carries the specifics a joke needs; tags alone only ever gave generic captions.
        var scene = string.IsNullOrWhiteSpace(description)
            ? $"Scene elements: {string.Join(", ", tags)}"
            : $"Scene: {description}\nElements: {string.Join(", ", tags)}";

        if (judge is null)
        {
            var response = await chat.GetResponseAsync<MemeCaptionText>(
                [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, scene)], cancellationToken: ct);
            return response.TryGetResult(out var caption) && Usable(caption)
                ? caption
                : throw new RunStepException("The caption model returned nothing usable. Try again, or type the text yourself.");
        }

        var drafted = await chat.GetResponseAsync<CaptionDrafts>(
        [
            new ChatMessage(ChatRole.System, SystemPrompt + $" Write {Drafts} different captions, each taking a different angle on the scene."),
            new ChatMessage(ChatRole.User, scene),
        ], cancellationToken: ct);

        List<MemeCaptionText> drafts = drafted.TryGetResult(out var answer) ? [.. (answer.Captions ?? []).Where(Usable)] : [];
        if (drafts.Count == 0)
            throw new RunStepException("The caption model returned nothing usable. Try again, or type the text yourself.");

        var best = await judge.PickAsync($"Meme caption for: {description}", Rubric, [.. drafts.Select(d => $"{d.Top} / {d.Bottom}")], ct);
        return drafts[best];
    }

    private static bool Usable(MemeCaptionText? caption) => caption is not null && !string.IsNullOrWhiteSpace(caption.Top + caption.Bottom);

    private sealed record CaptionDrafts(MemeCaptionText[]? Captions);
}

public sealed class MockCaptionWriter : ICaptionWriter
{
    public Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        Task.FromResult(new MemeCaptionText("Mock top text", "Mock bottom text"));
}
