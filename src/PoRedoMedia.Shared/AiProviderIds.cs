namespace PoRedoMedia.Shared;

/// <summary>
/// Ids for the AI providers a user can pick. The prefix says where the model runs:
/// <c>remote:</c> a metered cloud service, <c>ollama:</c> a local daemon (development only),
/// <c>browser:</c> on the user's device. The same weights under two prefixes are not interchangeable.
/// </summary>
public static class AiProviderIds
{
    public const string OllamaPrefix = "ollama:";
    public const string BrowserPrefix = "browser:";

    public const string GeminiVision = "remote:gemini-vision";
    public const string AzureOpenAiVision = "remote:azure-openai-vision";
    public const string AzureComputerVision = "remote:azure-cv";
    public const string OllamaVision = "ollama:vision";
}
