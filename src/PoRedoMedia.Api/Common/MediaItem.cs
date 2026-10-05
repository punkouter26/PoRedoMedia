using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Common;

public enum MediaStatus
{
    /// <summary>A place has been reserved and an upload link issued; the bytes are not confirmed yet.</summary>
    Uploading,
    Ready,
}

/// <summary>One stored image, video or audio item: an upload, or the output of a function.</summary>
public sealed record MediaItem
{
    public required UserId Owner { get; init; }
    public required MediaId Id { get; init; }
    public required MediaKind Kind { get; init; }
    public required MediaStatus Status { get; init; }

    /// <summary>"Upload", or the name of the function that produced it.</summary>
    public required string Origin { get; init; }

    /// <summary>The item this one was made from, when it is a function's output.</summary>
    public MediaId? ParentId { get; init; }

    public required string Title { get; init; }
    public required string ContentType { get; init; }

    /// <summary>File extension of the stored source, with its dot.</summary>
    public required string Extension { get; init; }

    public long SizeBytes { get; init; }
    public double? DurationSeconds { get; init; }
    public bool Pinned { get; init; }
    public string? ShareToken { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public string SourcePath => MediaBlobPaths.Source(Id, Extension);
}

/// <summary>
/// Blob paths for one media item. Everything it owns lives under <see cref="Prefix"/>, so one
/// prefix delete removes it whole. Keep new per-item files under the same prefix.
/// </summary>
public static class MediaBlobPaths
{
    public static string Prefix(MediaId id) => $"{StorageNames.Containers.Media}/{id}/";

    public static string Source(MediaId id, string extension) => $"{Prefix(id)}source{extension}";

    public static string Thumbnail(MediaId id) => $"{Prefix(id)}thumb.jpg";
}
