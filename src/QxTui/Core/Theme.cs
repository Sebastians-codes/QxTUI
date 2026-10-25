namespace QxTui.Core;

/// <summary>
/// Semantic color roles — apps and widgets name WHAT they paint, never a
/// raw palette index. Defaults live on the grayscale ramp (232-250) plus a
/// few stable accents, because terminal themes remap cube colors
/// arbitrarily (the scenery blue-blob incident) but leave grays alone.
/// Override per app via <see cref="App.Theme"/>.
/// </summary>
/// <summary>Named color roles — widgets reference these, never raw ints,
/// so a theme switch recolors the whole app.</summary>
public enum ThemeRole
{
    Border,
    Title,
    Text,
    Dim,
    Faint,
    Accent,
    Success,
    Warning,
    Danger,
    PanelBg,
}

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

    /// <summary>Exact-hue night theme in 24-bit color — immune to terminal
    /// palette remapping (grays stay gray, accents stay their hex).</summary>
    public static readonly Theme NightRgb = new(
        Border: Color.Rgb(70, 70, 70),
        Title: Color.Rgb(0, 175, 255),
        Text: Color.Rgb(220, 220, 220),
        Dim: Color.Rgb(150, 150, 150),
        Faint: Color.Rgb(90, 90, 90),
        Accent: Color.Rgb(0, 175, 255),
        Success: Color.Rgb(0, 215, 95),
        Warning: Color.Rgb(255, 175, 0),
        Danger: Color.Rgb(255, 60, 60),
        PanelBg: Color.Rgb(18, 18, 18));

    public static Theme Default => Night;

    public int Get(ThemeRole role) => role switch
    {
        ThemeRole.Border => Border,
        ThemeRole.Title => Title,
        ThemeRole.Text => Text,
        ThemeRole.Dim => Dim,
        ThemeRole.Faint => Faint,
        ThemeRole.Accent => Accent,
        ThemeRole.Success => Success,
        ThemeRole.Warning => Warning,
        ThemeRole.Danger => Danger,
        ThemeRole.PanelBg => PanelBg,
        _ => Text,
    };
}
