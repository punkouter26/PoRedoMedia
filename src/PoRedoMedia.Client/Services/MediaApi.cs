using System.Net.Http.Json;
using System.Text.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>The gallery and upload calls. A failed call returns the server's reason, never throws for a 4xx.</summary>
public sealed class MediaApi(HttpClient http)
{
    public async Task<List<MediaDto>> ListAsync() =>
        await http.GetFromJsonAsync("api/media", WireJson.Default.ListMediaDto) ?? [];

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

    /// <summary>The <c>detail</c> of a problem response, or a plain fallback.</summary>
    public static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException)
        {
            // Not a problem document; fall through.
        }

        return $"The request failed ({(int)response.StatusCode}).";
    }
}
