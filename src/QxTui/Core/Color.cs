namespace QxTui.Core;

/// <summary>
/// Color encoding for Cell fg/bg ints:
///   -1            terminal default
///   0..255        256-color palette index
///   Color.Rgb()   24-bit truecolor (marker bit + 0xRRGGBB)
///
/// Truecolor bypasses terminal palette remapping entirely — the exact hue
/// you ask for is the hue you get (the scenery blue-blob incident class
/// becomes impossible for rgb themes).
/// </summary>
public static class Color
{
    private const int RgbBit = 0x40000000;

    public static int Rgb(int r, int g, int b) =>
        RgbBit | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);

    public static bool IsRgb(int c) => c >= 0 && (c & RgbBit) != 0;

    public static int R(int c) => (c >> 16) & 0xFF;
    public static int G(int c) => (c >> 8) & 0xFF;
    public static int B(int c) => c & 0xFF;
}
