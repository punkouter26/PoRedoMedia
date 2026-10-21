namespace PoRedoMedia.Shared;

/// <summary>Configuration key names. Read configuration through these, never string literals.</summary>
public static class ConfigKeys
{
    public const string AzureAdClientId = "AzureAd:ClientId";
    public const string AzureAdClientSecret = "AzureAd:ClientSecret";
    public const string AzureAdTenantId = "AzureAd:TenantId";
    public const string AzureAdCallbackPath = "AzureAd:CallbackPath";
    public const string AzureAdSignedOutCallbackPath = "AzureAd:SignedOutCallbackPath";
    public const string AzureAdAllowedTenantIds = "AzureAd:AllowedTenantIds";
    public const string AuthEnableFakeAuth = "Auth:EnableFakeAuth";

    /// <summary>Comma-separated email addresses that may sign in. Empty = any Microsoft account.</summary>
    public const string AuthAllowedEmails = "Auth:AllowedEmails";

    /// <summary>The address share links are built on, such as https://example.com. Empty = the request's own host.</summary>
    public const string AppPublicBaseUrl = "App:PublicBaseUrl";
    public const string KeyVaultUri = "KeyVault:Uri";
    public const string MocksUseMockAi = "Mocks:UseMockAi";
    public const string StorageConnectionString = "Storage:ConnectionString";
    public const string RenderQuotaDailyLimit = "RenderQuota:DailyLimit";

    /// <summary>Image runs (remote model waits, no render) executed at once. Video runs always go one at a time.</summary>
    public const string RunsRemoteConcurrency = "Runs:RemoteConcurrency";
    public const string RetentionDays = "Retention:Days";
    public const string HousekeepingEnabled = "Housekeeping:Enabled";

    /// <summary>Where traces, logs and metrics go. Empty = not sent anywhere.</summary>
    public const string ApplicationInsightsConnectionString = "ApplicationInsights:ConnectionString";

    public const string OpenAiEndpoint = "OpenAI:Endpoint";
    public const string OpenAiKey = "OpenAI:Key";
    public const string OpenAiChatDeployment = "OpenAI:ChatCompletionsDeployment";
    public const string ComputerVisionEndpoint = "ComputerVision:Endpoint";
    public const string ComputerVisionApiKey = "ComputerVision:ApiKey";
    public const string ComputerVisionMinTagConfidence = "ComputerVision:MinTagConfidence";
    public const string GoogleApiKey = "Google:ApiKey";
    public const string GoogleVisionModel = "Google:VisionModel";
    public const string GoogleImageModel = "Google:ImageModel";
    public const string GoogleLyriaModel = "Google:LyriaModel";
    public const string GoogleVeoModel = "Google:VeoModel";
    public const string GoogleUpscaleModel = "Google:UpscaleModel";

    /// <summary>The model that watches a whole video for Meme-ify. Empty = <see cref="GoogleVisionModel"/>.</summary>
    public const string GoogleClipVisionModel = "Google:ClipVisionModel";

    /// <summary>False keeps Meme-ify on labelling sampled frames even when Gemini could watch the whole clip.</summary>
    public const string VisionWholeClip = "Vision:WholeClip";

    /// <summary>Who labels sampled video frames: "foundry" or "gemini". Empty = Foundry when configured, else Gemini.</summary>
    public const string VisionFrameProvider = "Vision:FrameProvider";
    public const string HuggingFaceApiKey = "HuggingFace:ApiKey";
    public const string VisionSecondOpinion = "Vision:SecondOpinion";
    public const string OllamaEndpoint = "Ollama:Endpoint";
    public const string OllamaVisionModel = "Ollama:VisionModel";
    public const string OllamaChatModel = "Ollama:ChatModel";
}
