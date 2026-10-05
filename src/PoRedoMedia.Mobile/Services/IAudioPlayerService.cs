namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Native audio playback service for playing voice notes, music clips, and rap roast tracks.
/// </summary>
public interface IAudioPlayerService : IDisposable
{
    /// <summary>Whether audio is actively playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Fires when playback starts.</summary>
    event EventHandler? PlaybackStarted;

    /// <summary>Fires when playback completes naturally or stops.</summary>
    event EventHandler? PlaybackEnded;

    /// <summary>Fires when an error occurs during playback.</summary>
    event EventHandler<string>? PlaybackError;

    /// <summary>
    /// Plays the provided audio bytes (e.g. MP3 / WAV).
    /// </summary>
    Task PlayAsync(byte[] audioBytes, string contentType = "audio/mpeg");

    /// <summary>Pauses the active audio track.</summary>
    void Pause();

    /// <summary>Resumes audio playback if paused.</summary>
    void Resume();

    /// <summary>Stops playback and releases media resources.</summary>
    void Stop();
}
