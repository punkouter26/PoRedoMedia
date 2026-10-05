using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Configuration;

namespace PoRedoMedia.UnitTests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("true", "Development", true)]
    [InlineData("true", "Test", true)]
    [InlineData("true", "Production", false)]
    [InlineData("false", "Development", false)]
    public void Mock_AI_is_never_enabled_in_Production(string flag, string environment, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mocks:UseMockAi"] = flag }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        Assert.Equal(expected, MockAi.IsEnabled(config, env));
    }

    [Theory]
    [InlineData("PoRedoMedia--AiFoundry--Key", true, "AiFoundry:Key")]
    [InlineData("AzureAd--TenantId", true, "AzureAd:TenantId")]
    [InlineData("PoMemeVideo--AiFoundry--Key", false, null)]
    [InlineData("PoRedoImage-OpenAI-ApiKey", false, null)]
    public void Only_this_apps_secrets_and_the_shared_sign_in_secrets_are_loaded(string secretName, bool loaded, string? configKey)
    {
        var manager = new PrefixKeyVaultSecretManager("PoRedoMedia");

        Assert.Equal(loaded, manager.Load(SecretModelFactory.SecretProperties(name: secretName)));
        if (loaded)
            Assert.Equal(configKey, manager.GetKey(new KeyVaultSecret(secretName, "value")));
    }
}
