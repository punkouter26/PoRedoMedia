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
