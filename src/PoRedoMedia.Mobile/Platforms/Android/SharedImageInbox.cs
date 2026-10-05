namespace PoRedoMedia.Mobile;

/// <summary>
/// Holds images handed over via share intents until the studio UI drains them.
/// </summary>
public static class SharedImageInbox
{
    private static byte[]? _pending;
    private static string _fileName = "shared.jpg";

    public static void Push(byte[] bytes, string fileName)
    {
        // Latest share wins — the user taps "share" once and expects to see that photo.
        _pending = bytes;
        _fileName = fileName;
    }

    public static (byte[] Bytes, string FileName)? Consume()
    {
        if (_pending is null)
            return null;

        var result = (_pending, _fileName);
        _pending = null;
        return result;
    }

    /// <summary>Flag set by the quick-settings tile; drained on the next OnAppearing.</summary>
    private static volatile bool _cameraLaunchRequested;

    /// <summary>Called from the native entry points when they want a camera-first launch.</summary>
    public static void RequestCameraLaunch() => _cameraLaunchRequested = true;

    /// <summary>True once if a camera launch was requested since the last drain.</summary>
    public static bool ConsumeCameraLaunch()
    {
        if (!_cameraLaunchRequested)
            return false;
        _cameraLaunchRequested = false;
        return true;
    }
}
