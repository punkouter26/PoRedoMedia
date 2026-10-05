using System.Diagnostics;
using Azure;
using Azure.AI.Vision.ImageAnalysis;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>
/// Azure Image Analysis 4.0 implementation of <see cref="ISceneDetailProvider"/> — OCR
/// (<c>Read</c>), region-level <c>DenseCaptions</c>, <c>Objects</c>, and <c>People</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Regional availability.</b> <c>DenseCaptions</c> carries the same regional restriction as
/// <c>Caption</c>: outside a supporting region the request fails with a 400. <c>Read</c>,
/// <c>Objects</c>, and <c>People</c> are broadly available. Rather than guess which region the
/// configured resource is in, this asks for everything once, and on a 400 naming an unsupported
/// feature it retries with only the universally-available set — then remembers the answer for the
/// process lifetime so the probe costs one wasted call, not one per image.
/// </para>
/// </remarks>
public sealed class AzureSceneDetailService : ISceneDetailProvider, ICombinedVisionAnalyzer
{
    /// <summary>Features available everywhere.</summary>
    private const VisualFeatures BaseFeatures =
        VisualFeatures.Read | VisualFeatures.Objects | VisualFeatures.People;

    /// <summary>Everything, including the region-limited region captions.</summary>
    private const VisualFeatures RichFeatures = BaseFeatures | VisualFeatures.DenseCaptions;

    /// <summary>
    /// The detail features PLUS the two <see cref="IVisionService"/> needs. Requesting all of them
    /// at once is what lets one call answer both interfaces — see <see cref="ICombinedVisionAnalyzer"/>
    /// for why Rap Roast used to pay for two.
    /// </summary>
    private const VisualFeatures CombinedFeatures = RichFeatures | VisualFeatures.Caption | VisualFeatures.Tags;

    /// <summary>Combined, minus the two region-limited features.</summary>
    private const VisualFeatures CombinedBaseFeatures = BaseFeatures | VisualFeatures.Tags;

    private readonly ILogger<AzureSceneDetailService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ImageAnalysisClient? _client;
    private readonly AzureKeyCredential? _credential;
    private readonly float _minConfidence;

    /// <summary>
    /// Null until the first call determines whether this region supports DenseCaptions; then fixed.
    /// </summary>
    private VisualFeatures? _supportedFeatures;

    private string? CurrentKey =>
        _configuration[ConfigKeys.ComputerVisionApiKey];

    public bool IsConfigured => _client is not null;

    public AzureSceneDetailService(IConfiguration configuration, ILogger<AzureSceneDetailService> logger)
    {
        _logger = logger;
        _configuration = configuration;
        _minConfidence = (configuration.GetValue<float?>(ConfigKeys.ComputerVisionMinTagConfidence) ?? 0.6f);

        var endpoint = configuration[ConfigKeys.ComputerVisionEndpoint];
        var key = CurrentKey;

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            _logger.LogInformation("Azure scene detail not configured; OCR and region captions are unavailable.");
            return;
        }

