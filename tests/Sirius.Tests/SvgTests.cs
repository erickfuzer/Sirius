using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class SvgTests
{
    private const string Rect = "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='50'><rect width='100' height='50' fill='#ff0000'/></svg>";

    private static SKColor Center(SvgPicture picture)
    {
        using var bitmap = SKBitmap.Decode(picture.Png);
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    }

    [Fact]
    public void Desenha_no_tamanho_do_svg_com_folga_para_o_zoom()
    {
        var picture = SvgRender.Render(Rect);

        Assert.NotNull(picture);
        Assert.Equal((100.0, 50.0), (picture.Width, picture.Height));
        Assert.Equal((SvgRender.MaxSide, SvgRender.MaxSide / 2), (picture.PixelWidth, picture.PixelHeight));
        Assert.Equal(new SKColor(255, 0, 0), Center(picture));
    }

    [Fact]
    public void Icone_pequeno_tambem_fica_nitido()
    {
        var picture = SvgRender.Render("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' width='24' height='24'><circle cx='12' cy='12' r='10'/></svg>");

        Assert.NotNull(picture);
        Assert.Equal(24, picture.Width);
        Assert.Equal(SvgRender.MaxSide, picture.PixelWidth);
    }

    [Theory]
    [InlineData("")]
    [InlineData("oi")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect")]
    public void Texto_que_ainda_nao_e_desenho_devolve_nada(string text) => Assert.Null(SvgRender.Render(text));

    [Fact]
    public async Task Texto_grande_demais_nem_e_desenhado() =>
        Assert.Null(await SvgRender.RenderAsync(new string(' ', SvgRender.MaxChars + 1)));

    [Theory]
    [InlineData("src:url(http://x.com/f.woff)", "src:url()")]
    [InlineData("fill:url('https://x.com/a.svg#g')", "fill:url()")]
    [InlineData("fill:url(#g)", "fill:url(#g)")]
    [InlineData("background:url(data:image/png;base64,AAAA)", "background:url(data:image/png;base64,AAAA)")]
    [InlineData("src:URL( \"//x.com/f.woff\" )", "src:url()")]
    public void Endereco_de_fora_some_do_texto_desenhado(string css, string expected) =>
        Assert.Equal(expected, SvgRender.WithoutExternalUrls(css));

    [Fact]
    public async Task Nunca_busca_nada_fora_do_texto()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var hits = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    Interlocked.Increment(ref hits);
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        });

        var u = $"http://127.0.0.1:{port}";
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='20' height='20'>" +
                  $"<style>@font-face{{font-family:X;src:url({u}/f.woff)}} text{{font-family:X}} @import url({u}/a.css);</style>" +
                  $"<image href='{u}/a.png' width='5' height='5'/><image xlink:href='{u}/b.png' width='5' height='5'/>" +
                  $"<use href='{u}/c.svg#x'/><filter id='f'><feImage href='{u}/d.png'/></filter>" +
                  $"<rect width='5' height='5' filter='url(#f)' fill='url({u}/e.svg#g)'/><text y='15'>a</text></svg>";

        Assert.NotNull(await SvgRender.RenderAsync(svg));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref hits));
        listener.Stop();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void Imagem_embutida_no_texto_continua()
    {
        var picture = SvgRender.Render("<svg xmlns='http://www.w3.org/2000/svg' width='2' height='2'><image width='2' height='2' " +
            "href='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=='/></svg>");

        Assert.NotNull(picture);
        Assert.NotEqual(0, Center(picture).Alpha);
    }
}

public class SvgWindowTests
{
    private static MainWindow Open(TempFolder temp, AppSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task Rendered(MainWindow window)
    {
        await window.SvgRendered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
    }

    // A pausa na digitação dispara o desenho, e os testes não esperam o relógio.
    private static async Task Typed(MainWindow window, string text)
    {
        window.ActiveDocument!.Document.Text = text;
        var limit = DateTime.UtcNow.AddSeconds(5);
        var before = window.SvgRendered;
        while (window.SvgRendered == before && DateTime.UtcNow < limit)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        await Rendered(window);
    }

    [AvaloniaFact]
    public async Task Svg_abre_com_o_desenho_ao_lado()
    {
        using var temp = new TempFolder();
        var path = temp.File("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='60'><circle cx='30' cy='30' r='20' fill='teal'/></svg>");
        var window = Open(temp);

        window.OpenFile(path);
        await Rendered(window);

        Assert.True(window.IsSvgPreviewVisible);
        Assert.True(window.EditorVisible);
        Assert.True(window.HasSvgPicture);
        Assert.Equal(120, window.SvgPreviewControl.Info!.Width);
        Assert.Contains("Esconder a prévia do SVG", window.PaletteCommandNames("prévia"));

        // Outro tipo de arquivo não tem prévia.
        window.OpenFile(temp.File("a.html", "<p>oi</p>"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsSvgPreviewVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Texto_quebrado_mantem_o_ultimo_desenho_e_avisa()
    {
        using var temp = new TempFolder();
        const string Good = "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><rect width='40' height='40'/></svg>";
        var path = temp.File("a.svg", Good);
        var window = Open(temp);
        window.OpenFile(path);
        await Rendered(window);

        await Typed(window, "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='40'><rect width=");
        Assert.True(window.HasSvgPicture);
        Assert.True(window.IsSvgNoteVisible);

        await Typed(window, Good.Replace("40", "80"));
        Assert.False(window.IsSvgNoteVisible);
        Assert.Equal(80, window.SvgPreviewControl.Info!.Width);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Svg_vazio_explica_o_que_fazer()
    {
        using var temp = new TempFolder();
        var window = Open(temp);
        window.OpenFile(temp.File("novo.svg", ""));
        await Rendered(window);

        Assert.False(window.HasSvgPicture);
        Assert.Contains("SVG válido", window.SvgMessageText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Botao_esconde_e_a_escolha_fica_guardada()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.svg", "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>");
        var window = Open(temp);
        window.OpenFile(path);
        await Rendered(window);

        window.FindControl<Button>("SvgToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsSvgPreviewVisible);
        Assert.False(AppSettings.Load(Path.Combine(temp.Path, "settings.json")).SvgPreview);
        Assert.Contains("Mostrar a prévia do SVG", window.PaletteCommandNames("prévia"));

        window.ToggleSvgPreview();
        await Rendered(window);
        Assert.True(window.IsSvgPreviewVisible);
        Assert.True(window.HasSvgPicture);
        window.Close();
    }
}
