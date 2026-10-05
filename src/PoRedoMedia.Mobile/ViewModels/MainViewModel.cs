using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using PoRedoMedia.Mobile.Models;
using PoRedoMedia.Mobile.Services;
using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using System.Collections.ObjectModel;
using ImageCaptureResult = PoRedoMedia.Mobile.Models.ImageCaptureResult;

namespace PoRedoMedia.Mobile.ViewModels;

public enum ResultMode
{
    None,
    Meme,
    Regenerate,
    RapRoast,
    Bulk,
    Video
}

public partial class MainViewModel : ObservableObject
{
    private readonly ICameraService _cameraService;
    private readonly MobileApiClient _apiClient;
    private readonly IShareService _shareService;
    private readonly IMobileSettingsService _settings;
    private readonly IRenderMonitorService _renderMonitor;
    private readonly IImageOptimizationService _optimizer;
    private readonly ISharedImageInbox _sharedInbox;
    private readonly IAudioPlayerService _audioPlayer;

    // Restored: aba465c overwrote this line while adding _activeAction below, which deleted the
    // generated CapturedImage property that twenty call sites still use. Nothing noticed, because
    // this machine had no Android SDK and nothing else builds the MAUI head.
    [ObservableProperty]
    private ImageCaptureResult? _capturedImage;

    [ObservableProperty]
    private ResultMode _activeAction = ResultMode.None;

    public bool IsMemeSelected => ActiveAction == ResultMode.Meme;
    public bool IsRegenerateSelected => ActiveAction == ResultMode.Regenerate;
    public bool IsRapRoastSelected => ActiveAction == ResultMode.RapRoast;
    public bool IsBulkSelected => ActiveAction == ResultMode.Bulk;
    public bool IsVideoSelected => ActiveAction == ResultMode.Video;

