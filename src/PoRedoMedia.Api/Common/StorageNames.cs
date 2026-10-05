namespace PoRedoMedia.Api.Common;

/// <summary>The one place table and container names are spelled.</summary>
public static class StorageNames
{
    public static class Tables
    {
        public const string Media = "Media";
    }

    public static class Containers
    {
        /// <summary>Everything a media item owns lives under <c>media/{mediaId}/</c>.</summary>
        public const string Media = "media";
    }
}
