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
[JsonSerializable(typeof(QuotaStatusDto))]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(Dictionary<string, RunRequest>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(RunDto))]
[JsonSerializable(typeof(List<RunDto>))]
[JsonSerializable(typeof(RunProgressDto))]
[JsonSerializable(typeof(List<MemeTemplateDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(FrameUploadRequest))]
[JsonSerializable(typeof(FramesResult))]
[JsonSerializable(typeof(ShareLinkDto))]
[JsonSerializable(typeof(List<SoundAssetDto>))]
[JsonSerializable(typeof(SoundAssetDto))]
public sealed partial class WireJson : JsonSerializerContext;
