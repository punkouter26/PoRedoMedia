using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PoRedoMedia.Mobile.Models;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Talks to the PoRedoMedia server the way the web client does: a session cookie, the
/// <c>X-CSRF-TOKEN</c> header on writes, files uploaded straight to storage, and work done as runs.
/// </summary>
/// <remarks>
/// Sign-in uses <c>/dev-login</c>, which exists only on Development and Test servers. Every
/// endpoint needs a session, so against a Production server the app can do nothing until a real
/// phone sign-in exists; <see cref="EnsureAuthenticatedAsync"/> returns false and the UI says so.
/// </remarks>
public sealed class MobileApiClient(Func<Uri> baseUri)
{
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // Storage gets its own client: the session cookie and CSRF header must never be sent there.
    private static readonly HttpClient Storage = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly CookieContainer _cookies = new();
    private HttpClient? _client;
    private Uri? _clientBaseUri;
    private Uri? _sessionBaseUri;

    private sealed record AntiforgeryToken(string Token);

    public bool HasSession => _sessionBaseUri is not null && _sessionBaseUri == baseUri();

    private HttpClient Client()
    {
        var current = baseUri();
        if (_client is null || _clientBaseUri != current)
        {
            _client?.Dispose();
            _client = new HttpClient(new SocketsHttpHandler
            {
                CookieContainer = _cookies, AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false,
            })
            {
                BaseAddress = current,
                Timeout = TimeSpan.FromSeconds(90),
            };
            _clientBaseUri = current;
        }

        return _client;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Client().GetAsync("health/live", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Signs this install in as its own dev user and fetches the token writes must carry.</summary>
    public async Task<bool> EnsureAuthenticatedAsync(string user = "phone@poredomedia.local", CancellationToken ct = default)
    {
        if (HasSession)
            return true;

        try
        {
            var client = Client();
            using var login = await client.GetAsync($"dev-login?email={Uri.EscapeDataString(user)}", ct);
            // The token is bound to the signed-in user, so it is fetched after the cookie lands. A
            // server without /dev-login leaves us anonymous and the token then fails the check below.
            var token = await client.GetFromJsonAsync<AntiforgeryToken>("api/antiforgery/token", JsonSerializerOptions.Web, ct);
            using var check = await client.GetAsync("api/quota", ct);
            if (token is null || !check.IsSuccessStatusCode)
                return false;

            client.DefaultRequestHeaders.Remove(CsrfHeaderName);
            client.DefaultRequestHeaders.Add(CsrfHeaderName, token.Token);
            _sessionBaseUri = _clientBaseUri;
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Uploads a photo straight to storage and has the server check and register it.</summary>
    public async Task<MediaDto> UploadAsync(ImageCaptureResult image, CancellationToken ct = default)
    {
        using var reserve = await Client().PostAsJsonAsync(
            "api/media/sas", new UploadRequest(image.FileName, image.Bytes.LongLength), WireJson.Default.UploadRequest, ct);
        var ticket = await ReadAsync(reserve, WireJson.Default.UploadTicket, ct);

        using var content = new ByteArrayContent(image.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        using var put = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl) { Content = content };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var stored = await Storage.SendAsync(put, ct);
        if (!stored.IsSuccessStatusCode)
            throw new HttpRequestException($"The upload to storage failed ({(int)stored.StatusCode}).");

        using var confirm = await Client().PostAsync($"api/media/{ticket.Id}/confirm", null, ct);
        return await ReadAsync(confirm, WireJson.Default.MediaDto, ct);
    }

    /// <summary>
    /// Starts a run and polls it to the end. <paramref name="onUpdate"/> sees every poll, so the
    /// caller can show the current step and pick up outputs as they land.
    /// </summary>
    public async Task<RunDto> RunAsync(
        Guid sourceId, MediaFunction[] functions, Dictionary<string, string>? options,
        Action<RunDto>? onUpdate = null, CancellationToken ct = default)
    {
        using var started = await Client().PostAsJsonAsync(
            "api/runs", new RunRequest(sourceId, functions, options), WireJson.Default.RunRequest, ct);
        var run = await ReadAsync(started, WireJson.Default.RunDto, ct);

        while (true)
        {
            onUpdate?.Invoke(run);
            if (run.Status is RunStatus.Complete or RunStatus.Failed)
                return run;

            await Task.Delay(PollInterval, ct);
            using var polled = await Client().GetAsync($"api/runs/{run.Id}", ct);
            run = await ReadAsync(polled, WireJson.Default.RunDto, ct);
        }
    }

    public async Task<IReadOnlyList<MediaDto>> ListGalleryAsync(CancellationToken ct = default)
    {
        using var response = await Client().GetAsync("api/media/", ct);
        return await ReadAsync(response, WireJson.Default.ListMediaDto, ct);
    }

    /// <summary>Downloads a <see cref="MediaDto.Url"/> or <see cref="MediaDto.ThumbUrl"/>.</summary>
    public async Task<byte[]> GetBytesAsync(string appUrl, CancellationToken ct = default)
    {
        // The route answers with a redirect to storage. It is followed by hand so the session
        // cookie and CSRF header stay with the app and are not sent on to the storage host.
        using var response = await Client().GetAsync(appUrl.TrimStart('/'), ct);
        if (response.StatusCode is HttpStatusCode.Redirect && response.Headers.Location is { } blob)
            return await Storage.GetByteArrayAsync(blob, ct);

        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await Client().DeleteAsync($"api/media/{id}", ct);
        await ThrowIfFailedAsync(response, ct);
    }

    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        await ThrowIfFailedAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(type, ct)
            ?? throw new HttpRequestException("The server sent an empty reply.");
    }

    /// <summary>Turns a refusal into the sentence the server wrote for the user, not raw JSON.</summary>
    private async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode is HttpStatusCode.Unauthorized)
            _sessionBaseUri = null; // the cookie expired: the next call signs in again

        string? detail = null;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (problem.RootElement.TryGetProperty("detail", out var text))
                detail = text.GetString();
        }
        catch (JsonException)
        {
            // Not a problem document; the status below is all there is to say.
        }

        throw new HttpRequestException(detail ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Not signed in. Try again.",
            HttpStatusCode.NotFound => "That item no longer exists.",
            HttpStatusCode.TooManyRequests => "Today's run limit is used up.",
            _ => $"The server refused the request ({(int)response.StatusCode}).",
        }, null, response.StatusCode);
    }
}
