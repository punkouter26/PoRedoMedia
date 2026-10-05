namespace PoRedoMedia.Shared.Models;

/// <summary>The option keys a run request may carry, and their fixed values.</summary>
public static class RunOptions
{
    /// <summary>Model id from the model picker for looking at an image. Absent = the default provider.</summary>
    public const string VisionModel = "Vision.model";

    /// <summary>Id of a recipe in <see cref="StyleRecipeCatalog"/>. Absent = recreate the picture as it is.</summary>
    public const string RestyleStyle = "Restyle.style";

    /// <summary>A style in the user's own words. Wins over <see cref="RestyleStyle"/>.</summary>
    public const string RestylePrompt = "Restyle.prompt";

    public const string MemeMode = "MemeCaption.mode";
    public const string MemeModeAi = "ai";
    public const string MemeModeText = "text";
    public const string MemeModeTemplate = "template";
    public const string MemeTop = "MemeCaption.top";
    public const string MemeBottom = "MemeCaption.bottom";
    public const string MemeTemplate = "MemeCaption.template";

    public static string MemeZone(int index) => $"MemeCaption.zone{index}";
}

/// <summary>A meme layout: where each line of text goes. The user's picture is the artwork.</summary>
public sealed record MemeTemplateDto(string Id, string Name, string Description, int RequiredZoneCount, string[] ZoneLabels);
