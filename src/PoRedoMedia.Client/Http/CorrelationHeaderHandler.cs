namespace PoRedoMedia.Client.Http;

/// <summary>
/// Stamps every API request with a per-tab <c>X-Session-ID</c> and a per-request
/// <c>X-Correlation-ID</c>, so one browser action can be followed through the server logs.
/// </summary>
public sealed class CorrelationHeaderHandler : DelegatingHandler
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Session-ID", _sessionId);
        if (!request.Headers.Contains("X-Correlation-ID"))
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", Guid.NewGuid().ToString("N"));

        return base.SendAsync(request, cancellationToken);
    }
}
