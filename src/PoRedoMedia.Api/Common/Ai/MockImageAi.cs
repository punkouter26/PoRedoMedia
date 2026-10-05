namespace PoRedoMedia.Api.Common.Ai;

// Mock providers, registered when MockAi.IsEnabled. None makes a network call, so mock mode
// cannot spend anything.

public sealed class MockVisionService : IVisionService, IVisionServiceRouter
{
    public Task<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct = default) =>
        Task.FromResult(new VisionResult("A mock description of the picture, written with no AI call.", ["mock", "sample"], 0.99));

    public IVisionService Resolve(string? modelId) => this;
}
