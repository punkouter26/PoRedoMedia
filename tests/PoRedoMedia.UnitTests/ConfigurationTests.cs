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

    [Fact]
    public void The_sweep_removes_stale_uploads_and_expired_items_but_never_a_pinned_one()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        MediaItem Item(MediaStatus status, int ageDays, bool pinned = false) => new()
        {
            Owner = new UserId("u"), Id = MediaId.New(), Kind = PoRedoMedia.Shared.Enums.MediaKind.Image, Status = status, Origin = "Upload",
            Title = "t", ContentType = "image/png", Extension = ".png", Pinned = pinned, CreatedAt = now.AddDays(-ageDays),
        };
        var verdict = (MediaItem item, int days) => PoRedoMedia.Api.Features.Housekeeping.HousekeepingService.Classify(item, now, days).ToString();

        Assert.Equal("Keep", verdict(Item(MediaStatus.Ready, 29), 30));
        Assert.Equal("DeleteExpired", verdict(Item(MediaStatus.Ready, 31), 30));
        Assert.Equal("Keep", verdict(Item(MediaStatus.Ready, 400, pinned: true), 30));
        Assert.Equal("Keep", verdict(Item(MediaStatus.Ready, 400), 0));
        Assert.Equal("DeleteAbandonedUpload", verdict(Item(MediaStatus.Uploading, 2), 0));
        Assert.Equal("Keep", verdict(Item(MediaStatus.Uploading, 0), 30));
    }
}
