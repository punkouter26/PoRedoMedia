using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Client.Http;

/// <summary>
/// Attaches the antiforgery request token to every write. The secret half of the pair is an
/// HttpOnly cookie WASM cannot read; this supplies the other half from /api/antiforgery/token.
/// </summary>
/// <remarks>
/// A 400 on a write is retried once with a fresh token, and that retry is load-bearing: the token
/// is bound to the caller's identity, so one fetched before sign-in stops validating after it.
/// The first signed-in write of a session normally takes this path. One retry only, so a request
/// that is genuinely malformed still surfaces its 400.
/// </remarks>
public sealed partial class AntiforgeryTokenHandler(Func<HttpClient> tokenClientFactory) : DelegatingHandler
{
    private const string TokenHeader = "X-CSRF-TOKEN";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isWrite = request.Method == HttpMethod.Post || request.Method == HttpMethod.Put
            || request.Method == HttpMethod.Patch || request.Method == HttpMethod.Delete;
        if (!isWrite)
            return await base.SendAsync(request, cancellationToken);

        // Buffer the body up front: the first send consumes it, and a retry must replay it.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        await StampAsync(request, forceRefresh: false, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.BadRequest)
            return response;

        response.Dispose();
        var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
        {
            retry.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
                retry.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        await StampAsync(retry, forceRefresh: true, cancellationToken);
        return await base.SendAsync(retry, cancellationToken);
    }

    private async Task StampAsync(HttpRequestMessage request, bool forceRefresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (forceRefresh || _token is null)
            {
                // A separate client: re-entering this pipeline while holding the gate would deadlock.
                using var client = tokenClientFactory();
                _token = (await client.GetFromJsonAsync("api/antiforgery/token", TokenJson.Default.TokenResponse, ct))?.Token;
            }
        }
        catch (HttpRequestException)
        {
            // Unreachable token endpoint: send unstamped and let the server answer.
        }
        finally
        {
            _gate.Release();
        }

        request.Headers.Remove(TokenHeader);
        if (!string.IsNullOrEmpty(_token))
            request.Headers.TryAddWithoutValidation(TokenHeader, _token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
        base.Dispose(disposing);
    }

    internal sealed record TokenResponse(string Token);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(TokenResponse))]
    private sealed partial class TokenJson : JsonSerializerContext;
}
