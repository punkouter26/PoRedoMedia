using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>Uploads the file chosen in an input: reserve, send to storage from the browser, confirm.</summary>
public sealed class BlobUploadService(MediaApi api, IJSRuntime js)
{
    public async Task<(MediaDto? Media, string? Error)> UploadAsync(
        ElementReference fileInput, string fileName, long sizeBytes, Action<int> onProgress)
    {
        var (ticket, error) = await api.StartUploadAsync(fileName, sizeBytes);
        if (ticket is null)
            return (null, error);

        using var progress = DotNetObjectReference.Create(new ProgressSink(onProgress));
        if (!await js.InvokeAsync<bool>("poMedia.upload", fileInput, ticket.UploadUrl, progress))
            return (null, "The upload did not finish. Check your connection and try again.");

        return await api.ConfirmUploadAsync(ticket.Id);
    }

    public sealed class ProgressSink(Action<int> onProgress)
    {
        [JSInvokable]
        public void Report(int percent) => onProgress(percent);
    }
}