    partial void OnActiveActionChanged(ResultMode value)
    {
        OnPropertyChanged(nameof(IsMemeSelected));
        OnPropertyChanged(nameof(IsRegenerateSelected));
        OnPropertyChanged(nameof(IsRapRoastSelected));
        OnPropertyChanged(nameof(IsBulkSelected));
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(ActiveActionTitle));
    }

    // A result landing is a tap, a failure a long press — felt even with the phone on silent.
    partial void OnHasResultChanged(bool value) { if (value) Buzz(HapticFeedbackType.Click); }

    partial void OnHasErrorChanged(bool value) { if (value) Buzz(HapticFeedbackType.LongPress); }

    private static void Buzz(HapticFeedbackType type)
    {
        try { HapticFeedback.Default.Perform(type); }
        catch (FeatureNotSupportedException) { /* emulators and tablets without a motor */ }
    }

    public string ActiveActionTitle => ActiveAction switch
    {
        ResultMode.Meme => "Make Meme",
        ResultMode.Regenerate => "Reimagine Art",
        ResultMode.RapRoast => "Rap Roast",
        ResultMode.Bulk => "Bulk ×10 Styles",
        ResultMode.Video => "Veo Video Clip",
        _ => "Studio Processing"
    };

    [ObservableProperty]
    private bool _isRoastAudioPlaying;

    [ObservableProperty]
    private bool _hasRoastAudio;

    [ObservableProperty]
    private string _roastAudioStatus = "Beat Ready";

    [ObservableProperty]
    private Microsoft.Maui.Controls.ImageSource? _photoImageSource;

    [ObservableProperty]
    private string _photoSummary = string.Empty;

    [ObservableProperty]
    private bool _hasPhoto;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _processingStage = "Ready";

    [ObservableProperty]
    private double _processingProgress;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private ResultMode _currentResultMode = ResultMode.None;

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultSubtitle = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private Microsoft.Maui.Controls.ImageSource? _resultImageSource;

    [ObservableProperty]
    private byte[]? _resultImageBytes;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _selectedStyle = "Cyberpunk";

    [ObservableProperty]
    private string _videoPrompt =
        "The photo comes alive: the subject moves gently, cinematic lighting shifts, ambient sound.";

    /// <summary>Slots for the Bulk board — prefilled as pending, filled in as the stream lands.</summary>
    public ObservableCollection<BulkItemViewModel> BulkItems { get; } = [];

    [ObservableProperty]
    private bool _hasBulkResults;

    [ObservableProperty]
    private string _bulkSummary = string.Empty;

    [ObservableProperty]
    private bool _isBulkResult;

    [ObservableProperty]
    private bool _isVideoResult;

    [ObservableProperty]
    private bool _videoReady;

    [ObservableProperty]
    private string _galleryStatus = string.Empty;

    [ObservableProperty]
    private bool _hasGalleryStatus;

    private byte[]? _videoClipBytes;

    private string _videoContentType = "video/mp4";

    // The photo as the server knows it: uploaded on the first run, reused by every later one.
    private Guid? _sourceId;

    private string _resultContentType = "image/jpeg";

    public string ResultContentType
    {
        get => _resultContentType;
        set => SetProperty(ref _resultContentType, value);
    }

    /// <summary>
    /// Says where the caption came from. Shown under every meme, because "the AI wrote this on your
    /// phone" and "the AI wrote this in Azure" are different products and the user should not have
    /// to guess which one they got.
    /// </summary>
    [ObservableProperty]
    private string _captionSourceNote = string.Empty;

    public MainViewModel(
        ICameraService cameraService,
        MobileApiClient apiClient,
        IShareService shareService,
        IMobileSettingsService settings,
        IRenderMonitorService renderMonitor,
        IImageOptimizationService optimizer,
        ISharedImageInbox sharedInbox,
        IAudioPlayerService audioPlayer)
    {
        _cameraService = cameraService;
        _apiClient = apiClient;
        _shareService = shareService;
        _settings = settings;
        _renderMonitor = renderMonitor;
        _optimizer = optimizer;
        _sharedInbox = sharedInbox;
        _audioPlayer = audioPlayer;
        _selectedStyle = _settings.SelectedStyle;

        _audioPlayer.PlaybackStarted += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsRoastAudioPlaying = true;
                RoastAudioStatus = "Playing Beat 🎵";
            });
        };

        _audioPlayer.PlaybackEnded += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsRoastAudioPlaying = false;
                RoastAudioStatus = HasRoastAudio ? "Beat Ready · Tap to Play" : "Beat Ended";
            });
        };

        _audioPlayer.PlaybackError += (s, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsRoastAudioPlaying = false;
                RoastAudioStatus = $"Playback Error: {msg}";
            });
        };
    }

    /// <summary>
    /// Drains the native entry points on page show: an image shared from another app, or the
    /// camera-first launch the tile/widget requested. Both are invisible to a browser page.
    /// </summary>
    public async Task OnAppearingAsync()
    {
        if (_sharedInbox.TryTake(out var bytes, out var fileName, out var contentType))
        {
            try
            {
                ProcessingStage = "Optimizing shared photo…";
                await ReceivePhotoBytesAsync(fileName, contentType, bytes);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Shared image failed: {ex.Message}";
                HasError = true;
            }
            return;
        }

        if (SharedImageInbox.ConsumeCameraLaunch())
        {
            // Let the page finish appearing before the camera activity takes over the screen.
            await Task.Delay(300);
            await TakePhotoAsync();
        }
    }

    /// <summary>Bytes from the CameraX pro-capture page enter the studio here.</summary>
    public void ReceiveCapturedPhoto(ImageCaptureResult result) => SetCapturedPhoto(result);

    private async Task ReceivePhotoBytesAsync(string fileName, string contentType, byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        var optimized = await _optimizer.OptimizeAsync(
            stream, fileName, contentType, maxDimension: 1280, quality: 85);
        if (optimized is not null)
            SetCapturedPhoto(optimized);
    }

    [RelayCommand]
    public async Task TakePhotoAsync()
    {
        await CaptureAsync(
            stage => _cameraService.CapturePhotoAsync(stage),
            "Opening camera…",
            "Camera error");
    }

    [RelayCommand]
    public async Task PickPhotoAsync()
    {
        await CaptureAsync(
            stage => _cameraService.PickPhotoAsync(stage),
            "Selecting photo…",
            "Gallery error");
    }

    /// <summary>
    /// Shared camera/gallery flow. The progress bar only starts once the picker hands the
    /// photo back, so it tracks the on-device optimization the user actually waits through
    /// rather than the time they spent composing the shot.
    /// </summary>
    private async Task CaptureAsync(
        Func<IProgress<string>, Task<ImageCaptureResult?>> capture,
        string openingStage,
        string errorPrefix)
    {
        ClearError();
        ProcessingStage = openingStage;
        ProcessingProgress = 0;

        var creep = new CancellationTokenSource();
        var creepStarted = false;
        try
        {
            var stage = new Progress<string>(text =>
            {
                ProcessingStage = text;
                IsProcessing = true;
                if (!creepStarted)
                {
                    creepStarted = true;
                    _ = CreepProgressAsync(creep.Token);
                }
            });

            var result = await capture(stage);
            if (result != null)
            {
                ProcessingProgress = 1.0;
                SetCapturedPhoto(result);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{errorPrefix}: {ex.Message}";
            HasError = true;
        }
        finally
        {
            creep.Cancel();
            IsProcessing = false;
            ProcessingProgress = 0;
            if (!HasPhoto)
            {
                ProcessingStage = "Ready";
            }
        }
    }

    /// <summary>
    /// Eases the progress bar toward — but never to — completion while the optimizer runs.
    /// ImageSharp reports no real progress, so the curve is time-based against the ~7s a
    /// full-resolution phone photo takes; the caller snaps it to 1.0 on success.
    /// </summary>
    private async Task CreepProgressAsync(CancellationToken ct)
    {
        const double ceiling = 0.92;
        const double expectedSeconds = 7.0;
        var elapsed = 0.0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                elapsed += 0.1;
                ProcessingProgress = ceiling * (1 - Math.Exp(-elapsed / (expectedSeconds / 2.5)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetCapturedPhoto(ImageCaptureResult photo)
    {
        CapturedImage = photo;
        _sourceId = null;
        PhotoImageSource = ImageSource.FromStream(() => new MemoryStream(photo.Bytes));
        PhotoSummary = photo.FormattedSummary;
        HasPhoto = true;
        HasResult = false;
        ResultImageSource = null;
        ResultImageBytes = null;
        ResultText = string.Empty;
        CaptionSourceNote = string.Empty;
        CurrentResultMode = ResultMode.None;
        ResetTransientResultState();
    }

    /// <summary>
    /// Every action is the same three moves: upload the photo once, run one function on it, and
    /// fetch what the run saved. The server keeps the results, so they are in the gallery already.
    /// </summary>
    private async Task<List<MediaDto>> RunAsync(MediaFunction function, Dictionary<string, string>? options = null)
    {
        if (!await _apiClient.EnsureAuthenticatedAsync(_settings.GuestId))
            throw new InvalidOperationException(
                "Sign-in failed. For now the phone app can only sign in to a Development or Test server.");

        if (_sourceId is null)
        {
            ProcessingStage = "Uploading photo…";
            _sourceId = (await _apiClient.UploadAsync(CapturedImage!)).Id;
        }

        var label = FunctionStack.Label(function);
        var startedAt = DateTime.UtcNow;
        var run = await _apiClient.RunAsync(_sourceId.Value, [function], options, update =>
        {
            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
            ProcessingStage = update.Status == RunStatus.Queued
                ? "Waiting in the queue…"
                : update.OutputIds.Length > 0 ? $"{label}… {update.OutputIds.Length} done" : $"{label}… {elapsed}s";
            // No real percentage comes back, so the bar eases toward the end and never reaches it.
            ProcessingProgress = 0.95 - 0.65 * Math.Exp(-elapsed / 30.0);
        });

        var gallery = await _apiClient.ListGalleryAsync();
        var outputs = run.OutputIds.Select(id => gallery.FirstOrDefault(m => m.Id == id)).OfType<MediaDto>().ToList();
        if (run.Status == RunStatus.Failed && outputs.Count == 0)
            throw new InvalidOperationException(run.Error ?? "The run failed.");

        // Everything the server did differently from what was asked is said, never hidden.
        CaptionSourceNote = string.Join(" ", run.Notes.Append(run.Error).Where(n => !string.IsNullOrWhiteSpace(n)));
        GalleryStatus = outputs.Count == 0 ? string.Empty : "Saved to your PoRedo gallery ✓";
        HasGalleryStatus = outputs.Count > 0;
        return outputs;
    }

    private async Task ShowImageAsync(MediaDto item)
    {
        var bytes = await _apiClient.GetBytesAsync(item.Url);
        ResultImageBytes = bytes;
        ResultContentType = item.ContentType;
        ResultImageSource = ImageSource.FromStream(() => new MemoryStream(bytes));
    }

    private static MediaDto First(List<MediaDto> outputs, MediaKind kind) =>
        outputs.FirstOrDefault(o => o.Kind == kind) ?? throw new InvalidOperationException("The run finished but made nothing.");

    [RelayCommand]
    public async Task ProcessMemeAsync()
    {
        if (CapturedImage == null) return;
        await ExecuteProcessingAsync("Meme Magic", async () =>
        {
            var meme = First(await RunAsync(MediaFunction.MemeCaption), MediaKind.Image);
            await ShowImageAsync(meme);

            ResultTitle = "🎭 AI Meme Created";
            ResultSubtitle = string.Empty;
            ResultText = meme.Text ?? string.Empty;
            CurrentResultMode = ResultMode.Meme;
            HasResult = true;
        });
    }

    [RelayCommand]
    public async Task ProcessRegenerateAsync()
    {
        if (CapturedImage == null) return;
        await ExecuteProcessingAsync("AI Art Transformation", async () =>
        {
            var options = new Dictionary<string, string> { [RunOptions.RestylePrompt] = SelectedStyle };
            await ShowImageAsync(First(await RunAsync(MediaFunction.Restyle, options), MediaKind.Image));

            ResultTitle = $"✨ Reimagined ({SelectedStyle})";
            ResultSubtitle = string.Empty;
            ResultText = string.Empty;
            CurrentResultMode = ResultMode.Regenerate;
            HasResult = true;
        });
    }

    [RelayCommand]
    public async Task ProcessRapRoastAsync()
    {
        if (CapturedImage == null) return;
        await ExecuteProcessingAsync("Rap Roast", async () =>
        {
            // No audio is not a failure: when the music provider declines, the run still completes
            // and its notes carry the lyrics and the reason.
            var roast = (await RunAsync(MediaFunction.RapRoast)).FirstOrDefault(o => o.Kind == MediaKind.Audio);

            ResultTitle = "🎤 Savage Rap Roast";
            ResultSubtitle = string.Empty;
            ResultText = roast?.Text ?? string.Empty;
            ResultImageSource = PhotoImageSource;
            ResultImageBytes = CapturedImage.Bytes;
            RoastAudioBytes = roast is null ? null : await _apiClient.GetBytesAsync(roast.Url);
            RoastAudioContentType = roast?.ContentType ?? "audio/mpeg";
            CurrentResultMode = ResultMode.RapRoast;
            HasResult = true;
        });
    }

    public byte[]? RoastAudioBytes { get; private set; }

    public string RoastAudioContentType { get; private set; } = "audio/mpeg";

    /// <summary>Bulk ×10: the server draws the user's ten saved styles and the board fills in.</summary>
    [RelayCommand]
    public async Task ProcessBulkAsync()
    {
        if (CapturedImage == null) return;
        await ExecuteProcessingAsync("Bulk Art Studio", async () =>
        {
            BulkItems.Clear();
            HasBulkResults = true;
            IsBulkResult = true;

            // ponytail: the board fills when the run ends, not slot by slot. Download each new
            // output inside the poll callback if watching them land turns out to matter.
            var outputs = await RunAsync(MediaFunction.BulkStyles);
            foreach (var output in outputs.Where(o => o.Kind == MediaKind.Image))
            {
                var bytes = await _apiClient.GetBytesAsync(output.Url);
                BulkItems.Add(new BulkItemViewModel(BulkItems.Count)
                {
                    Bytes = bytes,
                    ContentType = output.ContentType,
                    Image = ImageSource.FromStream(() => new MemoryStream(bytes)),
                    IsFilled = true,
                    StatusText = output.Title,
                });
            }

            BulkSummary = $"{BulkItems.Count}/{BulkPrompts.Count} variations generated";
            ResultTitle = "🎨 Bulk Art Studio";
            ResultSubtitle = BulkSummary;
            ResultText = string.Empty;
            CurrentResultMode = ResultMode.Bulk;
            HasResult = true;
        });
    }

    /// <summary>Photo and prompt in, an 8-second clip out. The server waits on Veo; the phone polls the run.</summary>
    [RelayCommand]
    public async Task ProcessVideoAsync()
    {
        if (CapturedImage == null) return;
        var prompt = string.IsNullOrWhiteSpace(VideoPrompt) ? string.Empty : VideoPrompt.Trim();

        await ExecuteProcessingAsync("Video Render", async () =>
        {
            if (prompt.Length is < 3 or > 1200)
                throw new InvalidOperationException("Video prompt must be between 3 and 1200 characters.");

            // Foreground service + notification: without it Android freezes the app the moment
            // the user locks the phone, and a 1-5 minute render dies silently in the background.
            await _renderMonitor.StartAsync("Rendering your PoRedo clip…");
            var succeeded = false;
            try
            {
                var clip = First(
                    await RunAsync(MediaFunction.PhotoToVideo, new() { [RunOptions.VideoPrompt] = prompt }), MediaKind.Video);
                _videoClipBytes = await _apiClient.GetBytesAsync(clip.Url);
                _videoContentType = clip.ContentType;
                succeeded = true;
            }
            finally
            {
                await _renderMonitor.CompleteAsync(
                    succeeded ? "Your 8-second clip is ready 🎬" : "Clip render failed — open PoRedo for details.",
                    succeeded);
            }

            VideoReady = true;
            ResultTitle = "🎬 Video Ready";
            ResultSubtitle = prompt;
            ResultText = "Your 8-second clip with sound is ready. Open it to watch, or share it.";
            ResultImageSource = PhotoImageSource;
            ResultImageBytes = CapturedImage.Bytes;
            CurrentResultMode = ResultMode.Video;
            IsVideoResult = true;
            HasResult = true;
        });
    }

    /// <summary>
    /// Writes the finished clip to the app cache and hands it to the platform player.
    /// </summary>
    [RelayCommand]
    public async Task OpenVideoClipAsync()
    {
        if (_videoClipBytes is null) return;
        var path = Path.Combine(FileSystem.CacheDirectory, $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4");
        await File.WriteAllBytesAsync(path, _videoClipBytes);
        await Launcher.Default.OpenAsync(new OpenFileRequest
        {
            Title = "PoRedo clip",
            File = new ReadOnlyFile(path, _videoContentType)
        });
    }

    private void ResetTransientResultState()
    {
        HasBulkResults = false;
        IsBulkResult = false;
        IsVideoResult = false;
        VideoReady = false;
        BulkItems.Clear();
        BulkSummary = string.Empty;
        GalleryStatus = string.Empty;
        HasGalleryStatus = false;
        _videoClipBytes = null;
        RoastAudioBytes = null;
    }

    [RelayCommand]
    public async Task ShareResultAsync()
    {
        if (CurrentResultMode == ResultMode.Video && _videoClipBytes is not null)
        {
            await _shareService.ShareFileAsync(
                _videoClipBytes, $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4", ResultTitle);
            return;
        }

        if (CurrentResultMode == ResultMode.RapRoast && RoastAudioBytes is not null)
        {
            await _shareService.ShareFileAsync(
                RoastAudioBytes, $"poredo_roast_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp3", ResultTitle);
            return;
        }

        if (ResultImageBytes != null && CurrentResultMode != ResultMode.RapRoast)
        {
            var fileName = $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
            await _shareService.ShareImageAsync(ResultImageBytes, fileName, ResultTitle);
        }
        else if (!string.IsNullOrEmpty(ResultText))
        {
            await _shareService.ShareTextAsync(ResultText, ResultTitle);
        }
    }

    [RelayCommand]
    public async Task SaveResultAsync()
    {
        var meta = new MediaMetadata(
            Title: ResultTitle,
            PromptOrDescription: !string.IsNullOrWhiteSpace(ResultText) ? ResultText : PhotoSummary,
            ModelOrStyle: CurrentResultMode == ResultMode.Regenerate ? SelectedStyle : CurrentResultMode.ToString(),
            CreatedAt: DateTimeOffset.UtcNow);

        if (IsVideoResult && _videoClipBytes != null && _videoClipBytes.Length > 0)
        {
            var fileName = $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4";
            var path = await _shareService.SaveToDeviceAsync(_videoClipBytes, fileName, _videoContentType, meta);
            if (path != null)
            {
                ProcessingStage = $"Saved video to {path}!";
            }
        }
        else if (ResultImageBytes != null && ResultImageBytes.Length > 0)
        {
            var fileName = $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
            var path = await _shareService.SaveToDeviceAsync(ResultImageBytes, fileName, ResultContentType, meta);
            if (path != null)
            {
                ProcessingStage = $"Saved image to {path}!";
            }
        }
    }

    [RelayCommand]
    public void Reset()
    {
        CapturedImage = null;
        _sourceId = null;
        PhotoImageSource = null;
        PhotoSummary = string.Empty;
        HasPhoto = false;
        HasResult = false;
        ResultImageSource = null;
        ResultImageBytes = null;
        ResultText = string.Empty;
        CaptionSourceNote = string.Empty;
        CurrentResultMode = ResultMode.None;
        ResetTransientResultState();
        ClearError();
    }

    private async Task ExecuteProcessingAsync(string operationName, Func<Task> action)
    {
        ClearError();
        IsProcessing = true;
        ProcessingProgress = 0.1;
        ProcessingStage = $"Starting {operationName}…";

        try
        {
            await action();
            ProcessingProgress = 1.0;
            ProcessingStage = "Done!";

            if (_settings.AutoSaveToGallery)
            {
                await AutoSaveResultToDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
            ProcessingStage = "Error occurred";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task AutoSaveResultToDeviceAsync()
    {
        try
        {
            var meta = new MediaMetadata(
                Title: ResultTitle,
                PromptOrDescription: !string.IsNullOrWhiteSpace(ResultText) ? ResultText : PhotoSummary,
                ModelOrStyle: CurrentResultMode == ResultMode.Regenerate ? SelectedStyle : CurrentResultMode.ToString(),
                CreatedAt: DateTimeOffset.UtcNow);

            if (IsVideoResult && _videoClipBytes != null && _videoClipBytes.Length > 0)
            {
                var fileName = $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4";
                var path = await _shareService.SaveToDeviceAsync(_videoClipBytes, fileName, _videoContentType, meta);
                if (path != null)
                {
                    ProcessingStage = "Done! (Saved to device gallery)";
                }
            }
            else if (ResultImageBytes != null && ResultImageBytes.Length > 0)
            {
                var fileName = $"poredo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
                var path = await _shareService.SaveToDeviceAsync(ResultImageBytes, fileName, ResultContentType, meta);
                if (path != null)
                {
                    ProcessingStage = "Done! (Saved to device gallery)";
                }
            }
        }
        catch
        {
            // Auto-save is best-effort and must not fail the generation result
        }
    }

    private void ClearError()
    {
        ErrorMessage = null;
        HasError = false;
    }
}
