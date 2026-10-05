namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Drains photos handed to the app through Android share intents. Android-only in nature;
/// <see cref="NullSharedImageInbox"/> stands in elsewhere.
/// </summary>
public interface ISharedImageInbox
{
    bool TryTake(out byte[] bytes, out string fileName, out string contentType);
}

/// <summary>The no-op inbox for platforms without a share-target surface.</summary>
public sealed class NullSharedImageInbox : ISharedImageInbox
{
    public bool TryTake(out byte[] bytes, out string fileName, out string contentType)
    {
        bytes = [];
        fileName = string.Empty;
        contentType = string.Empty;
        return false;
    }
}
