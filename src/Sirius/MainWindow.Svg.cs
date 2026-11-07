using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using Sirius.Core;

namespace Sirius;

// A prévia do SVG: o desenho à direita do código, refeito numa pausa da digitação.
public partial class MainWindow
{
    private static readonly TimeSpan SvgDelay = TimeSpan.FromMilliseconds(250);

    private DispatcherTimer? _svgDelay;
    private int _svgVersion;
    private string? _svgShownPath;

    public bool IsSvgPreviewVisible => SvgPane.IsVisible;
    public bool HasSvgPicture => SvgView.Info is not null;
    public string SvgMessageText => SvgMessage.IsVisible ? SvgMessage.Text ?? "" : "";
    public bool IsSvgNoteVisible => SvgNote.IsVisible;
    public Editing.ImagePreview SvgPreviewControl => SvgView;

    // Termina quando o desenho pedido por último chega. Os testes esperam por ele.
    public Task SvgRendered { get; private set; } = Task.CompletedTask;

    private void InitializeSvg()
    {
        _svgDelay = new DispatcherTimer(SvgDelay, DispatcherPriority.Background, (_, _) =>
        {
            _svgDelay!.Stop();
            RenderSvg();
        });
        CodeEditor.TextChanged += (_, _) =>
        {
            if (!SvgPane.IsVisible)
                return;
            _svgDelay.Stop();
            _svgDelay.Start();
        };
    }

    private bool ShowsSvg => ActiveDocument is { IsImage: false } document && SvgRender.Supports(document.FullPath) && !IsDiffOpen;

    // Chamado sempre que a aba ou a superfície muda.
    private void UpdateSvgPane()
    {
        SvgToggle.IsVisible = ShowsSvg;
        SvgToggle.Classes.Set("on", _settings.SvgPreview);
        var visible = ShowsSvg && _settings.SvgPreview;
        var columns = EditorGrid.ColumnDefinitions;
        columns[0].Width = new Avalonia.Controls.GridLength(visible ? 3 : 1, Avalonia.Controls.GridUnitType.Star);
        columns[2].Width = visible ? new Avalonia.Controls.GridLength(2, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0);
        SvgDivider.IsVisible = visible;

        var wasVisible = SvgPane.IsVisible;
        SvgPane.IsVisible = visible;
        if (!visible)
        {
            _svgDelay?.Stop();
            _svgVersion++;
            return;
        }
        if (!wasVisible || !string.Equals(_svgShownPath, ActiveDocument!.FullPath, StringComparison.OrdinalIgnoreCase))
            RenderSvg();
    }

    private void OnSvgToggle(object? sender, RoutedEventArgs e) => ToggleSvgPreview();

    public void ToggleSvgPreview()
    {
        _settings.SvgPreview = !_settings.SvgPreview;
        _settings.Save(_settingsPath);
        UpdateSvgPane();
    }

    private void RenderSvg()
    {
        if (!SvgPane.IsVisible || ActiveDocument is not { } document)
            return;
        var version = ++_svgVersion;
        var path = document.FullPath;
        var text = document.Document.Text;
        SvgRendered = ShowSvgAsync(version, path, text);
    }

    private async Task ShowSvgAsync(int version, string path, string text)
    {
        var picture = await SvgRender.RenderAsync(text);
        // Outra digitação ou outra aba já pediu um desenho mais novo.
        if (version != _svgVersion)
            return;

        var samePath = string.Equals(_svgShownPath, path, StringComparison.OrdinalIgnoreCase);
        if (picture is null)
        {
            if (!samePath)
            {
                SvgView.Clear();
                _svgShownPath = path;
            }
            var empty = SvgView.Info is null;
            SvgMessage.Text = text.Length > SvgRender.MaxChars
                ? "Este SVG é grande demais para a prévia."
                : "Escreva um SVG válido para ver o desenho aqui.";
            SvgMessage.IsVisible = empty;
            SvgNote.IsVisible = !empty;
            return;
        }

        var info = new ImageInfo((int)Math.Max(1, Math.Round(picture.Width)), (int)Math.Max(1, Math.Round(picture.Height)),
            SvgRender.ByteCount(text), HasAlpha: true, SKEncodedOrigin.TopLeft);
        // Digitando no mesmo arquivo, o zoom em que a pessoa estava continua.
        SvgView.Show(new Bitmap(new MemoryStream(picture.Png)), info, keepView: samePath && SvgView.Info is not null,
            loadedScale: picture.PixelWidth / (double)info.PixelWidth);
        _svgShownPath = path;
        SvgMessage.IsVisible = false;
        SvgNote.IsVisible = false;
    }
}
