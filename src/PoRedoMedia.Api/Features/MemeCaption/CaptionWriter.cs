namespace PoRedoMedia.Api.Features.MemeCaption;

public sealed record MemeCaptionText(string Top, string Bottom);

/// <summary>Writes the two lines of a meme from a description of the picture.</summary>
public interface ICaptionWriter
{
    Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default);
}

public sealed class MockCaptionWriter : ICaptionWriter
{
    public Task<MemeCaptionText> WriteAsync(string description, IReadOnlyList<string> tags, CancellationToken ct = default) =>
        Task.FromResult(new MemeCaptionText("Mock top text", "Mock bottom text"));
}
