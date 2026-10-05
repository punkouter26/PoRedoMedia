namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Native MAUI implementation of sharing and saving to device filesystem.
/// </summary>
public class MauiShareService : IShareService
{
    public async Task ShareImageAsync(byte[] imageBytes, string fileName, string title = "PoRedo Image")
    {
        var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(tempPath, imageBytes);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(tempPath)
        });
    }

    public async Task ShareFileAsync(byte[] fileBytes, string fileName, string title = "PoRedo Clip")
    {
        var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(tempPath, fileBytes);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(tempPath, "video/mp4")
        });
    }

    public async Task ShareTextAsync(string text, string title = "PoRedo Roast")
    {
        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = title,
            Text = text
        });
    }

    public async Task<string?> SaveToDeviceAsync(
        byte[] mediaBytes,
        string fileName,
        string contentType = "image/jpeg",
        Models.MediaMetadata? metadata = null)
    {
        try
        {
#if ANDROID
            var savedPath = await Platforms.Android.AndroidMediaStore.SaveMediaAsync(
                mediaBytes, fileName, contentType, metadata);
            if (savedPath is not null)
                return savedPath;
#endif
            var folder = FileSystem.AppDataDirectory;
            var targetPath = Path.Combine(folder, fileName);
            await File.WriteAllBytesAsync(targetPath, mediaBytes);
            return targetPath;
        }
        catch
        {
            return null;
        }
    }
}

