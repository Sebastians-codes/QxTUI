namespace QxTui.Core;

/// <summary>
/// Semantic color roles — apps and widgets name WHAT they paint, never a
/// raw palette index. Defaults live on the grayscale ramp (232-250) plus a
/// few stable accents, because terminal themes remap cube colors
/// arbitrarily (the scenery blue-blob incident) but leave grays alone.
/// Override per app via <see cref="App.Theme"/>.
/// </summary>
public sealed record Theme(
    int Border,
    int Title,
    int Text,
    int Dim,
    int Faint,
    int Accent,
    int Success,
    int Warning,
    int Danger,
    int PanelBg)
{
    /// <summary>Grayscale-safe night theme: the default.</summary>
    public static readonly Theme Night = new(
        Border: 238,
        Title: 39,
        Text: 250,
        Dim: 244,
        Faint: 236,
        Accent: 45,
        Success: 42,
        Warning: 214,
        Danger: 196,
        PanelBg: 233);

    /// <summary>Pure grayscale — maximum theme-portability, zero hue.</summary>
    public static readonly Theme Mono = new(
        Border: 238,
        Title: 255,
        Text: 250,
        Dim: 244,
        Faint: 236,
        Accent: 252,
        Success: 248,
        Warning: 252,
        Danger: 255,
        PanelBg: 233);

    public static Theme Default => Night;
}
