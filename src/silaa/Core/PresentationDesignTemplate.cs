namespace silaa.Core;

public sealed record DocumentDesignTemplate(
    string Id,
    string Name,
    string Description,
    string Background,
    string TitleColor,
    string BodyColor,
    string AccentColor,
    string MutedColor,
    string FontFamily,
    string Layout,
    bool IsBuiltIn = false,
    string Format = "pptx");
