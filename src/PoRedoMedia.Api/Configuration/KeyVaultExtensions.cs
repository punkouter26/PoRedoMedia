using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace PoRedoMedia.Api.Configuration;

public static class KeyVaultExtensions
{
    /// <summary>
    /// Loads this app's secrets from Key Vault with the caller's Azure identity (<c>az login</c>
    /// locally, managed identity in Azure). Skipped when no vault is configured or mock AI is on.
    /// </summary>
    public static void AddPoRedoMediaKeyVault(this WebApplicationBuilder builder)
    {
        var uri = builder.Configuration[ConfigKeys.KeyVaultUri];
        if (string.IsNullOrWhiteSpace(uri) || MockAi.IsEnabled(builder.Configuration, builder.Environment))
            return;

        try
        {
            // Built separately so a failed load leaves no half-added source behind.
            var secrets = new ConfigurationBuilder()
                .AddAzureKeyVault(new Uri(uri), new DefaultAzureCredential(), new PrefixKeyVaultSecretManager("PoRedoMedia"))
                .Build();
            builder.Configuration.AddConfiguration(secrets);
        }
        catch (Exception ex) when (builder.Environment.IsDevelopment())
        {
            // A developer without vault access still gets a running app: functions whose keys are
            // missing report themselves unavailable. Anywhere else an unreachable vault is fatal.
            Console.Error.WriteLine($"Key Vault {uri} could not be read ({ex.GetType().Name}); continuing without its secrets.");
        }
    }
}

/// <summary>
/// Maps <c>PoRedoMedia--AiFoundry--Key</c> to <c>AiFoundry:Key</c> and the shared
/// <c>AzureAd--*</c> secrets to <c>AzureAd:*</c>. Every other secret in the shared vault belongs
/// to another app and is not loaded.
/// </summary>
internal sealed class PrefixKeyVaultSecretManager(string prefix) : KeyVaultSecretManager
{
    private readonly string _prefix = prefix + "--";

    public override bool Load(SecretProperties secret) =>
        secret.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
        || secret.Name.StartsWith("AzureAd--", StringComparison.OrdinalIgnoreCase);

    public override string GetKey(KeyVaultSecret secret)
    {
        var name = secret.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
            ? secret.Name[_prefix.Length..]
            : secret.Name;
        return name.Replace("--", ConfigurationPath.KeyDelimiter);
    }
}
