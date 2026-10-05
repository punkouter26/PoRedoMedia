using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Shared.Models;

/// <summary>One item in a user's gallery. <c>Url</c> and <c>ThumbUrl</c> are app routes that redirect to storage.</summary>
public sealed record MediaDto(
    Guid Id,
    MediaKind Kind,
    string Title,
    string Origin,
    Guid? ParentId,
    string ContentType,
    long SizeBytes,
    double? DurationSeconds,
    bool Pinned,
    bool Shared,
    DateTimeOffset CreatedAt,
    string Url,
    string ThumbUrl);

public sealed record UploadRequest(string FileName, long SizeBytes);

/// <summary>Where to PUT the file. The link is valid for one blob and expires.</summary>
public sealed record UploadTicket(Guid Id, string UploadUrl, DateTimeOffset ExpiresAt);

public sealed record MediaUpdateRequest(string? Title, bool? Pinned);
