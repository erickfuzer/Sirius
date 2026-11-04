using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Theming;

namespace Sirius.Tests;

public class ImageTests
{
    private static byte[] Encoded(SKEncodedImageFormat format, int width = 40, int height = 20, bool transparent = false)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(transparent ? SKColors.Transparent : SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    // Foto de celular: guardada deitada, com a etiqueta EXIF dizendo como mostrar.
    private static byte[] JpegWithOrientation(byte orientation)
    {
        var jpeg = Encoded(SKEncodedImageFormat.Jpeg);
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0,
            1, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0,
            0, 0, 0, 0,
        ];
        return [.. jpeg.Take(2), .. exif, .. jpeg.Skip(2)];
    }

    // Só o cabeçalho de um BMP de 24 bits: o bastante para saber o tamanho sem precisar dos pixels.
    private static byte[] BmpHeader(int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = 0x42;
        bytes[1] = 0x4D;
        bytes[10] = 54;
        bytes[14] = 40;
        BitConverter.TryWriteBytes(bytes.AsSpan(18), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), height);
        bytes[26] = 1;
        bytes[28] = 24;
        return bytes;
    }

    private static string WriteImage(TempFolder temp, string name, byte[]? bytes = null)
    {
        var path = temp.File(name);
        File.WriteAllBytes(path, bytes ?? Encoded(SKEncodedImageFormat.Png));
        return path;
    }

    private static MainWindow Open(TempFolder temp, TempFolder settingsFolder, AppSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(settingsFolder.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Bitmap StubBitmap() => new(new MemoryStream(Encoded(SKEncodedImageFormat.Png)));

    private static ImageInfo InfoOf(int width, int height) => new(width, height, 1000, false, SKEncodedOrigin.TopLeft);

    private static ImagePreview Sized(double width, double height)
    {
        var preview = new ImagePreview();
        preview.Measure(new Size(width, height));
        preview.Arrange(new Rect(0, 0, width, height));
        return preview;
    }

    // O que abre como imagem

    [Theory]
    [InlineData("a.png")]
    [InlineData("a.PNG")]
    [InlineData("a.jpg")]
    [InlineData("a.jpeg")]
    [InlineData("a.jfif")]
    [InlineData("a.gif")]
    [InlineData("a.webp")]
    [InlineData("a.bmp")]
    [InlineData("a.ico")]
    public void Extensoes_de_imagem_sao_reconhecidas(string name) => Assert.True(ImageFile.IsImage(name));

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("a.txt")]
    [InlineData("a.pdf")]
    [InlineData("png")]
    [InlineData("a.png.txt")]
    public void Outros_arquivos_nao_abrem_como_imagem(string name) => Assert.False(ImageFile.IsImage(name));

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Tamanho_vem_do_cabecalho(SKEncodedImageFormat format)
    {
        var info = ImageFile.Inspect(Encoded(format))!;

        Assert.Equal(40, info.Width);
        Assert.Equal(20, info.Height);
        Assert.Equal(SKEncodedOrigin.TopLeft, info.Origin);
    }

    [Fact]
    public void Gif_e_bmp_tambem_sao_lidos()
    {
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        Assert.Equal(1, ImageFile.Inspect(gif)!.Width);
        Assert.Equal(300, ImageFile.Inspect(BmpHeader(300, 200))!.Width);
    }

    [Fact]
    public void Foto_deitada_troca_largura_e_altura()
    {
        var info = ImageFile.Inspect(JpegWithOrientation(6))!;

        Assert.Equal(SKEncodedOrigin.RightTop, info.Origin);
        Assert.Equal(40, info.PixelWidth);
        Assert.Equal(20, info.Width);
        Assert.Equal(40, info.Height);
    }

    [Fact]
    public void Transparencia_so_conta_onde_ela_existe()
    {
        Assert.False(ImageFile.Inspect(Encoded(SKEncodedImageFormat.Jpeg))!.HasAlpha);
        Assert.True(ImageFile.Inspect(Encoded(SKEncodedImageFormat.Png, transparent: true))!.HasAlpha);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void Conteudo_que_nao_e_imagem_devolve_nulo(byte[] bytes) => Assert.Null(ImageFile.Inspect(bytes));

    [Fact]
    public void Imagem_gigante_nao_e_decodificada()
    {
        using var temp = new TempFolder();
        var path = WriteImage(temp, "bomba.bmp", BmpHeader(20000, 20000));

        var error = Assert.Throws<ImageFileException>(() => ImageFile.Load(path));
        Assert.Contains("grande demais", error.Message);
    }

    [Fact]
    public void Arquivo_em_uso_por_outro_programa_ainda_abre()
    {
        using var temp = new TempFolder();
        var path = WriteImage(temp, "a.png");

        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(40, ImageFile.Load(path).Info.Width);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536 * 1024, "1,5 MB")]
    [InlineData(2 * 1024 * 1024, "2 MB")]
    public void Tamanho_do_arquivo_fica_legivel(long bytes, string text) => Assert.Equal(text, ImageFile.DescribeSize(bytes));

    // Zoom e posição

    [Theory]
    [InlineData(1, 1, 1.5)]
    [InlineData(1, -1, 0.75)]
    [InlineData(0.8, 1, 1)]
    [InlineData(0.8, -1, 0.75)]
    [InlineData(32, 1, 32)]
    [InlineData(0.05, -1, 0.05)]
    public void Zoom_anda_de_parada_em_parada(double current, int direction, double expected) =>
        Assert.Equal(expected, ImageZoom.Next(current, direction));

    [Theory]
    [InlineData(800, 600, 400, 300, 1)]
    [InlineData(800, 600, 1600, 600, 0.5)]
    [InlineData(800, 600, 800, 1200, 0.5)]
    [InlineData(0, 0, 800, 600, 1)]
    public void Ajuste_so_reduz(double areaWidth, double areaHeight, double width, double height, double expected) =>
        Assert.Equal(expected, ImageZoom.Fit(areaWidth, areaHeight, width, height));

    // Largura 40, altura 20. O canto de cima à esquerda do arquivo vai para onde a orientação manda.
    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 0, 0)]
    [InlineData(SKEncodedOrigin.TopRight, 40, 0)]
    [InlineData(SKEncodedOrigin.BottomRight, 40, 20)]
    [InlineData(SKEncodedOrigin.BottomLeft, 0, 20)]
    [InlineData(SKEncodedOrigin.LeftTop, 0, 0)]
    [InlineData(SKEncodedOrigin.RightTop, 20, 0)]
    [InlineData(SKEncodedOrigin.RightBottom, 20, 40)]
    [InlineData(SKEncodedOrigin.LeftBottom, 0, 40)]
    public void Orientacao_leva_o_canto_para_o_lugar_certo(SKEncodedOrigin origin, double x, double y)
    {
        var point = new Point(0, 0).Transform(ImageZoom.OrientationOf(origin, 40, 20));

        Assert.Equal(new Point(x, y), point);
    }

    [AvaloniaFact]
    public void Imagem_grande_cabe_na_area_e_pequena_fica_no_tamanho_real()
    {
        var preview = Sized(1000, 800);

        preview.Show(StubBitmap(), InfoOf(4000, 1000));
        Assert.True(preview.IsFit);
        Assert.Equal(968.0 / 4000, preview.Scale, 5);
        Assert.False(preview.CanPan);

        preview.Show(StubBitmap(), InfoOf(100, 50));
        Assert.Equal(1, preview.Scale);
    }

    [AvaloniaFact]
    public void Imagem_ajustada_nao_fica_sob_a_capsula()
    {
        var preview = Sized(1000, 800);

        preview.Show(StubBitmap(), InfoOf(1000, 4000));

        Assert.True(preview.ImageRect.Bottom <= 800 - 40 - 12);
        Assert.True(preview.ImageRect.Top >= 0);
    }

    [AvaloniaFact]
    public void Imagem_menor_que_a_area_nao_sai_do_centro()
    {
        var preview = Sized(1000, 800);
        preview.Show(StubBitmap(), InfoOf(100, 100));
        var before = preview.ImageRect;

        preview.PanBy(new Vector(300, 300));

        Assert.Equal(before, preview.ImageRect);
    }

    [AvaloniaFact]
    public void Imagem_aproximada_nao_some_ao_arrastar()
    {
        var preview = Sized(1000, 800);
        preview.Show(StubBitmap(), InfoOf(4000, 3000));
        preview.ShowActualSize();
        Assert.True(preview.CanPan);

        preview.PanBy(new Vector(100000, 100000));

        Assert.True(preview.ImageRect.Left <= 16);
        Assert.True(preview.ImageRect.Top <= 16);
        Assert.True(preview.ImageRect.Right >= 1000 - 16);
    }

    [AvaloniaFact]
    public void Recarregar_mantem_o_zoom_e_outra_imagem_volta_a_caber()
    {
        var preview = Sized(1000, 800);
        preview.Show(StubBitmap(), InfoOf(4000, 3000));
        preview.ShowActualSize();

        preview.Show(StubBitmap(), InfoOf(4000, 3000), keepView: true);
        Assert.False(preview.IsFit);
        Assert.Equal(1, preview.Scale);

        preview.Show(StubBitmap(), InfoOf(4000, 3000));
        Assert.True(preview.IsFit);
    }

    [Theory]
    [InlineData(1000, 500, 1920, null)]
    [InlineData(1920, 1000, 1920, null)]
    [InlineData(6000, 4000, 1920, 1920)]
    [InlineData(4000, 6000, 1920, 1280)]
    public void Reducao_so_vale_para_o_que_nao_cabe_na_tela(int width, int height, int side, int? expected) =>
        Assert.Equal(expected, ImageFile.ReducedWidth(InfoOf(width, height), side));

    [AvaloniaFact]
    public void Zoom_alem_da_versao_reduzida_pede_a_imagem_inteira()
    {
        var preview = Sized(1000, 800);
        var asked = 0;
        preview.NeedsFullImage += () => asked++;

        preview.Show(StubBitmap(), InfoOf(8000, 6000), loadedScale: 0.25);
        Assert.True(preview.IsReduced);
        Assert.Equal(0, asked);

        preview.ZoomStep(1);
        Assert.Equal(0.25, preview.Scale);
        Assert.Equal(0, asked);

        preview.ZoomStep(1);
        Assert.Equal(0.33, preview.Scale);
        Assert.Equal(1, asked);

        preview.Show(StubBitmap(), InfoOf(8000, 6000), keepView: true);
        Assert.False(preview.IsReduced);
        preview.ZoomStep(1);
        Assert.Equal(1, asked);
    }

    [AvaloniaFact]
    public void Sem_memoria_para_a_inteira_nao_tenta_de_novo_a_cada_zoom()
    {
        var preview = Sized(1000, 800);
        var asked = 0;
        preview.NeedsFullImage += () =>
        {
            asked++;
            preview.KeepReducedImage();
        };
        preview.Show(StubBitmap(), InfoOf(8000, 6000), loadedScale: 0.25);

        preview.ZoomStep(1);
        preview.ZoomStep(1);
        preview.ZoomStep(1);

        Assert.Equal(1, asked);
    }

    [AvaloniaFact]
    public void Foto_enorme_abre_reduzida_e_o_zoom_traz_a_inteira()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "enorme.png", Encoded(SKEncodedImageFormat.Png, 16000, 100));
        var window = Open(temp, settings);

        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        var preview = window.ImagePreviewControl;
        Assert.True(preview.IsReduced);
        Assert.Equal(16000, preview.Info!.Width);
        Assert.Contains("16000 x 100", window.CaretPositionText);

        preview.ShowActualSize();
        Dispatcher.UIThread.RunJobs();

        Assert.False(preview.IsReduced);
        Assert.Equal(1, preview.Scale);
        Assert.Contains("100%", window.CaretPositionText);
        window.Close();
    }

    [AvaloniaFact]
    public void Mudar_o_tamanho_da_janela_reajusta_a_imagem()
    {
        var preview = Sized(1000, 800);
        preview.Show(StubBitmap(), InfoOf(4000, 1000));
        var changes = 0;
        preview.ViewChanged += () => changes++;
        var scale = preview.Scale;

        preview.Measure(new Size(500, 800));
        preview.Arrange(new Rect(0, 0, 500, 800));

        Assert.True(preview.Scale < scale);
        Assert.True(changes > 0);
    }

    // A aba na janela

    [AvaloniaFact]
    public void Abrir_imagem_mostra_a_imagem_no_lugar_do_editor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);

        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.ActiveDocument!.IsImage);
        Assert.True(window.IsImageShown);
        Assert.False(window.EditorVisible);
        Assert.Equal("", window.ImageMessageText);
        Assert.Contains("40 x 20", window.CaretPositionText);
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_de_aba_alterna_entre_imagem_e_codigo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var image = WriteImage(temp, "foto.png");
        var code = temp.File("a.txt", "linha");
        var window = Open(temp, settings);

        window.OpenFile(image);
        window.OpenFile(code);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsImageShown);
        Assert.True(window.EditorVisible);
        Assert.StartsWith("Ln ", window.CaretPositionText);
        Assert.Null(window.ImagePreviewControl.Info);

        window.OpenFile(image);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsImageShown);
        Assert.False(window.EditorVisible);
        Assert.Equal(40, window.ImagePreviewControl.Info!.Width);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicar_de_novo_na_mesma_imagem_nao_perde_o_zoom()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        window.ImagePreviewControl.ZoomStep(1);
        var scale = window.ImagePreviewControl.Scale;

        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(scale, window.ImagePreviewControl.Scale);
        window.Close();
    }

    [AvaloniaFact]
    public void Imagem_estragada_abre_a_aba_com_um_aviso()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "ruim.png", [1, 2, 3, 4]);
        var window = Open(temp, settings);

        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsImageShown);
        Assert.Contains("Não foi possível", window.ImageMessageText);
        Assert.Null(window.ImagePreviewControl.Info);
        window.Close();
    }

    [AvaloniaFact]
    public void Imagem_que_nao_existe_nao_abre_aba()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings);

        window.OpenFile(Path.Combine(temp.Path, "sumiu.png"));

        Assert.Empty(window.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public void Svg_continua_abrindo_como_codigo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("logo.svg", "<svg></svg>");
        var window = Open(temp, settings);

        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.ActiveDocument!.IsImage);
        Assert.Equal("<svg></svg>", window.ActiveDocument.Document.Text);
        Assert.True(window.EditorVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Outro_binario_continua_recusado()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "dados.bin", [1, 0, 2]);
        var window = Open(temp, settings);

        window.OpenFile(path);

        Assert.Empty(window.Documents);
        window.Close();
    }

    // Nada pode gravar por cima da imagem

    [AvaloniaFact]
    public async Task Salvar_nunca_grava_por_cima_da_imagem()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var original = File.ReadAllBytes(path);
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        var document = window.ActiveDocument!;
        var asked = false;
        window.PickSavePath = _ =>
        {
            asked = true;
            return Task.FromResult<string?>(Path.Combine(temp.Path, "copia.png"));
        };

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(await window.SaveAsync(document));
        Assert.False(await window.SaveAsAsync(document));
        document.Save();
        document.SaveTo(path);

        Assert.False(asked);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(temp.Path, "copia.png")));
        window.Close();
    }

    [AvaloniaFact]
    public void Digitar_com_a_imagem_aberta_nao_altera_nada()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("abc");
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.ActiveDocument!.IsDirty);
        Assert.Equal("", window.ActiveDocument.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Editor_volta_a_aceitar_texto_depois_da_imagem()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var image = WriteImage(temp, "foto.png");
        var code = temp.File("a.txt", "");
        var window = Open(temp, settings);
        window.OpenFile(image);
        Dispatcher.UIThread.RunJobs();

        window.OpenFile(code);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("abc");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("abc", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Fechar_a_aba_de_imagem_nao_pergunta_nada()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        Assert.True(await window.CloseDocumentAsync(window.ActiveDocument!));
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsImageShown);
        Assert.Null(window.ImagePreviewControl.Info);
        Assert.True(File.Exists(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Substituir_no_projeto_ignora_a_imagem_aberta()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var original = File.ReadAllBytes(path);
        var text = temp.File("a.txt", "foo");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        var document = window.ActiveDocument!;
        window.OpenSearch();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("foo");
        Dispatcher.UIThread.RunJobs();

        var replacing = window.ReplaceAllAsync("bar");
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await replacing;

        Assert.False(document.IsDirty);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("bar", File.ReadAllText(text));
        window.Close();
    }

    // Disco e sessão

    [AvaloniaFact]
    public async Task Imagem_alterada_no_disco_e_recarregada()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        File.WriteAllBytes(path, Encoded(SKEncodedImageFormat.Png, 100, 80));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        await window.CheckOpenDocumentsAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(100, window.ImagePreviewControl.Info!.Width);
        Assert.False(window.IsDialogOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Imagem_apagada_por_fora_nao_manda_salvar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        File.Delete(path);
        await window.CheckOpenDocumentsAsync();

        Assert.DoesNotContain("Salve", window.StatusText);
        Assert.False(File.Exists(path));
        window.Close();
    }

    [AvaloniaFact]
    public void Reabrir_a_pasta_volta_com_a_imagem()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var image = WriteImage(temp, "foto.png");
        var code = temp.File("a.txt", "x");
        var settings = new AppSettings();
        var first = Open(temp, settingsFolder, settings);
        first.OpenFile(code);
        first.OpenFile(image);
        Dispatcher.UIThread.RunJobs();
        first.Close();

        var second = Open(temp, settingsFolder, settings);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, second.Documents.Count);
        Assert.True(second.ActiveDocument!.IsImage);
        Assert.True(second.IsImageShown);
        Assert.Equal(40, second.ImagePreviewControl.Info!.Width);
        second.Close();
    }

    // Teclado e comandos

    [AvaloniaFact]
    public void Ctrl_mais_aproxima_a_imagem_sem_mudar_a_fonte_do_editor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        var font = window.EditorFontSize;

        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1.5, window.ImagePreviewControl.Scale);
        Assert.Contains("150%", window.CaretPositionText);

        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.ImagePreviewControl.IsFit);

        Assert.Equal(font, window.EditorFontSize);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_mais_continua_mudando_a_fonte_no_codigo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "x");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        var font = window.EditorFontSize;

        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(font + 1, window.EditorFontSize);
        window.Close();
    }

    [AvaloniaFact]
    public void Barra_de_comando_so_oferece_o_que_vale_para_imagem()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png");
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        var commands = window.PaletteCommandNames("").ToList();

        Assert.Contains("Aproximar imagem", commands);
        Assert.Contains("Fechar aba", commands);
        Assert.Contains("Renomear arquivo", commands);
        Assert.DoesNotContain("Salvar arquivo", commands);
        Assert.DoesNotContain("Salvar como", commands);
        Assert.DoesNotContain("Procurar no arquivo", commands);
        Assert.DoesNotContain("Ir para a definição", commands);
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_de_tema_com_a_imagem_aberta_nao_quebra()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = WriteImage(temp, "foto.png", Encoded(SKEncodedImageFormat.Png, transparent: true));
        var window = Open(temp, settings);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        var theme = Palette.Current.Name;

        window.ToggleTheme();
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(theme, Palette.Current.Name);

        window.ToggleTheme();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsImageShown);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Comparar_imagem_nova_com_a_aba_aberta_avisa_que_e_binaria()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        await git.RunAsync(["commit", "-m", "base", "--allow-empty"]);
        var path = WriteImage(temp, "foto.png");
        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.GitEnvironment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(config.Path, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.RefreshGitAsync();
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        await window.OpenDiffAsync(window.GitRows.Single().Change);

        Assert.False(window.IsDiffOpen);
        Assert.True(window.IsImageShown);
        Assert.Contains("binário", window.GitStatusMessage);
        window.Close();
    }
}
