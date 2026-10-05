using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using SixLabors.ImageSharp;

namespace PoRedoMedia.Api.Features.Media;

public static class MediaEndpoints
{
    private static readonly TimeSpan UploadLinkLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapMedia(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/media").RequireAntiforgeryValidation();
        group.MapPost("/sas", StartUploadAsync);
        group.MapPost("/{id}/confirm", ConfirmUploadAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}/content", ContentAsync);
        group.MapGet("/{id}/thumb", ThumbAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        return app;
    }

    /// <summary>Reserves a media item and returns a link the browser uploads the file to directly.</summary>
    private static async Task<Results<Ok<UploadTicket>, ProblemHttpResult>> StartUploadAsync(
        UploadRequest request, ClaimsPrincipal user, IMediaRepository media, StorageClients storage, CancellationToken ct)
    {
        var type = UploadValidation.Classify(request.FileName, request.SizeBytes);
        if (type.Error is not null)
            return Refused(type.Error);

        var item = new MediaItem
        {
            Owner = UserId.From(user),
            Id = MediaId.New(),
            Kind = type.Kind,
            Status = MediaStatus.Uploading,
            Origin = "Upload",
            Title = Path.GetFileName(request.FileName),
            ContentType = type.ContentType,
            Extension = type.Extension,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await media.SaveAsync(item, ct);

        var expiresAt = DateTimeOffset.UtcNow.Add(UploadLinkLifetime);
        var link = await storage.CreateUploadLinkAsync(item.SourcePath, expiresAt);
        return TypedResults.Ok(new UploadTicket(item.Id.Value, link.ToString(), expiresAt));
    }

    /// <summary>
    /// Checks what actually arrived. The upload link cannot limit size or content, so nothing the
    /// client declared is trusted here: size, image format and video length are all measured.
    /// </summary>
    private static async Task<Results<Ok<MediaDto>, NotFound, ProblemHttpResult>> ConfirmUploadAsync(
        MediaId id, ClaimsPrincipal user, IMediaRepository media, StorageClients storage, BlobStorageService blobs,
        FFmpegProcess ffmpeg, Thumbnails thumbnails, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null || item.Status != MediaStatus.Uploading)
            return TypedResults.NotFound();

        var blob = storage.Blob(item.SourcePath);
        if (!await blob.ExistsAsync(ct))
            return Refused("The file has not been uploaded yet.");

        var size = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
        double? duration = null;
        string? problem = null;

        if (size > UploadValidation.MaxBytes(item.Kind))
        {
            problem = item.Kind == MediaKind.Video ? "Videos can be up to 200 MB." : "Images can be up to 10 MB.";
        }
        else if (item.Kind == MediaKind.Image)
        {
            try
            {
                Image.Identify(await blobs.ReadAllBytesAsync(item.SourcePath, ct));
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
            {
                problem = "That file is not a readable image.";
            }
        }
        else
        {
            // ffprobe reads the headers straight from storage; the video is never copied here.
            duration = await ffmpeg.DurationSecondsAsync(storage.CreateReadLink(item.SourcePath, TimeSpan.FromMinutes(5)).ToString(), ct);
            if (duration <= 0)
                problem = "That file is not a readable video.";
            else if (duration > UploadValidation.MaxVideoSeconds)
                problem = "Videos can be up to 10 minutes long.";
        }

        if (problem is not null)
        {
            await RemoveAsync(item, media, blobs, ct);
            return Refused(problem);
        }

        item = item with { Status = MediaStatus.Ready, SizeBytes = size, DurationSeconds = duration };
        await thumbnails.CreateAsync(item, ct);
        await media.SaveAsync(item, ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Ok<List<MediaDto>>> ListAsync(ClaimsPrincipal user, IMediaRepository media, CancellationToken ct) =>
        TypedResults.Ok((await media.ListAsync(UserId.From(user), ct))
            .Where(m => m.Status == MediaStatus.Ready)
            .Select(ToDto)
            .ToList());

    private static async Task<IResult> ContentAsync(
        MediaId id, bool? download, HttpContext http, IMediaRepository media, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Status: MediaStatus.Ready } item
            ? await BlobDelivery.ServeAsync(http, item.SourcePath, download == true ? DownloadName(item) : null, ct)
            : Results.NotFound();

    private static async Task<IResult> ThumbAsync(MediaId id, HttpContext http, IMediaRepository media, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Status: MediaStatus.Ready }
            ? await BlobDelivery.ServeAsync(http, MediaBlobPaths.Thumbnail(id), ct: ct)
            : Results.NotFound();

    private static async Task<Results<Ok<MediaDto>, NotFound, ProblemHttpResult>> UpdateAsync(
        MediaId id, MediaUpdateRequest request, ClaimsPrincipal user, IMediaRepository media, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null)
            return TypedResults.NotFound();

        var title = request.Title?.Trim();
        if (title is { Length: 0 or > 120 })
            return Refused("A title must be 1 to 120 characters.");

        item = item with { Title = title ?? item.Title, Pinned = request.Pinned ?? item.Pinned };
        await media.SaveAsync(item, ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        MediaId id, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs, CancellationToken ct)
    {
        var item = await media.GetAsync(UserId.From(user), id, ct);
        if (item is null)
            return TypedResults.NotFound();

        await RemoveAsync(item, media, blobs, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Blobs first: if the row went first and the blob delete failed, nothing would point at the orphans.</summary>
    private static async Task RemoveAsync(MediaItem item, IMediaRepository media, BlobStorageService blobs, CancellationToken ct)
    {
        await blobs.DeletePrefixAsync(MediaBlobPaths.Prefix(item.Id), ct);
        await media.DeleteAsync(item.Owner, item.Id, ct);
    }

    private static string DownloadName(MediaItem item)
    {
        var name = string.Concat(Path.GetFileNameWithoutExtension(item.Title).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '));
        return (name.Length == 0 ? "media" : name) + item.Extension;
    }

    private static ProblemHttpResult Refused(string reason) =>
        TypedResults.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);

    public static MediaDto ToDto(MediaItem item) => new(
        item.Id.Value, item.Kind, item.Title, item.Origin, item.ParentId?.Value, item.ContentType, item.SizeBytes,
        item.DurationSeconds, item.Pinned, item.ShareToken is not null, item.CreatedAt,
        $"/api/media/{item.Id}/content", $"/api/media/{item.Id}/thumb");
}
