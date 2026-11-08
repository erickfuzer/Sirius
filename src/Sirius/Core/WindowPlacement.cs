using Avalonia;

namespace Sirius.Core;

// Onde a janela abre. O tamanho padrão, 1320 por 860, é mais alto que a área livre de um notebook de 768
// linhas ou de um monitor com zoom do Windows. Centralizada assim, o topo ficava acima da tela, levando a
// barra com fechar e minimizar, e não havia como arrastar a janela de volta.
public static class WindowPlacement
{
    private const double Margin = 16;

    // A área livre vem em pixels da tela, e o tamanho, em unidades da interface (pixels divididos pela escala).
    public static (double Width, double Height, PixelPoint Position) Fit(PixelRect workingArea, double scaling, double width, double height)
    {
        scaling = scaling > 0 ? scaling : 1;
        var areaWidth = workingArea.Width / scaling;
        var areaHeight = workingArea.Height / scaling;
        var fittedWidth = Math.Min(width, Math.Max(areaWidth - 2 * Margin, areaWidth * 0.9));
        var fittedHeight = Math.Min(height, Math.Max(areaHeight - 2 * Margin, areaHeight * 0.9));

        var x = workingArea.X + (int)Math.Round((workingArea.Width - fittedWidth * scaling) / 2);
        var y = workingArea.Y + (int)Math.Round((workingArea.Height - fittedHeight * scaling) / 2);
        // O topo nunca fica acima da área livre, para a barra da janela sempre aparecer.
        return (fittedWidth, fittedHeight, new PixelPoint(Math.Max(x, workingArea.X), Math.Max(y, workingArea.Y)));
    }
}
