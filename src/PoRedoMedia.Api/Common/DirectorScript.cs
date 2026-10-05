namespace PoRedoMedia.Api.Common;

/// <summary>A session's cues as stored (one row per session); <see cref="DirectorScripts"/> reads and writes the JSON.</summary>
public class DirectorScript
{
    public MediaId MediaId { get; init; }
    public int TotalSoundCount { get; set; }
    public string EntriesJson { get; set; } = "[]";
}
