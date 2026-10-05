namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Fallback audio player for non-Android platforms or test environments.
/// </summary>
public class NullAudioPlayer : IAudioPlayerService
{
    public bool IsPlaying => false;

    // Never raised — nothing plays — so the handlers are simply not kept. Plain field-like events
    // here tripped CS0067 ("never used"), which TreatWarningsAsErrors turns into a build break.
    public event EventHandler? PlaybackStarted { add { } remove { } }
    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackError { add { } remove { } }

    public Task PlayAsync(byte[] audioBytes, string contentType = "audio/mpeg")
    {
        return Task.CompletedTask;
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Stop()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
