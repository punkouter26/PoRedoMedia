#if ANDROID
using Android.Content;
using Android.OS;
using Android.Provider;
using PoRedoMedia.Mobile.Models;

namespace PoRedoMedia.Mobile.Platforms.Android;

/// <summary>
/// Writes AI-generated images and videos into Android's public Scoped Storage MediaStore
/// (Pictures/PoRedoMedia and Movies/PoRedoMedia), with embedded EXIF metadata tags.
/// </summary>
public static class AndroidMediaStore
{
    public static async Task<string?> SaveMediaAsync(
        byte[] mediaBytes,
        string fileName,
        string contentType,
        MediaMetadata? metadata)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var resolver = context.ContentResolver;
            if (resolver is null) return null;

            bool isVideo = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            var collectionUri = isVideo
                ? MediaStore.Video.Media.ExternalContentUri
                : MediaStore.Images.Media.ExternalContentUri;

            if (collectionUri is null) return null;

            var folderName = isVideo
                ? global::Android.OS.Environment.DirectoryMovies + "/PoRedoMedia"
                : global::Android.OS.Environment.DirectoryPictures + "/PoRedoMedia";

            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
            values.Put(MediaStore.IMediaColumns.MimeType, contentType);
            values.Put(MediaStore.IMediaColumns.Title, metadata?.Title ?? Path.GetFileNameWithoutExtension(fileName));

            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            values.Put(MediaStore.IMediaColumns.DateAdded, nowSeconds);
            values.Put(MediaStore.IMediaColumns.DateModified, nowSeconds);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                values.Put(MediaStore.IMediaColumns.RelativePath, folderName);
                values.Put(MediaStore.IMediaColumns.IsPending, 1);
            }

            var itemUri = resolver.Insert(collectionUri, values);
            if (itemUri is null) return null;

            // Stream content into the newly created MediaStore URI
            using (var outputStream = resolver.OpenOutputStream(itemUri))
            {
                if (outputStream is null) return null;
                await outputStream.WriteAsync(mediaBytes, 0, mediaBytes.Length);
                await outputStream.FlushAsync();
            }

            // Write EXIF tags for JPEG images via FileDescriptor
            if (!isVideo && (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                             fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
            {
                InjectExifMetadata(resolver, itemUri, metadata);
            }

            // Publish: clear IS_PENDING so the media appears in gallery & photo pickers immediately
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                values.Clear();
                values.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(itemUri, values, null, null);
            }

            return $"{folderName}/{fileName}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void InjectExifMetadata(ContentResolver resolver, global::Android.Net.Uri itemUri, MediaMetadata? metadata)
    {
        try
        {
            using var pfd = resolver.OpenFileDescriptor(itemUri, "rw");
            if (pfd?.FileDescriptor is null) return;

            var exif = new global::Android.Media.ExifInterface(pfd.FileDescriptor);

            if (!string.IsNullOrWhiteSpace(metadata?.PromptOrDescription))
            {
                var comment = metadata.PromptOrDescription;
                if (!string.IsNullOrWhiteSpace(metadata.ModelOrStyle))
                {
                    comment = $"{comment} [Model/Style: {metadata.ModelOrStyle}]";
                }

                exif.SetAttribute(global::Android.Media.ExifInterface.TagUserComment, comment);
                exif.SetAttribute(global::Android.Media.ExifInterface.TagImageDescription, metadata.PromptOrDescription);
            }

            if (!string.IsNullOrWhiteSpace(metadata?.ModelOrStyle))
            {
                exif.SetAttribute(global::Android.Media.ExifInterface.TagModel, metadata.ModelOrStyle);
            }

            exif.SetAttribute(global::Android.Media.ExifInterface.TagSoftware, metadata?.Author ?? "PoRedoMedia AI Studio");
            exif.SetAttribute(global::Android.Media.ExifInterface.TagArtist, metadata?.Author ?? "PoRedoMedia AI Studio");
            exif.SetAttribute(global::Android.Media.ExifInterface.TagDatetime, DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss"));

            exif.SaveAttributes();
        }
        catch
        {
            // Non-fatal: if EXIF injection fails (e.g. driver limitation), the image is still saved
        }
    }
}
#endif

