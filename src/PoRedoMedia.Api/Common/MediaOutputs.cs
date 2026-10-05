using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Common;

/// <summary>Saves what a function produced as a new gallery item: blob, thumbnail and row.</summary>
public sealed class MediaOutputs(BlobStorageService blobs, IMediaRepository media, Thumbnails thumbnails)
{
    public async Task<MediaItem> SaveAsync(
        MediaItem madeFrom, MediaFunction function, MediaKind kind, byte[] content, string contentType, string extension,
        CancellationToken ct, double? durationSeconds = null)
    {
        var item = new MediaItem
        {
            Owner = madeFrom.Owner,
            Id = MediaId.New(),
            Kind = kind,
            Status = MediaStatus.Ready,
            Origin = function.ToString(),
            ParentId = madeFrom.Id,
            Title = $"{FunctionStack.Label(function)} · {Path.GetFileNameWithoutExtension(madeFrom.Title)}",
            ContentType = contentType,
            Extension = extension,
            SizeBytes = content.Length,
            DurationSeconds = durationSeconds,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await blobs.UploadAsync(item.SourcePath, content, contentType, ct);
        await thumbnails.CreateAsync(item, ct);
        // The row goes last, so a failure above leaves no gallery item pointing at missing bytes.
        await media.SaveAsync(item, ct);
        return item;
    }
}
