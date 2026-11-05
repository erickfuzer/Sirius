using Avalonia;
using Sirius.Terminal;

namespace Sirius.Tests;

public class TerminalWheelTests
{
    private static TerminalBuffer Buffer(string sequences)
    {
        var buffer = new TerminalBuffer(120, 40);
        buffer.Feed(sequences);
        return buffer;
    }

    [Fact]
    public void Terminal_normal_nao_manda_nada_e_rola_o_historico()
    {
        Assert.Null(TerminalKeys.Wheel(true, 0, 0, Buffer("")));
    }

    [Fact]
    public void Programa_que_pede_o_mouse_recebe_a_roda_com_a_posicao()
    {
        var buffer = Buffer("\u001b[?1049h\u001b[?1000h\u001b[?1006h");

        Assert.Equal("\u001b[<64;5;3M", TerminalKeys.Wheel(true, 4, 2, buffer));
        Assert.Equal("\u001b[<65;5;3M", TerminalKeys.Wheel(false, 4, 2, buffer));
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("1002")]
    [InlineData("1003")]
    public void Todos_os_modos_de_mouse_contam(string mode)
    {
        Assert.True(Buffer($"\u001b[?{mode}h").MouseTracking);
    }

    [Fact]
    public void Formato_antigo_guarda_a_posicao_em_um_caractere_e_tem_limite()
    {
        var buffer = Buffer("\u001b[?1000h");

        Assert.Equal("\u001b[M" + (char)96 + (char)37 + (char)35, TerminalKeys.Wheel(true, 4, 2, buffer));
        Assert.Equal("\u001b[M" + (char)97 + (char)255 + (char)255, TerminalKeys.Wheel(false, 500, 500, buffer));
    }

    [Fact]
    public void Tela_cheia_sem_mouse_recebe_setas()
    {
        var buffer = Buffer("\u001b[?1049h");

        Assert.Equal("\u001b[A", TerminalKeys.Wheel(true, 0, 0, buffer));
        Assert.Equal("\u001b[B", TerminalKeys.Wheel(false, 0, 0, buffer));

        buffer.Feed("\u001b[?1h");
        Assert.Equal("\u001bOA", TerminalKeys.Wheel(true, 0, 0, buffer));
    }

    [Fact]
    public void Quando_o_programa_larga_o_mouse_a_roda_volta_ao_normal()
    {
        var buffer = Buffer("\u001b[?1049h\u001b[?1000h\u001b[?1006h");

        buffer.Feed("\u001b[?1006l\u001b[?1000l\u001b[?1049l");

        Assert.False(buffer.MouseTracking);
        Assert.False(buffer.SgrMouse);
        Assert.Null(TerminalKeys.Wheel(true, 0, 0, buffer));
    }

    [Fact]
    public void Reiniciar_o_terminal_larga_o_mouse()
    {
        var buffer = Buffer("\u001b[?1000h\u001b[?1006h");

        buffer.Feed("\u001bc");

        Assert.False(buffer.MouseTracking);
        Assert.False(buffer.SgrMouse);
    }
}

public class BlockElementsTests
{
    private static Rect Bounds(int rune)
    {
        var parts = BlockElements.For(rune).Parts;
        return parts.Skip(1).Aggregate(parts[0], (all, part) => all.Union(part));
    }

    [Fact]
    public void Todos_os_blocos_tem_desenho_dentro_da_celula()
    {
        for (var rune = BlockElements.First; rune <= BlockElements.Last; rune++)
        {
            Assert.True(BlockElements.IsBlock(rune));
            var (parts, opacity) = BlockElements.For(rune);
            Assert.NotEmpty(parts);
            Assert.InRange(opacity, 0.2, 1);
            Assert.All(parts, part =>
            {
                Assert.True(part.Width > 0 && part.Height > 0);
                Assert.True(part.X >= 0 && part.Y >= 0 && part.Right <= 1 && part.Bottom <= 1);
            });
        }
    }

    [Theory]
    [InlineData(0x2580, 0, 0, 1, 0.5)]
    [InlineData(0x2581, 0, 0.875, 1, 0.125)]
    [InlineData(0x2584, 0, 0.5, 1, 0.5)]
    [InlineData(0x2587, 0, 0.125, 1, 0.875)]
    [InlineData(0x2588, 0, 0, 1, 1)]
    [InlineData(0x2589, 0, 0, 0.875, 1)]
    [InlineData(0x258C, 0, 0, 0.5, 1)]
    [InlineData(0x258F, 0, 0, 0.125, 1)]
    [InlineData(0x2590, 0.5, 0, 0.5, 1)]
    [InlineData(0x2594, 0, 0, 1, 0.125)]
    [InlineData(0x2595, 0.875, 0, 0.125, 1)]
    [InlineData(0x2598, 0, 0, 0.5, 0.5)]
    [InlineData(0x259D, 0.5, 0, 0.5, 0.5)]
    [InlineData(0x2596, 0, 0.5, 0.5, 0.5)]
    [InlineData(0x2597, 0.5, 0.5, 0.5, 0.5)]
    public void Blocos_de_uma_parte_ficam_no_lugar_certo(int rune, double x, double y, double width, double height)
    {
        Assert.Equal(new Rect(x, y, width, height), Assert.Single(BlockElements.For(rune).Parts));
    }

    [Theory]
    [InlineData(0x2599, 3)]
    [InlineData(0x259A, 2)]
    [InlineData(0x259B, 3)]
    [InlineData(0x259C, 3)]
    [InlineData(0x259E, 2)]
    [InlineData(0x259F, 3)]
    public void Quadrantes_combinados_cobrem_a_celula_inteira(int rune, int count)
    {
        Assert.Equal(count, BlockElements.For(rune).Parts.Length);
        Assert.Equal(new Rect(0, 0, 1, 1), Bounds(rune));
    }

    [Fact]
    public void Sombreados_usam_cor_mais_fraca()
    {
        Assert.Equal(0.25, BlockElements.For(0x2591).Opacity);
        Assert.Equal(0.5, BlockElements.For(0x2592).Opacity);
        Assert.Equal(0.75, BlockElements.For(0x2593).Opacity);
    }
}
