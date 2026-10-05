namespace PoRedoMedia.Shared.Enums;

// Shared with the client: the server's ScriptEntry and the wire ScriptEntryDto both carry it.
public enum VisualEffectType
{
    None,
    DeepFry,

    // Retired. SnapZoom cropped to half size and scaled back up, but the filter was added to the
    // base chain instead of the cue's time window, so it cropped the ENTIRE video. Nothing
    // produces or renders it now. The member itself must stay: entries are persisted as JSON via
    // JsonStringEnumConverter, so every already-stored script contains "SnapZoom" and removing the
    // member would throw JsonException when loading those sessions.
    SnapZoom,
    MotionBlur,
    Overlay
}
