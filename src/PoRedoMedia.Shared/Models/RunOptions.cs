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

    /// <summary>A <c>RapStyle</c> name. Absent = Trap.</summary>
    public const string RoastStyle = "RapRoast.style";

    /// <summary>A <c>RoastIntensity</c> name. Absent = Roast.</summary>
    public const string RoastIntensity = "RapRoast.intensity";

    /// <summary>"true" allows profanity in the lyrics.</summary>
    public const string RoastExplicit = "RapRoast.explicit";

    /// <summary>One style prompt for Bulk styles, index 0 to 9. None sent = the user's saved set.</summary>
    public static string BulkPrompt(int index) => $"BulkStyles.prompt{index}";
}

/// <summary>A meme layout: where each line of text goes. The user's picture is the artwork.</summary>
public sealed record MemeTemplateDto(string Id, string Name, string Description, int RequiredZoneCount, string[] ZoneLabels);
