using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Shared.Models;

/// <summary>Source-generated JSON for every wire type, so the browser needs no reflection.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfigDto))]
[JsonSerializable(typeof(MediaDto))]
[JsonSerializable(typeof(List<MediaDto>))]
[JsonSerializable(typeof(UploadRequest))]
[JsonSerializable(typeof(UploadTicket))]
[JsonSerializable(typeof(MediaUpdateRequest))]
public sealed partial class WireJson : JsonSerializerContext;
