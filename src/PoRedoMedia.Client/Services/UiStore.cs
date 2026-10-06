using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.Services;

/// <summary>
/// Small things remembered in this browser: the last options used, saved recipes. Nothing here
/// is needed for the app to work, so a browser that refuses storage just forgets.
/// </summary>
// ponytail: per browser, not per account. Move recipes to a table if they should follow the user.
public sealed class UiStore(IJSRuntime js)
{
    public const string LastOptions = "po.options";
    public const string Recipes = "po.recipes";

    public async Task<T?> ReadAsync<T>(string key, JsonTypeInfo<T> type)
    {
        try
        {
            return await js.InvokeAsync<string?>("poMedia.read", key) is { Length: > 0 } stored
                ? JsonSerializer.Deserialize(stored, type)
                : default;
        }
        catch (Exception ex) when (ex is JsonException or JSException)
        {
            return default;
        }
    }

    public ValueTask WriteAsync<T>(string key, T value, JsonTypeInfo<T> type) =>
        js.InvokeVoidAsync("poMedia.write", key, JsonSerializer.Serialize(value, type));
}
