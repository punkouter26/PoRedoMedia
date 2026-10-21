using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Common;

/// <summary>Makes the 480px JPEG the gallery shows for an image or a video.</summary>
public sealed class Thumbnails(BlobStorageService blobs, StorageClients storage, FFmpegProcess ffmpeg)
{
    private const int MaxEdge = 480;

    public async Task CreateAsync(MediaItem item, CancellationToken ct)
    {
        if (item.Kind == MediaKind.Audio)
            return;

        var jpeg = item.Kind == MediaKind.Image
            ? ImageBytes.ToJpeg(await blobs.ReadAllBytesAsync(item.SourcePath, ct), MaxEdge)
            : await VideoFrameAsync(item, ct);
        if (jpeg is null)
            return; // A video without a poster is still usable.

        await blobs.UploadAsync(MediaBlobPaths.Thumbnail(item.Id), jpeg, "image/jpeg", ct);
    }

    private async Task<byte[]?> VideoFrameAsync(MediaItem item, CancellationToken ct)
    {
        // ffmpeg reads the first frame straight from storage; the video is never copied here.
        var frame = Path.Combine(Path.GetTempPath(), $"poredomedia-thumb-{item.Id}.jpg");
        try
        {
            var source = storage.CreateReadLink(item.SourcePath, TimeSpan.FromMinutes(5));
            var exit = await ffmpeg.RunAsync($"-y -i \"{source}\" -frames:v 1 -vf \"scale={MaxEdge}:-2\" \"{frame}\"", $"thumb {item.Id}", ct);
            return exit == 0 && File.Exists(frame) ? await File.ReadAllBytesAsync(frame, ct) : null;
        }
        finally
        {
            File.Delete(frame);
        }
    }
}