        _credential = new AzureKeyCredential(key);
        _client = new ImageAnalysisClient(new Uri(endpoint), _credential, new ImageAnalysisClientOptions
        {
            Retry =
            {
                MaxRetries = 2,
                Mode = Azure.Core.RetryMode.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                NetworkTimeout = TimeSpan.FromSeconds(60),
            }
        });
    }

    public async Task<SceneDetails> GetDetailsAsync(byte[] imageData, CancellationToken ct = default)
    {
        if (!IsConfigured) return SceneDetails.Empty;

        ArgumentNullException.ThrowIfNull(imageData);
        if (imageData.Length == 0) return SceneDetails.Empty;

        if (!string.IsNullOrWhiteSpace(CurrentKey) && _credential is not null)
            _credential.Update(CurrentKey);

        var start = Stopwatch.GetTimestamp();
        var features = _supportedFeatures ?? RichFeatures;

        try
        {
            return Map(await AnalyzeAsync(imageData, features, ct), start);
        }
        catch (RequestFailedException ex) when (ex.Status == 400 && features != BaseFeatures)
        {
            // The region rejected DenseCaptions. Fall back permanently and retry once.
            _logger.LogInformation(
                "Region does not support DenseCaptions; continuing with OCR + objects + people only.");
            _supportedFeatures = BaseFeatures;

            try
            {
                return Map(await AnalyzeAsync(imageData, BaseFeatures, ct), start);
            }
            catch (RequestFailedException retryEx)
            {
                _logger.LogWarning(retryEx, "Azure scene detail unavailable after fallback.");
                return SceneDetails.Empty;
            }
        }
        catch (RequestFailedException ex)
        {
            // Scene detail is an enhancement: losing it must degrade the description, not fail the
            // caller's pipeline.
            _logger.LogWarning(ex, "Azure scene detail failed (HTTP {Status}).", ex.Status);
            return SceneDetails.Empty;
        }
    }

    /// <inheritdoc />
    public bool SupportsCombinedAnalysis => IsConfigured;

    /// <inheritdoc />
    public async Task<CombinedVisionResult> AnalyzeAllAsync(byte[] imageData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (!IsConfigured || imageData.Length == 0)
            return new CombinedVisionResult(string.Empty, [], 0, SceneDetails.Empty, 0);

        if (!string.IsNullOrWhiteSpace(CurrentKey) && _credential is not null)
            _credential.Update(CurrentKey);

        var start = Stopwatch.GetTimestamp();

        // Caption and DenseCaptions are the two region-limited features and they travel together:
        // an endpoint that has one has the other. The probe result recorded for the detail path is
        // therefore reused here rather than re-discovered.
        var rich = _supportedFeatures is null || _supportedFeatures == RichFeatures;
        var features = rich ? CombinedFeatures : CombinedBaseFeatures;

        try
        {
            return MapCombined(await AnalyzeAsync(imageData, features, ct), start);
        }
        catch (RequestFailedException ex) when (ex.Status == 400 && features == CombinedFeatures)
        {
            _logger.LogInformation(
                "Region does not support Caption/DenseCaptions; continuing with the base feature set.");
            _supportedFeatures = BaseFeatures;
            return MapCombined(await AnalyzeAsync(imageData, CombinedBaseFeatures, ct), start);
        }
    }

    /// <summary>
    /// Maps one analysis result onto both interfaces' shapes.
    /// </summary>
    /// <remarks>
    /// The description falls back to joined tags exactly as <c>AzureVisionService</c> does, because
    /// the regions that lack Caption are the same regions, and callers already understand that a
    /// keyword-derived description is not a real one — <c>SceneDescriber</c> is built around it.
    /// </remarks>
    private CombinedVisionResult MapCombined(ImageAnalysisResult result, long start)
    {
        var details = Map(result, start);

        var tags = result.Tags?.Values
            .Where(t => t.Confidence >= _minConfidence)
            .Select(t => t.Name)
            .ToList() ?? [];

        var caption = result.Caption?.Text;
        var description = !string.IsNullOrWhiteSpace(caption)
            ? caption
            : tags.Count > 0
                ? $"A photo showing {string.Join(", ", tags.Take(8))}"
                : "No description available";

        return new CombinedVisionResult(
            description,
            tags,
            result.Caption?.Confidence ?? 0,
            details,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private async Task<ImageAnalysisResult> AnalyzeAsync(
        byte[] imageData, VisualFeatures features, CancellationToken ct)
    {
        var response = await _client!.AnalyzeAsync(
            BinaryData.FromBytes(imageData), features,
            new ImageAnalysisOptions { Language = "en", GenderNeutralCaption = true }, ct);

        _supportedFeatures ??= features;
        return response.Value;
    }

    private SceneDetails Map(ImageAnalysisResult result, long start)
    {
        // OCR arrives as blocks of lines; flatten and drop whitespace-only reads.
        var textLines = result.Read?.Blocks
            .SelectMany(b => b.Lines)
            .Select(l => l.Text.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList() ?? [];

        var regionCaptions = result.DenseCaptions?.Values
            .Where(c => c.Confidence >= _minConfidence)
            .Select(c => c.Text.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList() ?? [];

        var objects = result.Objects?.Values
            .SelectMany(o => o.Tags)
            .Where(t => t.Confidence >= _minConfidence)
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList() ?? [];

        var people = result.People?.Values.Count(p => p.Confidence >= _minConfidence) ?? 0;

        var elapsed = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _logger.LogInformation(
            "Scene detail extracted in {Elapsed}ms. Text={Text}, Regions={Regions}, Objects={Objects}, People={People}",
            elapsed, textLines.Count, regionCaptions.Count, objects.Count, people);

        return new SceneDetails(textLines, regionCaptions, objects, people, elapsed);
    }
}

/// <summary>Scene-detail provider used when the feature is mocked or unavailable.</summary>
public sealed class NullSceneDetailProvider : ISceneDetailProvider, ICombinedVisionAnalyzer
{
    public bool IsConfigured => false;

    /// <summary>False: there is nothing to combine, so callers take their two-call path.</summary>
    public bool SupportsCombinedAnalysis => false;

    public Task<SceneDetails> GetDetailsAsync(byte[] imageData, CancellationToken ct = default)
        => Task.FromResult(SceneDetails.Empty);

    public Task<CombinedVisionResult> AnalyzeAllAsync(byte[] imageData, CancellationToken ct = default)
        => Task.FromResult(new CombinedVisionResult(string.Empty, [], 0, SceneDetails.Empty, 0));
}
