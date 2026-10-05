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
    public const string KeyVaultUri = "KeyVault:Uri";
    public const string MocksUseMockAi = "Mocks:UseMockAi";
    public const string StorageConnectionString = "Storage:ConnectionString";
    public const string RenderQuotaDailyLimit = "RenderQuota:DailyLimit";
}
