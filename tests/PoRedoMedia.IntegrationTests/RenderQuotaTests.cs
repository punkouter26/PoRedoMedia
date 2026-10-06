using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Quota;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class RenderQuotaTests(AzuriteFixture azurite)
{
    private RenderQuotaService Quota(int? limit = null)
    {
        var settings = new Dictionary<string, string?> { ["Storage:ConnectionString"] = azurite.ConnectionString };
        if (limit is not null)
            settings["RenderQuota:DailyLimit"] = limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new RenderQuotaService(new StorageClients(configuration), configuration, TimeProvider.System, NullLogger<RenderQuotaService>.Instance);
    }

    [DockerFact]
    public async Task The_eleventh_run_of_the_day_is_refused_and_other_users_are_unaffected()
    {
        var quota = Quota();
        var user = new UserId($"dev|{Guid.NewGuid()}");

        for (var run = 1; run <= 10; run++)
            Assert.True((await quota.TryConsumeAsync(user)).Allowed, $"run {run} should be allowed");

        var (allowed, status) = await quota.TryConsumeAsync(user);
        Assert.False(allowed);
        Assert.Equal((10, 10, 0), (status.Used, status.Limit, status.Remaining));
        Assert.True(status.ResetsAt > DateTimeOffset.UtcNow);
        Assert.True((await quota.TryConsumeAsync(new UserId($"dev|{Guid.NewGuid()}"))).Allowed);
    }

    [DockerFact]
    public async Task A_refunded_credit_can_be_spent_again()
    {
        var quota = Quota(limit: 1);
        var user = new UserId($"dev|{Guid.NewGuid()}");
        Assert.True((await quota.TryConsumeAsync(user)).Allowed);
        Assert.False((await quota.TryConsumeAsync(user)).Allowed);

        await quota.RefundAsync(user);

        Assert.True((await quota.TryConsumeAsync(user)).Allowed);
    }

    [DockerFact]
    public async Task A_limit_of_zero_switches_the_quota_off()
    {
        var quota = Quota(limit: 0);
        var user = new UserId($"dev|{Guid.NewGuid()}");

        Assert.True((await quota.TryConsumeAsync(user)).Allowed);
        Assert.True((await quota.GetStatusAsync(user)).Unlimited);
    }
}
