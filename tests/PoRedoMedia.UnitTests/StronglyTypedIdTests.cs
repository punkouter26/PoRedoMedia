using System.Text.Json;
using PoRedoMedia.Api.Common;

namespace PoRedoMedia.UnitTests;

public sealed class StronglyTypedIdTests
{
    // Table keys and wire payloads hold the bare GUID. Changing this orphans every stored row.
    [Fact]
    public void A_guid_id_serialises_as_the_bare_guid_and_parses_back()
    {
        var id = MediaId.New();

        Assert.Equal($"\"{id.Value}\"", JsonSerializer.Serialize(id));
        Assert.Equal(id, JsonSerializer.Deserialize<MediaId>($"\"{id.Value}\""));
        Assert.Equal(id.Value.ToString(), id.ToString());
        Assert.True(MediaId.TryParse(id.ToString(), null, out var parsed) && parsed == id);
        Assert.False(RunId.TryParse("not-a-guid", null, out _));
    }

    [Theory]
    [InlineData("dev|a@b.c", "dev%7Ca%40b.c")]
    [InlineData("a/b#c?d\\e", "a%2Fb%23c%3Fd%5Ce")]
    public void A_user_id_becomes_a_key_table_storage_accepts(string claim, string key)
    {
        Assert.Equal(key, new UserId(claim).Key);
    }
}
