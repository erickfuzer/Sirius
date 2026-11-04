using Avalonia.Media;
using Sirius.Theming;

namespace Sirius.Tests;

public class PaletteTests
{
    private const double ReadableText = 4.5;

    private static Color ColorOf(IBrush brush) => ((ISolidColorBrush)brush).Color;

    private static double Channel(byte value)
    {
        var scaled = value / 255.0;
        return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color color) =>
        0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    private static double Contrast(Color a, Color b)
    {
        var (first, second) = (Luminance(a), Luminance(b));
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    // O que a pessoa vê quando uma cor transparente fica por cima de outra.
    private static Color Over(Color top, Color below)
    {
        var alpha = top.A / 255.0;
        byte Mix(byte a, byte b) => (byte)Math.Round(alpha * a + (1 - alpha) * b);
        return Color.FromRgb(Mix(top.R, below.R), Mix(top.G, below.G), Mix(top.B, below.B));
    }

    private static Color CapsuleOverSheet(Palette palette) =>
        Over(ColorOf(palette.CapsuleBackground), ColorOf(palette.Sheet));

    [Fact]
    public void Capsula_do_grafite_e_translucida_e_mais_suave_que_a_tinta()
    {
        var capsule = ColorOf(Palette.Grafite.CapsuleBackground);

        Assert.InRange(capsule.A, (byte)0xC0, (byte)0xF0);
        Assert.True(Luminance(CapsuleOverSheet(Palette.Grafite)) < Luminance(ColorOf(Palette.Grafite.Ink)));
    }

    [Theory]
    [InlineData("grafite")]
    [InlineData("claro")]
    public void Textos_da_capsula_sao_legiveis(string theme)
    {
        var palette = theme == Palette.Grafite.Name ? Palette.Grafite : Palette.Claro;
        var ground = CapsuleOverSheet(palette);

        Assert.True(Contrast(ColorOf(palette.CapsuleInk), ground) >= ReadableText);
        Assert.True(Contrast(ColorOf(palette.CapsuleMuted), ground) >= ReadableText);
        Assert.True(Contrast(ColorOf(palette.TerminalError), ground) >= ReadableText);
        Assert.True(Contrast(ColorOf(palette.TerminalWarning), ground) >= ReadableText);
    }

    [Fact]
    public void Cores_do_terminal_sao_legiveis_na_capsula_do_grafite()
    {
        var ground = CapsuleOverSheet(Palette.Grafite);

        var scheme = TerminalScheme.For(Palette.Grafite);
        for (var index = 0; index < scheme.Ansi.Length; index++)
        {
            var contrast = Contrast(scheme.Ansi[index], ground);
            Assert.True(contrast >= ReadableText, $"Cor {index} ({scheme.Ansi[index]}) tem contraste {contrast:0.00}.");
        }
    }

    [Fact]
    public void Texto_invertido_do_terminal_usa_cor_solida()
    {
        foreach (var palette in new[] { Palette.Grafite, Palette.Claro })
            Assert.Equal(255, ColorOf(TerminalScheme.For(palette).Background).A);
    }
}
