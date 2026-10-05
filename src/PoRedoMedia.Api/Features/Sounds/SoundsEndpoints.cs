using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Sounds;

public static class SoundsEndpoints
{
    /// <summary>
    /// Large enough for the studio's sound picker to take the whole library in one request; the
    /// Sound Library page still asks for small pages as it scrolls.
    /// </summary>
    private const int MaxPageSize = 1000;

    /// <summary>Upload limits. The size matches what the client checks before it sends the file.</summary>
    internal const long MaxUploadBytes = 15 * 1024 * 1024;
    internal const int MaxDisplayNameLength = 60;
    internal const int MaxUploadTags = 6;
    internal const int MaxTagLength = 24;

    public static IEndpointRouteBuilder MapSounds(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/sounds").RequireAntiforgeryValidation();

        group.MapGet("/", async (
            ISoundAssetRepository repository,
            ISoundFavoritesRepository favoritesRepository,
            HttpContext httpContext,
            string? tags,
            string? query,
            bool favoritesOnly = false,
            int limit = 20,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
        {
            var allSounds = await repository.LoadAllAsync(cancellationToken);
            var favorites = await favoritesRepository.GetAsync(UserId.From(httpContext.User), cancellationToken);

            var filtered = allSounds.AsEnumerable();

            if (favoritesOnly)
                filtered = filtered.Where(s => favorites.Contains(s.SoundId));

            if (!string.IsNullOrWhiteSpace(tags))
            {
                var requestedTags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                filtered = filtered.Where(s => requestedTags.Any(t => s.ActionVectorTags.Contains(t, StringComparer.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim();
                filtered = filtered.Where(s =>
                    s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.ActionVectorTags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
            }

            // Starred sounds first, then alphabetical — stable paging, and a user's picks are
            // always on page one of the studio's sound picker.
            var list = filtered
                .OrderByDescending(s => favorites.Contains(s.SoundId))
                .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var totalCount = list.Count;
            var page = list.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, MaxPageSize)).Select(s => new SoundAssetDto
            {
                SoundId = s.SoundId.Value,
                DisplayName = s.DisplayName,
                DurationMs = s.DurationMs,
                ActionVectorTags = s.ActionVectorTags,
                Attribution = s.Attribution,
                IsFavorite = favorites.Contains(s.SoundId),
            }).ToArray();

            return Results.Ok(new SoundPageDto(totalCount, page));
        });

        // PUT / DELETE /api/sounds/favorites/{soundId} — star or unstar a sound.
        group.MapPut("/favorites/{soundId:guid}", (SoundId soundId, ISoundFavoritesRepository favorites, HttpContext httpContext, CancellationToken ct)
                => SetFavoriteAsync(soundId, true, favorites, httpContext, ct))
            .WithName("StarSound")
            .WithTags("MemeLibrary");

        group.MapDelete("/favorites/{soundId:guid}", (SoundId soundId, ISoundFavoritesRepository favorites, HttpContext httpContext, CancellationToken ct)
                => SetFavoriteAsync(soundId, false, favorites, httpContext, ct))
            .WithName("UnstarSound")
            .WithTags("MemeLibrary");

        // POST /api/sounds/upload — upload custom meme sound
        group.MapPost("/upload", async (
            IFormFile file,
            string? displayName,
            string? tags,
            ISoundAssetRepository repository,
            BlobStorageService blobService,
            SoundTagger tagger,
            CancellationToken cancellationToken) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No audio file provided." });

            if (file.Length > MaxUploadBytes)
                return Results.BadRequest(new { error = $"Sounds can be at most {MaxUploadBytes / (1024 * 1024)} MB." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".wav" && ext != ".ogg")
                return Results.BadRequest(new { error = "Only .mp3, .wav, and .ogg files are supported." });

            var soundId = SoundId.New();
            var blobPath = $"{StorageNames.Containers.Sounds}/{soundId}{ext}";

            // Buffered (uploads are small clips) so the real length can be measured before storing.
            using var buffer = new MemoryStream();
            await using (var stream = file.OpenReadStream())
                await stream.CopyToAsync(buffer, cancellationToken);
            var durationMs = AudioDuration.EstimateMs(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), file.FileName);

            buffer.Position = 0;
            await blobService.UploadAsync(blobPath, buffer, file.ContentType ?? "audio/mpeg", cancellationToken);

            var asset = new SoundAsset
            {
                SoundId = soundId,
                DisplayName = CleanDisplayName(displayName, file.FileName),
                DurationMs = durationMs,
                ActionVectorTags = CleanTags(tags),
                BlobUrl = blobPath,
                Priority = false,
                UseCase = "custom-upload"
            };

            await tagger.TagAsync(asset, SoundVocabulary.Tags(await repository.LoadAllAsync(cancellationToken)), cancellationToken);
            await repository.AddSoundAsync(asset, cancellationToken);

            return Results.Created($"/api/sounds/{soundId}/stream", ToDto(asset));
        })
        // The built-in form check is off because the group's filter already validates the
        // token; running both rejects even a correct one.
        .DisableAntiforgery();

        // GET /api/sounds/{soundId}/stream — proxy sound file from blob storage to browser.
        group.MapGet("/{soundId:guid}/stream", async (
            SoundId soundId,
            ISoundAssetRepository repository,
            BlobStorageService blobService,
            CancellationToken cancellationToken) =>
        {
            var allSounds = await repository.LoadAllAsync(cancellationToken);
            var sound = allSounds.FirstOrDefault(s => s.SoundId == soundId);
            if (sound is null || !await blobService.ExistsAsync(sound.BlobPath, cancellationToken))
                return Results.NotFound();

            var stream = await blobService.OpenReadAsync(sound.BlobPath, cancellationToken);
            return Results.File(stream, contentType: "audio/mpeg", enableRangeProcessing: true);
        })
        .WithName("StreamSound")
        .WithTags("MemeLibrary")
        .Produces(200)
        .Produces(404);

        return routes;
    }

    private static async Task<IResult> SetFavoriteAsync(
        SoundId soundId,
        bool favorite,
        ISoundFavoritesRepository favorites,
        HttpContext httpContext,
        CancellationToken ct)
    {
        await favorites.SetAsync(UserId.From(httpContext.User), soundId, favorite, ct);
        return Results.Ok(new { soundId, isFavorite = favorite });
    }

    private static SoundAssetDto ToDto(SoundAsset s) => new()
    {
        SoundId = s.SoundId.Value,
        DisplayName = s.DisplayName,
        DurationMs = s.DurationMs,
        ActionVectorTags = s.ActionVectorTags,
        Attribution = s.Attribution,
    };

    /// <summary>
    /// The name a sound is listed under. It is shown to every user and quoted in the director's
    /// prompt, so it is one bounded line whatever the upload called itself.
    /// </summary>
    internal static string CleanDisplayName(string? displayName, string fileName)
        => UserText.Clean(displayName, MaxDisplayNameLength)
           ?? UserText.Clean(Path.GetFileNameWithoutExtension(fileName), MaxDisplayNameLength)
           ?? "Untitled sound";

    /// <summary>The uploader's comma-separated tags — a few short ones — plus the <c>custom</c> marker.</summary>
    internal static string[] CleanTags(string? tags)
    {
        var cleaned = (tags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => UserText.Clean(t, MaxTagLength))
            .OfType<string>()
            .Take(MaxUploadTags)
            .ToList();

        return cleaned.Count == 0
            ? ["custom", "user-upload"]
            : [.. cleaned.Append("custom").Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
