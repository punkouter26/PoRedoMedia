#if ANDROID
using Android.Media;
using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile.Platforms.Android;

/// <summary>
/// Native Android audio player utilizing Android.Media.MediaPlayer.
/// Supports zero-dependency playback for generated Rap Roast beats and audio clips.
/// </summary>
public class AndroidAudioPlayer : IAudioPlayerService
{
    private MediaPlayer? _mediaPlayer;
    private string? _currentTempFile;

    public bool IsPlaying => _mediaPlayer?.IsPlaying == true;

    public event EventHandler? PlaybackStarted;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackError;

    public async Task PlayAsync(byte[] audioBytes, string contentType = "audio/mpeg")
    {
        Stop();

        try
        {
            var ext = contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3";
            _currentTempFile = Path.Combine(FileSystem.CacheDirectory, $"poredo_audio_playback{ext}");
            await File.WriteAllBytesAsync(_currentTempFile, audioBytes);

            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.SetDataSource(_currentTempFile);

            var audioAttributes = new AudioAttributes.Builder()
                .SetContentType(AudioContentType.Music)
                ?.SetUsage(AudioUsageKind.Media)
                ?.Build();

            if (audioAttributes != null)
            {
                _mediaPlayer.SetAudioAttributes(audioAttributes);
            }

            _mediaPlayer.Completion += (s, e) =>
            {
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            };

            _mediaPlayer.Error += (s, e) =>
            {
                PlaybackError?.Invoke(this, $"Android MediaPlayer error code: {e.What}");
            };

            _mediaPlayer.Prepare();
            _mediaPlayer.Start();
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, ex.Message);
        }
    }

    public void Pause()
    {
        try
        {
            if (_mediaPlayer?.IsPlaying == true)
            {
                _mediaPlayer.Pause();
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Best effort
        }
    }

    public void Resume()
    {
        try
        {
            if (_mediaPlayer != null && !_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Start();
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Best effort
        }
    }

    public void Stop()
    {
        try
        {
            if (_mediaPlayer != null)
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop();
                }
                _mediaPlayer.Reset();
                _mediaPlayer.Release();
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }
        }
        catch
        {
            // Best effort cleanup
        }
        finally
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_currentTempFile != null && File.Exists(_currentTempFile))
        {
            try { File.Delete(_currentTempFile); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
#endif
