using Microsoft.Extensions.Configuration;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Media;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class MediaRepositoryTests(AzuriteFixture azurite)
{
    private MediaTableRepository Repository() => new(new StorageClients(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:ConnectionString"] = azurite.ConnectionString })
        .Build()));

    private static MediaItem NewItem(UserId owner, DateTimeOffset createdAt) => new()
    {
        Owner = owner,
        Id = MediaId.New(),
        Kind = MediaKind.Video,
        Status = MediaStatus.Ready,
        Origin = "Upload",
        ParentId = MediaId.New(),
        Title = "party.mp4",
        ContentType = "video/mp4",
        Extension = ".mp4",
        SizeBytes = 1234,
        DurationSeconds = 12.5,
        Pinned = true,
        ShareToken = "tok",
        CreatedAt = createdAt,
    };

    [DockerFact]
    public async Task An_item_round_trips_with_every_field()
    {
        var repository = Repository();
        var item = NewItem(new UserId($"dev|{Guid.NewGuid()}@x.y"), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

        await repository.SaveAsync(item);

        Assert.Equal(item, await repository.GetAsync(item.Owner, item.Id));
    }

    [DockerFact]
    public async Task A_user_sees_only_their_own_items_newest_first()
    {
        var repository = Repository();
        var alice = new UserId($"dev|alice-{Guid.NewGuid()}");
        var bob = new UserId($"dev|bob-{Guid.NewGuid()}");
        var older = NewItem(alice, DateTimeOffset.UtcNow.AddHours(-1));
        var newer = NewItem(alice, DateTimeOffset.UtcNow);
        var bobs = NewItem(bob, DateTimeOffset.UtcNow);
        foreach (var item in new[] { older, newer, bobs })
            await repository.SaveAsync(item);

        Assert.Equal([newer.Id, older.Id], (await repository.ListAsync(alice)).Select(m => m.Id));
        Assert.Null(await repository.GetAsync(alice, bobs.Id));
    }

    [DockerFact]
    public async Task A_deleted_item_is_gone()
    {
        var repository = Repository();
        var item = NewItem(new UserId($"dev|{Guid.NewGuid()}"), DateTimeOffset.UtcNow);
        await repository.SaveAsync(item);

        await repository.DeleteAsync(item.Owner, item.Id);

        Assert.Null(await repository.GetAsync(item.Owner, item.Id));
    }
}
