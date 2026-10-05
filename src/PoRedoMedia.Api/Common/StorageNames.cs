namespace PoRedoMedia.Api.Common;

/// <summary>The one place table and container names are spelled.</summary>
public static class StorageNames
{
    public static class Tables
    {
        public const string Media = "Media";
        public const string Runs = "Runs";
        public const string BulkPrompts = "BulkPrompts";

        /// <summary>Per-user daily run counters, partitioned by UTC day.</summary>
        public const string RenderQuotas = "RenderQuotas";
    }

    public static class Containers
    {
        /// <summary>Everything a media item owns lives under <c>media/{mediaId}/</c>.</summary>
        public const string Media = "media";
    }
}
