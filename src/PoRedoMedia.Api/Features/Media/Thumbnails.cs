using PoRedoMedia.Shared.Enums;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace PoRedoMedia.Api.Features.Media;

/// <summary>Makes the 480px JPEG the gallery shows for an image or a video.</summary>
public sealed class Thumbnails(BlobStorageService blobs, StorageClients storage, FFmpegProcess ffmpeg)
{
    private const int MaxEdge = 480;

    public async Task CreateAsync(MediaItem item, CancellationToken ct)
    {
        if (item.Kind == MediaKind.Audio)
            return;

        using var jpeg = new MemoryStream();
        if (item.Kind == MediaKind.Image)
        {
            using var image = Image.Load(await blobs.ReadAllBytesAsync(item.SourcePath, ct));
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxEdge, MaxEdge), Mode = ResizeMode.Max }));
            await image.SaveAsJpegAsync(jpeg, ct);
        }
        else
        {
            // ffmpeg reads the first frame straight from storage; the video is never copied here.
            var frame = Path.Combine(Path.GetTempPath(), $"poredomedia-thumb-{item.Id}.jpg");
            try
            {
                var source = storage.CreateReadLink(item.SourcePath, TimeSpan.FromMinutes(5));
                var exit = await ffmpeg.RunAsync($"-y -i \"{source}\" -frames:v 1 -vf \"scale={MaxEdge}:-2\" \"{frame}\"", $"thumb {item.Id}", ct);
                if (exit != 0 || !File.Exists(frame))
                    return; // A video without a poster is still usable.
                await jpeg.WriteAsync(await File.ReadAllBytesAsync(frame, ct), ct);
            }
            finally
            {
                File.Delete(frame);
            }
        }

        jpeg.Position = 0;
        await blobs.UploadAsync(MediaBlobPaths.Thumbnail(item.Id), jpeg, "image/jpeg", ct);
    }
}
