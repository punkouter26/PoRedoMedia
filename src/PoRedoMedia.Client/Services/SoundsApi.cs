using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>The meme sound library.</summary>
public sealed class SoundsApi(HttpClient http)
{
    public const long MaxUploadBytes = 15 * 1024 * 1024;

    /// <summary>The whole library. It is a few dozen to a few hundred small rows, so the page filters it locally.</summary>
    public async Task<List<SoundAssetDto>> ListAsync() =>
        await http.GetFromJsonAsync("api/sounds", WireJson.Default.ListSoundAssetDto) ?? [];

    public async Task<string?> SetFavoriteAsync(Guid soundId, bool favorite)
    {
        using var response = favorite
            ? await http.PutAsync($"api/sounds/favorites/{soundId}", null)
            : await http.DeleteAsync($"api/sounds/favorites/{soundId}");
        return response.IsSuccessStatusCode ? null : await MediaApi.ReasonAsync(response);
    }

    /// <summary>Deletes one of the user's own uploads.</summary>
    public async Task<string?> DeleteAsync(Guid soundId)
    {
        using var response = await http.DeleteAsync($"api/sounds/{soundId}");
        return response.IsSuccessStatusCode ? null : await MediaApi.ReasonAsync(response);
    }

    public async Task<(SoundAssetDto? Sound, string? Error)> UploadAsync(IBrowserFile file)
    {
        using var content = new MultipartFormDataContent();
        var stream = new StreamContent(file.OpenReadStream(MaxUploadBytes));
        stream.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(file.ContentType) ? "audio/mpeg" : file.ContentType);
        content.Add(stream, "file", file.Name);

        using var response = await http.PostAsync("api/sounds/upload", content);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.SoundAssetDto), null)
            : (null, await MediaApi.ReasonAsync(response));
    }
}
