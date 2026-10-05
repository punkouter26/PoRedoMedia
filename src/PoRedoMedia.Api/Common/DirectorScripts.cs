using System.Text.Json;
using System.Text.Json.Serialization;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// A director script between its stored JSON, the domain entries (strong ids) and the wire DTOs
/// (raw GUIDs).
/// </summary>
/// <remarks>
/// Processing writes scripts, Output reads and rewrites them, and Sharing reads them for remixes.
/// Each slice carried its own copy of the entry mapping and its own serializer options — Output's
/// hand-written copy of <see cref="FromDto"/> was the only caller-shaped one, because Output may
/// not reference Processing. Common is where every slice can reach one version.
/// </remarks>
public static class DirectorScripts
{
    /// <summary>Enums as names; tolerant of the older shapes already in storage.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Sticker overlays the renderer ships (<c>wwwroot/overlays/{id}.png</c>).</summary>
    public static readonly string[] Stickers = ["deal-with-it", "laser-eyes", "thug-life", "red-circle", "clown-wig", "explosion"];

    /// <summary>
    /// Most cues one script holds. The script is stored as a single Table Storage string
    /// (32K characters), and every cue is one more ffmpeg input and filter pass.
    /// </summary>
    public const int MaxCues = 40;

    /// <summary>Longest caption burned into the video; anything longer runs off the frame anyway.</summary>
    public const int MaxCaptionLength = 120;

    public static DirectorScript Create(MediaId mediaId, IReadOnlyCollection<ScriptEntry> entries) => new()
    {
        MediaId = mediaId,
        TotalSoundCount = entries.Count,
        EntriesJson = JsonSerializer.Serialize(entries, JsonOpts),
    };

    public static List<ScriptEntry> ReadEntries(DirectorScript script)
        => JsonSerializer.Deserialize<List<ScriptEntry>>(script.EntriesJson, JsonOpts) ?? [];

    public static ScriptEntryDto ToDto(ScriptEntry entry) => new()
    {
        EntryId = entry.EntryId.Value,
        MediaId = entry.MediaId.Value,
        TimestampMs = entry.TimestampMs,
        SoundId = entry.SoundId.Value,
        SoundName = entry.SoundName,
        ActionVectorTags = entry.ActionVectorTags,
        SceneDescription = entry.SceneDescription,
        SelectionRationale = entry.SelectionRationale,
        IsIronic = entry.IsIronic,
        VisualEffect = entry.VisualEffect,
        OverlayAssetId = entry.OverlayAssetId,
        PlacementType = entry.PlacementType,
        CaptionText = entry.CaptionText,
        CaptionPosition = entry.CaptionPosition,
        OverlayX = entry.OverlayX,
        OverlayY = entry.OverlayY,
        OverlayScale = entry.OverlayScale,
    };

    /// <summary>
    /// A studio-edited cue back into the domain; a cue added in the studio gets a fresh id. The
    /// DTO comes from a browser, so the sticker — which the renderer turns into a file path — must
    /// be one that ships, and the caption is bounded.
    /// </summary>
    public static ScriptEntry FromDto(ScriptEntryDto e, MediaId mediaId) => new()
    {
        EntryId = new EntryId(e.EntryId != Guid.Empty ? e.EntryId : Guid.NewGuid()),
        MediaId = mediaId,
        TimestampMs = e.TimestampMs,
        SoundId = new SoundId(e.SoundId),
        SoundName = e.SoundName,
        ActionVectorTags = e.ActionVectorTags ?? [],
        SceneDescription = e.SceneDescription ?? string.Empty,
        SelectionRationale = e.SelectionRationale ?? string.Empty,
        IsIronic = e.IsIronic,
        VisualEffect = e.VisualEffect,
        OverlayAssetId = Stickers.Contains(e.OverlayAssetId) ? e.OverlayAssetId : null,
        PlacementType = e.PlacementType,
        CaptionText = e.CaptionText is { Length: > MaxCaptionLength } caption ? caption[..MaxCaptionLength] : e.CaptionText,
        CaptionPosition = e.CaptionPosition,
        OverlayX = e.OverlayX,
        OverlayY = e.OverlayY,
        OverlayScale = e.OverlayScale,
    };
}
