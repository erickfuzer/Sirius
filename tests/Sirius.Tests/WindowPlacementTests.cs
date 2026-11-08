using Avalonia;
using Sirius.Core;

namespace Sirius.Tests;

// A janela sempre cabe na área livre da tela, com a barra de cima à vista.
public class WindowPlacementTests
{
    [Fact]
    public void Notebook_de_768_linhas_diminui_a_janela()
    {
        // 1366 x 768 com a barra de tarefas de 40 pixels.
        var (width, height, position) = WindowPlacement.Fit(new PixelRect(0, 0, 1366, 728), 1, 1320, 860);

        Assert.Equal(1320, width);
        Assert.Equal(696, height);
        Assert.Equal(new PixelPoint(23, 16), position);
    }

    [Fact]
    public void Zoom_do_windows_conta()
    {
        // 1920 x 1080 a 125%: a área livre tem 1032 pixels, ou 825,6 na interface.
        var (_, height, position) = WindowPlacement.Fit(new PixelRect(0, 0, 1920, 1032), 1.25, 1320, 860);

        Assert.True(height <= 1032 / 1.25);
        Assert.True(position.Y >= 0);
        Assert.True(position.Y + height * 1.25 <= 1032);
    }

    [Fact]
    public void Tela_grande_mantem_o_tamanho_e_centraliza()
    {
        var (width, height, position) = WindowPlacement.Fit(new PixelRect(0, 0, 2560, 1400), 1, 1320, 860);

        Assert.Equal((1320d, 860d), (width, height));
        Assert.Equal(new PixelPoint(620, 270), position);
    }

    [Fact]
    public void Segundo_monitor_usa_a_propria_posicao()
    {
        var (_, _, position) = WindowPlacement.Fit(new PixelRect(1920, -200, 1366, 728), 1, 1320, 860);

        Assert.Equal(1920 + 23, position.X);
        Assert.Equal(-200 + 16, position.Y);
    }
}
