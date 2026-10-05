using Microsoft.Extensions.AI;

namespace PoRedoMedia.Api.Features.MemeCaption;

public sealed record MemeCaptionText(string Top, string Bottom);

/// <summary>Writes the two lines of a meme from a description of the picture.</summary>
public interface ICaptionWriter
{
    Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default);
}

public sealed class ChatCaptionWriter(IChatClient chat) : ICaptionWriter
{
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

        var response = await chat.GetResponseAsync<MemeCaptionText>(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, scene)], cancellationToken: ct);

        return response.TryGetResult(out var caption) && !string.IsNullOrWhiteSpace(caption.Top + caption.Bottom)
            ? caption
            : throw new RunStepException("The caption model returned nothing usable. Try again, or type the text yourself.");
    }
}

public sealed class MockCaptionWriter : ICaptionWriter
{
    public Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        Task.FromResult(new MemeCaptionText("Mock top text", "Mock bottom text"));
}
