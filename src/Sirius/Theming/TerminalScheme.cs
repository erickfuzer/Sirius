using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sirius.Terminal;

namespace Sirius.Theming;

// As cores do terminal. A cápsula é invertida, então o tema claro tem terminal escuro e vice-versa.
public sealed record TerminalScheme(IBrush Foreground, IBrush Background, IBrush Cursor, IBrush Selection, Color[] Ansi)
{
    private static readonly Color[] OnDark = Parse(
        "#5C5F67", "#FF6B6B", "#7EDC8A", "#F5C65B", "#7FB2FF", "#D79BFF", "#62D6D0", "#D8DADF",
        "#8E9198", "#FF8A8A", "#9BE8A5", "#FAD77F", "#A3C8FF", "#E4B8FF", "#8BE5E0", "#FFFFFF");

    private static readonly Color[] OnLight = Parse(
        "#16171A", "#C62828", "#1B7F3B", "#8A6100", "#1F5FAF", "#8B2FC9", "#0B7A75", "#55585F",
        "#3F4249", "#A81F1F", "#146B30", "#6F4E00", "#174B8C", "#6F22A3", "#085E5A", "#6F727A");

    public static TerminalScheme For(Palette palette) =>
        palette == Palette.Grafite
            ? new TerminalScheme(palette.CapsuleInk, palette.CapsuleBackground, palette.RunBackground, B("#4DC93500"), OnLight)
            : new TerminalScheme(palette.CapsuleInk, palette.CapsuleBackground, palette.RunBackground, B("#59FF6A2B"), OnDark);

    public Color Resolve(int color)
    {
        if ((color & CellStyle.TrueColorBit) != 0)
            return Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        if (color < 16)
            return Ansi[color];
        if (color < 232)
        {
            var index = color - 16;
            return Color.FromRgb(Level(index / 36), Level(index / 6 % 6), Level(index % 6));
        }
        var gray = (byte)(8 + (color - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    private static byte Level(int step) => step == 0 ? (byte)0 : (byte)(55 + step * 40);

    private static IBrush B(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    private static Color[] Parse(params string[] colors) => colors.Select(Color.Parse).ToArray();
}
