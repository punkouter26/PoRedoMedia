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
    string ThumbUrl,
    string? Text = null,
    string? Detail = null);

public sealed record UploadRequest(string FileName, long SizeBytes);

/// <summary>Where to PUT the file. The link is valid for one blob and expires.</summary>
public sealed record UploadTicket(Guid Id, string UploadUrl, DateTimeOffset ExpiresAt);

public sealed record MediaUpdateRequest(string? Title, bool? Pinned);

/// <summary>Frames sampled from a video in the browser, as image data URLs, with each frame's time in seconds.</summary>
public sealed record FrameUploadRequest(List<string> Frames, List<double>? Timestamps = null);

public sealed record FramesResult(int FramesStored, int MomentsFound);

/// <summary>The public address of a shared item.</summary>
/// <param name="Feed">True when the item is also listed on the feed every signed-in user sees.</param>
public sealed record ShareLinkDto(string Url, bool Feed = false);

/// <summary>One shared item on the feed. Its picture and file are read through its share link.</summary>
public sealed record FeedItemDto(
    string Token,
    string Title,
    MediaKind Kind,
    string Author,
    DateTimeOffset SharedAt,
    int Views,
    int Remixes,
    bool Mine,
    bool CanRemix);
