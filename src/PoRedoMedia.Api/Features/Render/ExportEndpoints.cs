using System.Security.Claims;

namespace PoRedoMedia.Api.Features.Render;

/// <summary>Other forms of a rendered video: a looping GIF, and its roast as a sound file.</summary>
public static class ExportEndpoints
{
    /// <summary>A GIF covers the opening of the video only: longer ones run to tens of megabytes.</summary>
    internal const int GifSeconds = 8;

    /// <summary>One pass: 12 frames a second, 480 wide, on a 128-colour palette made from the clip itself.</summary>
    internal static string GifArgs(string source, string output) =>
        $"-v error -t {GifSeconds} -i \"{source}\" -filter_complex \"fps=12,scale=480:-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer\" -loop 0 -y \"{output}\"";

    public static IEndpointRouteBuilder MapExports(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/media/{id}/gif", MakeGifAsync).AddEndpointFilter<AntiforgeryValidationFilter>();
        app.MapGet("/api/media/{id}/gif", GifAsync);
        app.MapGet("/api/media/{id}/roast-audio", RoastAudioAsync);
        return app;
    }

    /// <summary>
    /// Makes the video's GIF. The first request encodes it, which costs one run credit; it is kept
    /// with the video, so asking again is free. A write, not a link: a link that spends a credit
    /// can be followed twice, and its refusal would replace the page it was clicked on.
    /// </summary>
    private static async Task<IResult> MakeGifAsync(
        MediaId id, HttpContext http, IMediaRepository media, BlobStorageService blobs, StorageClients storage,
        FFmpegProcess ffmpeg, IRenderQuota quota, Features.Runs.RunDispatcher dispatcher, CancellationToken ct)
    {
        var owner = UserId.From(http.User);
        if (await media.GetAsync(owner, id, ct) is not { Kind: Shared.Enums.MediaKind.Video, Status: MediaStatus.Ready } item)
            return Results.NotFound();

        var path = MediaAnalysisPaths.Gif(id.Value);
        if (await blobs.ExistsAsync(path, ct))
            return Results.NoContent();

        // The video's lane, as a run takes it: a second click, or a run on it, waits its turn.
        if (!dispatcher.TryReserve(id))
            return Results.Problem(detail: "This video is busy. Try again in a moment.", statusCode: StatusCodes.Status409Conflict);

        var file = Path.Combine(Path.GetTempPath(), $"poredomedia-{id}.gif");
        var spent = false;
        try
        {
            var (allowed, status) = await quota.TryConsumeAsync(owner, ct);
            if (!allowed)
            {
                return Results.Problem(
                    detail: $"Making a GIF uses a run, and all {status.Limit} for today are used. More are available at {status.ResetsAt:HH:mm} UTC.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            spent = true;
            // ffmpeg reads the video straight from storage; only the GIF touches this disk.
            var link = storage.CreateReadLink(item.SourcePath, TimeSpan.FromMinutes(10));
            if (await ffmpeg.RunAsync(GifArgs(link.ToString(), file), id, ct) != 0 || !File.Exists(file))
            {
                spent = false;
                await quota.RefundAsync(owner, CancellationToken.None);
                return Results.Problem(detail: "The GIF could not be made from this video.", statusCode: StatusCodes.Status500InternalServerError);
            }

            await blobs.UploadFileAsync(path, file, "image/gif", ct);
            return Results.NoContent();
        }
        catch when (spent)
        {
            await quota.RefundAsync(owner, CancellationToken.None);
            throw;
        }
        finally
        {
            dispatcher.Release(id);
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>The GIF, once it has been made. 404 until then.</summary>
    private static async Task<IResult> GifAsync(MediaId id, HttpContext http, IMediaRepository media, BlobDelivery delivery, CancellationToken ct) =>
        await media.GetAsync(UserId.From(http.User), id, ct) is { Kind: Shared.Enums.MediaKind.Video, Status: MediaStatus.Ready }
            ? await delivery.ServeAsync(http, MediaAnalysisPaths.Gif(id.Value), "meme.gif", ct: ct)
            : Results.NotFound();

    /// <summary>The insults alone, as the voice performed them. 404 when the video has no roast.</summary>
    private static async Task<IResult> RoastAudioAsync(
        MediaId id, ClaimsPrincipal user, HttpContext http, IMediaRepository media, BlobStorageService blobs, BlobDelivery delivery, CancellationToken ct)
    {
        if (await media.GetAsync(UserId.From(user), id, ct) is null)
            return Results.NotFound();

        foreach (var extension in new[] { "mp3", "wav" })
        {
            var path = MediaAnalysisPaths.RoastClip(id.Value, 0, extension);
            if (await blobs.ExistsAsync(path, ct))
                return await delivery.ServeAsync(http, path, $"roast.{extension}", ct: ct);
        }

        return Results.NotFound();
    }
}
