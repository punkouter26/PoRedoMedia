using System.Security.Claims;
using System.Text;
using PoRedoMedia.Api.Features.Render;

namespace PoRedoMedia.Api.Features.Captions;

public static class CaptionsEndpoints
{
    public static IEndpointRouteBuilder MapCaptions(this IEndpointRouteBuilder app)
    {
        // The subtitles burned into a rendered video, as an SRT file. 404 when it has none.
        app.MapGet("/api/media/{id}/captions.srt", async (
            MediaId id, ClaimsPrincipal user, IMediaRepository media, BlobStorageService blobs, CancellationToken ct) =>
        {
            if (await media.GetAsync(UserId.From(user), id, ct) is null
                || !await blobs.ExistsAsync(MediaAnalysisPaths.Transcript(id.Value), ct))
            {
                return Results.NotFound();
            }

            var srt = SrtWriter.Write(await MediaTranscript.LoadAsync(blobs, id, ct));
            return Results.File(Encoding.UTF8.GetBytes(srt), "application/x-subrip", "captions.srt");
        });
        return app;
    }
}
