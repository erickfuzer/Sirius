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

    // Escuras o bastante para o tom da cápsula no tema grafite, medido em PaletteTests.
    private static readonly Color[] OnLight = Parse(
        "#16171A", "#8F1414", "#0B4F22", "#5F4300", "#134586", "#68189E", "#064A47", "#3A3D44",
        "#2B2E34", "#8A1212", "#0B4F22", "#513900", "#103B73", "#5A1589", "#064643", "#45484F");

    // A cápsula do grafite é translúcida. O terminal não pinta o fundo normal, mas o texto invertido
    // usa esta cor como tinta, e tinta transparente ficaria apagada.
    private static readonly IBrush SolidOnLight = B("#B2B6BE");

    public static TerminalScheme For(Palette palette) =>
        palette == Palette.Grafite
            ? new TerminalScheme(palette.CapsuleInk, SolidOnLight, palette.RunBackground, B("#403D5A85"), OnLight)
            : new TerminalScheme(palette.CapsuleInk, palette.CapsuleBackground, palette.RunBackground, B("#599DB8E0"), OnDark);

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
