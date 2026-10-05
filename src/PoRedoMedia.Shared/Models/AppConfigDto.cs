using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Shared.Models;

/// <summary>What the client needs to know about how the server is configured.</summary>
public sealed record AppConfigDto(bool UseMockAi, bool DevLoginEnabled);

/// <summary>Source-generated JSON for every wire type, so the browser needs no reflection.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AppConfigDto))]
public sealed partial class WireJson : JsonSerializerContext;
