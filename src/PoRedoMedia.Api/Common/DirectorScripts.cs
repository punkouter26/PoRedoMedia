namespace PoRedoMedia.Api.Common;

/// <summary>Limits on what the director may put in a video.</summary>
public static class DirectorScripts
{
    /// <summary>Sticker overlays the renderer ships (<c>Assets/overlays/{id}.png</c>).</summary>
    public static readonly string[] Stickers = ["deal-with-it", "laser-eyes", "thug-life", "red-circle", "clown-wig", "explosion"];

    /// <summary>Most cues one video gets: every cue is one more ffmpeg input and filter pass.</summary>
    public const int MaxCues = 40;

    /// <summary>Longest caption burned into the video; anything longer runs off the frame anyway.</summary>
    public const int MaxCaptionLength = 120;
}
