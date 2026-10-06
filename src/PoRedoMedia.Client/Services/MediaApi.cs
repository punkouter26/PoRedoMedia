using System.Net.Http.Json;
using System.Text.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>The gallery and upload calls. A failed call returns the server's reason, never throws for a 4xx.</summary>
public sealed class MediaApi(HttpClient http)
{
    public async Task<List<MediaDto>> ListAsync() =>
        await http.GetFromJsonAsync("api/media", WireJson.Default.ListMediaDto) ?? [];

    /// <summary>One item, or null when it is gone or not the user's.</summary>
    public async Task<MediaDto?> GetAsync(Guid id)
    {
        using var response = await http.GetAsync($"api/media/{id}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(WireJson.Default.MediaDto) : null;
    }

    public async Task<(UploadTicket? Ticket, string? Error)> StartUploadAsync(string fileName, long sizeBytes)
    {
        using var response = await http.PostAsJsonAsync("api/media/sas", new UploadRequest(fileName, sizeBytes), WireJson.Default.UploadRequest);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.UploadTicket), null)
            : (null, await ReasonAsync(response));
    }

    public async Task<(MediaDto? Media, string? Error)> ConfirmUploadAsync(Guid id)
    {
        using var response = await http.PostAsync($"api/media/{id}/confirm", null);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.MediaDto), null)
            : (null, await ReasonAsync(response));
    }

    public async Task<(MediaDto? Media, string? Error)> UpdateAsync(Guid id, string? title, bool? pinned)
    {
        using var response = await http.PutAsJsonAsync($"api/media/{id}", new MediaUpdateRequest(title, pinned), WireJson.Default.MediaUpdateRequest);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.MediaDto), null)
            : (null, await ReasonAsync(response));
    }

    public async Task<string?> DeleteAsync(Guid id)
    {
        using var response = await http.DeleteAsync($"api/media/{id}");
        return response.IsSuccessStatusCode ? null : await ReasonAsync(response);
    }

    /// <summary>
    /// The item's public link. Asking again returns the same one. <paramref name="feed"/> posts it
    /// to the feed or takes it off; null leaves that as it is.
    /// </summary>
    public async Task<(ShareLinkDto? Link, string? Error)> ShareAsync(Guid id, bool? feed = null)
    {
        using var response = await http.PostAsync($"api/media/{id}/share{(feed is { } on ? $"?feed={(on ? "true" : "false")}" : "")}", null);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.ShareLinkDto), null)
            : (null, await ReasonAsync(response));
    }

    /// <summary>Makes the video's GIF if it has none yet, which uses one run. Returns the reason when it could not.</summary>
    public async Task<string?> MakeGifAsync(Guid id)
    {
        using var response = await http.PostAsync($"api/media/{id}/gif", null);
        return response.IsSuccessStatusCode ? null : await ReasonAsync(response);
    }

    /// <summary>What users have posted to the feed: trending first, or newest first.</summary>
    public async Task<List<FeedItemDto>> FeedAsync(bool newest) =>
        await http.GetFromJsonAsync($"api/feed{(newest ? "?sort=new" : "")}", WireJson.Default.ListFeedItemDto) ?? [];

    public async Task<string?> StopSharingAsync(Guid id)
    {
        using var response = await http.DeleteAsync($"api/media/{id}/share");
        return response.IsSuccessStatusCode ? null : await ReasonAsync(response);
    }

    /// <summary>The <c>detail</c> of a failed response, or a plain fallback.</summary>
    public static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (problem.RootElement.TryGetProperty("detail", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException)
        {
            // Not a problem document; fall through.
        }

        return $"The request failed ({(int)response.StatusCode}).";
    }
}
