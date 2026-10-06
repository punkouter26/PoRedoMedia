using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Features.Sounds;

public static class SoundsEndpoints
{
    /// <summary>Upload limits. The size matches what the client checks before it sends the file.</summary>
    internal const long MaxUploadBytes = 15 * 1024 * 1024;
    internal const int MaxUploadsPerUser = 25;
    internal const int MaxDisplayNameLength = 60;
    internal const int MaxUploadTags = 6;
    internal const int MaxTagLength = 24;

    public static IEndpointRouteBuilder MapSounds(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/sounds").RequireAntiforgeryValidation();
        group.MapGet("/", ListAsync);
        group.MapPut("/favorites/{soundId:guid}", (SoundId soundId, ClaimsPrincipal user, ISoundFavoritesRepository favorites, CancellationToken ct) =>
            SetFavoriteAsync(soundId, true, user, favorites, ct));
        group.MapDelete("/favorites/{soundId:guid}", (SoundId soundId, ClaimsPrincipal user, ISoundFavoritesRepository favorites, CancellationToken ct) =>
            SetFavoriteAsync(soundId, false, user, favorites, ct));
        group.MapPost("/upload", UploadAsync)
            // The built-in form check is off because the group's filter already validates the
            // token; running both rejects even a correct one.
            .DisableAntiforgery()
            .RequireRateLimiting(UploadRateLimit.Policy);
        group.MapGet("/{soundId:guid}/stream", StreamAsync);
        return routes;
    }

    /// <summary>
    /// The shared library plus the caller's uploads: starred first, then by name. It is a few
    /// hundred small rows, so the page takes all of it and filters locally.
    /// </summary>
    private static async Task<Ok<List<SoundAssetDto>>> ListAsync(
        ClaimsPrincipal user, ISoundAssetRepository repository, ISoundFavoritesRepository favoritesRepository, CancellationToken ct)
    {
        var owner = UserId.From(user);
        var favorites = await favoritesRepository.GetAsync(owner, ct);
        return TypedResults.Ok((await repository.LoadAllAsync(ct)).VisibleTo(owner)
            .OrderByDescending(s => favorites.Contains(s.SoundId))
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(s => ToDto(s, favorites.Contains(s.SoundId)))
            .ToList());
    }

    private static async Task<NoContent> SetFavoriteAsync(
        SoundId soundId, bool favorite, ClaimsPrincipal user, ISoundFavoritesRepository favorites, CancellationToken ct)
    {
        await favorites.SetAsync(UserId.From(user), soundId, favorite, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Created<SoundAssetDto>, ProblemHttpResult>> UploadAsync(
        IFormFile file, string? displayName, string? tags, ClaimsPrincipal user, ISoundAssetRepository repository,
        BlobStorageService blobs, SoundTagger tagger, CancellationToken ct)
    {
        var owner = UserId.From(user);
        var library = await repository.LoadAllAsync(ct);
        if (library.Count(s => s.Owner == owner.Key) >= MaxUploadsPerUser)
            return Refused($"You can keep up to {MaxUploadsPerUser} uploaded sounds.");
        if (file is null || file.Length == 0)
            return Refused("No audio file provided.");
        if (file.Length > MaxUploadBytes)
            return Refused($"Sounds can be at most {MaxUploadBytes / (1024 * 1024)} MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ContentTypeFor(ext) is not { } contentType)
            return Refused("Only .mp3, .wav, and .ogg files are supported.");

        // Buffered (uploads are small clips) so the bytes can be checked and measured before storing.
        using var buffer = new MemoryStream();
        await using (var stream = file.OpenReadStream())
            await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (!LooksLikeAudio(bytes))
            return Refused("That file is not readable audio.");

        var soundId = SoundId.New();
        var blobPath = $"{StorageNames.Containers.Sounds}/{soundId}{ext}";
        var durationMs = AudioDuration.EstimateMs(bytes, file.FileName);

        buffer.Position = 0;
        // The type this app chose for the extension, never the one the browser sent.
        await blobs.UploadAsync(blobPath, buffer, contentType, ct);

        var asset = new SoundAsset
        {
            SoundId = soundId,
            DisplayName = CleanDisplayName(displayName, file.FileName),
            DurationMs = durationMs,
            ActionVectorTags = CleanTags(tags),
            BlobUrl = blobPath,
            Priority = false,
            UseCase = "custom-upload",
            Owner = owner.Key,
        };

        await tagger.TagAsync(asset, SoundVocabulary.Tags(library.VisibleTo(owner)), ct);
        await repository.AddSoundAsync(asset, ct);
        return TypedResults.Created($"/api/sounds/{soundId}/stream", ToDto(asset, false));
    }

    private static async Task<IResult> StreamAsync(
        SoundId soundId, HttpContext http, ISoundAssetRepository repository, BlobDelivery delivery, CancellationToken ct) =>
        (await repository.LoadAllAsync(ct)).VisibleTo(UserId.From(http.User)).FirstOrDefault(s => s.SoundId == soundId) is { } sound
            ? await delivery.ServeAsync(http, sound.BlobPath, ct: ct)
            : Results.NotFound();

    private static string? ContentTypeFor(string extension) => extension switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        _ => null,
    };

    /// <summary>The first bytes of an MP3 (ID3 tag or frame sync), a WAV or an OGG. The extension alone proves nothing.</summary>
    internal static bool LooksLikeAudio(ReadOnlySpan<byte> d) =>
        d.Length >= 12
        && (d[..3].SequenceEqual("ID3"u8)
            || (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0)
            || (d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WAVE"u8))
            || d[..4].SequenceEqual("OggS"u8));

    private static SoundAssetDto ToDto(SoundAsset s, bool favorite) => new()
    {
        SoundId = s.SoundId.Value,
        DisplayName = s.DisplayName,
        DurationMs = s.DurationMs,
        ActionVectorTags = s.ActionVectorTags,
        Attribution = s.Attribution,
        IsFavorite = favorite,
    };

    /// <summary>
    /// The name a sound is listed under. It is quoted in the director's prompt, so it is one
    /// bounded line whatever the upload called itself.
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

    private static ProblemHttpResult Refused(string reason) =>
        TypedResults.Problem(detail: reason, statusCode: StatusCodes.Status400BadRequest);
}
