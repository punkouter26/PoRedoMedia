using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Background removal with BRIA RMBG 2.0, run by fal and reached through Hugging Face's
/// Inference Providers router with a Hugging Face token. fal answers through its queue: the
/// request is submitted, its status polled, then its result read.
/// </summary>
public sealed class HuggingFaceBackgroundRemover(IConfiguration configuration, IHttpClientFactory http) : IBackgroundRemover
{
    public const string HttpClientName = "huggingface";

    private const string Router = "https://router.huggingface.co/fal-ai";
    private const string Model = "fal-ai/bria/background/remove";
    private const string Queue = "?_subdomain=queue";
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);

    public async Task<byte[]> RemoveAsync(byte[] image, string contentType, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        var submitted = await SendAsync(client, HttpMethod.Post, $"{Router}/{Model}{Queue}", new
        {
            image_url = $"data:{contentType};base64,{Convert.ToBase64String(image)}",
            // Asks for the result inline, as a data URL, rather than a link to fal's storage.
            sync_mode = true,
        }, ct);

        // Both the status and the result live under the path of the response link, on the router.
        var path = new Uri(submitted.GetProperty("response_url").GetString()!).AbsolutePath;
        var status = submitted.TryGetProperty("status", out var first) ? first.GetString() : null;
        var waited = Stopwatch.StartNew();
        while (status != "COMPLETED")
        {
            if (waited.Elapsed > GiveUpAfter)
                throw new RunStepException("Removing the background took too long. Try again.");
            if (status is "FAILED" or "ERROR" or "CANCELLED")
                throw new InvalidOperationException($"Background removal ended as {status}.");

            await Task.Delay(PollEvery, ct);
            status = (await SendAsync(client, HttpMethod.Get, $"{Router}{path}/status{Queue}", null, ct)).GetProperty("status").GetString();
        }

        var result = await SendAsync(client, HttpMethod.Get, $"{Router}{path}{Queue}", null, ct);
        return await ReadImageAsync(client, result, ct);
    }

    /// <summary>The picture in a result: inline as a data URL, or behind a link.</summary>
    internal static async Task<byte[]> ReadImageAsync(HttpClient client, JsonElement result, CancellationToken ct)
    {
        if (!result.TryGetProperty("image", out var image) || image.GetProperty("url").GetString() is not { Length: > 0 } url)
            throw new RunStepException("The background remover returned no picture. Try again.");

        if (url.StartsWith("data:", StringComparison.Ordinal))
            return Convert.FromBase64String(url[(url.IndexOf(',', StringComparison.Ordinal) + 1)..]);

        // A link to fal's own storage, which needs no key.
        return await client.GetByteArrayAsync(new Uri(url), ct);
    }

    private async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration[ConfigKeys.HuggingFaceApiKey]);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request, ct);
        if ((int)response.StatusCode == 429)
            throw new RunStepException("The background remover is busy right now. Wait a minute and try again.");
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.PaymentRequired)
            throw new RunStepException("The background remover refused this server's key. Its Hugging Face credit may have run out.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Hugging Face returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.Clone();
    }
}
