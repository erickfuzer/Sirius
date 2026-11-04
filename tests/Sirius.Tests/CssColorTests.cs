using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius.Tests;

public class CssColorTests
{
    private static List<string> Texts(string text, string file = "a.css") =>
        CssColors.Find(text, file).Select(match => text.Substring(match.Start, match.Length)).ToList();

    private static CssColor One(string value) =>
        Assert.Single(CssColors.Find($"a {{ color: {value}; }}", "a.css")).Color;

    // Onde é cor

    [Fact]
    public void Acha_os_formatos_de_cor_em_um_valor() =>
        Assert.Equal(["#fff", "#336699", "#33669980", "rgb(1, 2, 3)", "rgba(1,2,3,.5)", "hsl(120, 100%, 50%)", "red"],
            Texts("a { b: #fff; c: #336699; d: #33669980; e: rgb(1, 2, 3); f: rgba(1,2,3,.5); g: hsl(120, 100%, 50%); h: red; }"));

    [Fact]
    public void Acha_varias_cores_no_mesmo_valor() =>
        Assert.Equal(["red", "#00f"], Texts("a { background: linear-gradient(red, #00f); }"));

    [Fact]
    public void Ultima_declaracao_sem_ponto_e_virgula_conta() =>
        Assert.Equal(["#abc"], Texts("a { color: #abc }"));

    [Fact]
    public void Variaveis_de_css_scss_e_less_contam()
    {
        Assert.Equal(["#111"], Texts(":root { --cor: #111; }"));
        Assert.Equal(["#222"], Texts("$cor: #222;", "a.scss"));
        Assert.Equal(["#333"], Texts("@cor: #333;", "a.less"));
    }

    [Fact]
    public void Sass_termina_a_declaracao_na_quebra_de_linha() =>
        Assert.Equal(["#111", "#222"], Texts("a\n  color: #111\n  background: #222\n", "a.sass"));

    // Onde parece, mas não é

    [Theory]
    [InlineData("#add { margin: 0; }")]
    [InlineData("a:hover #fff { margin: 0; }")]
    [InlineData("@media (min-width: 100px) { }")]
    [InlineData(".red { margin: 0; }")]
    [InlineData("/* color: #fff; */")]
    [InlineData("a { background: url(#fff); }")]
    [InlineData("a { background: url(red.png); }")]
    [InlineData("a { content: 'color: #fff; red'; }")]
    [InlineData("a { color: var(--red); }")]
    [InlineData("a { color: $red; }")]
    [InlineData("a { color: red-500; }")]
    [InlineData("a { color: #ab; }")]
    [InlineData("a { color: #abcde; }")]
    [InlineData("a { color: #abcxyz; }")]
    [InlineData("a { color: rgb(var(--r), 0); }")]
    [InlineData("a { color: rgb(var(--r), var(--g), var(--b)); }")]
    [InlineData("a { font-family: Red, Tan; }")]
    [InlineData("a { animation-name: tomato; }")]
    [InlineData("a { color: #{$cor}; }")]
    public void Nao_marca_o_que_nao_e_cor(string css) => Assert.Empty(Texts(css, "a.scss"));

    [Fact]
    public void Comentario_de_linha_so_vale_onde_existe()
    {
        Assert.Empty(Texts("// color: #fff;\n", "a.scss"));
        Assert.Equal(["#222"], Texts("// a: #111;\nb: #222;", "a.scss"));
    }

    [Fact]
    public void Tipos_de_arquivo_sem_css_nao_tem_cor()
    {
        Assert.False(CssColors.Supports("a.txt"));
        Assert.False(CssColors.Supports("a.js"));
        Assert.False(CssColors.Supports(null));
        Assert.Empty(Texts("a { color: #fff; }", "a.txt"));
    }

    [Fact]
    public void Arquivo_gigante_e_ignorado() =>
        Assert.Empty(CssColors.Find(new string(' ', 2_000_001) + "a { color: red; }", "a.css"));

    // HTML e PHP

    [Fact]
    public void Em_html_vale_so_dentro_de_style()
    {
        const string Html = """
            <a href="#add" class="red">red #fff</a>
            <style>
              .a { color: #111; }
            </style>
            <div style="color: #222; background: tomato"></div>
            <p title="color: #333">#444</p>
            <style media="print">b { color: #555 }</style>
            """;
        Assert.Equal(["#111", "#222", "tomato", "#555"], Texts(Html, "a.html"));
    }

    [Fact]
    public void Php_ignora_o_codigo_php_dentro_do_estilo()
    {
        const string Php = """
            <?php $cor = '#999'; $style = "color: #888"; ?>
            <style>
              .a { color: <?= $x ? '#777' : '#666' ?>; background: #111; }
            </style>
            <div style='color: #222'></div>
            """;
        Assert.Equal(["#111", "#222"], Texts(Php, "a.php"));
    }

    [Fact]
    public void Resultados_saem_em_ordem_no_arquivo() =>
        Assert.Equal(["#111", "#222", "#333"], Texts("<i style=\"color:#111\"></i><style>a{color:#222}</style><i style=\"color:#333\"></i>", "a.html"));

    // Valores

    [Theory]
    [InlineData("#f00", 255, 0, 0, 1)]
    [InlineData("#F00", 255, 0, 0, 1)]
    [InlineData("#0f08", 0, 255, 0, 0.53)]
    [InlineData("#336699", 51, 102, 153, 1)]
    [InlineData("#33669980", 51, 102, 153, 0.5)]
    [InlineData("rgb(1, 2, 3)", 1, 2, 3, 1)]
    [InlineData("rgb(1 2 3 / 50%)", 1, 2, 3, 0.5)]
    [InlineData("rgba(1, 2, 3, .25)", 1, 2, 3, 0.25)]
    [InlineData("rgb(100%, 0%, 0%)", 255, 0, 0, 1)]
    [InlineData("rgb(300, -5, 0)", 255, 0, 0, 1)]
    [InlineData("hsl(120, 100%, 50%)", 0, 255, 0, 1)]
    [InlineData("hsla(240deg 100% 50% / 0.5)", 0, 0, 255, 0.5)]
    [InlineData("rebeccapurple", 102, 51, 153, 1)]
    [InlineData("RED", 255, 0, 0, 1)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void Le_o_valor_certo(string value, int r, int g, int b, double a)
    {
        var color = One(value);
        Assert.Equal((r, g, b), (color.R, color.G, color.B));
        Assert.Equal(a, color.A, 2);
    }

    [Fact]
    public void Tem_todas_as_cores_com_nome() =>
        Assert.Equal(new CssColor(0x9A, 0xCD, 0x32), One("yellowgreen"));

    // Escrita

    [Theory]
    [InlineData(CssColorFormat.Hex, 1, "#336699")]
    [InlineData(CssColorFormat.Hex, 0.5, "#33669980")]
    [InlineData(CssColorFormat.Rgb, 1, "rgb(51, 102, 153)")]
    [InlineData(CssColorFormat.Rgb, 0.5, "rgba(51, 102, 153, 0.5)")]
    [InlineData(CssColorFormat.Hsl, 1, "hsl(210, 50%, 40%)")]
    [InlineData(CssColorFormat.Hsl, 0.25, "hsla(210, 50%, 40%, 0.25)")]
    public void Escreve_em_cada_formato(CssColorFormat format, double alpha, string expected) =>
        Assert.Equal(expected, CssColors.Format(new CssColor(51, 102, 153, alpha), format));

    [Fact]
    public void Numero_decimal_usa_ponto_mesmo_em_portugues()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
        try
        {
            Assert.Equal("rgba(1, 2, 3, 0.5)", CssColors.Format(new CssColor(1, 2, 3, 0.5), CssColorFormat.Rgb));
            Assert.Equal(0.5, One("rgba(1, 2, 3, 0.5)").A);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Conversao_de_cor_nao_perde_valor()
    {
        foreach (var color in new[] { new CssColor(51, 102, 153), new CssColor(0, 0, 0), new CssColor(255, 255, 255), new CssColor(200, 10, 90), new CssColor(128, 128, 128) })
        {
            var (hue, saturation, value) = color.ToHsv();
            Assert.Equal(color, CssColor.FromHsv(hue, saturation, value));
            var (h, s, l) = color.ToHsl();
            Assert.Equal(color, CssColor.FromHsl(h, s, l));
        }
    }

    [Fact]
    public void Formatos_se_alternam_e_nome_vira_hexadecimal()
    {
        Assert.Equal(CssColorFormat.Rgb, CssColors.NextFormat(CssColorFormat.Hex));
        Assert.Equal(CssColorFormat.Hsl, CssColors.NextFormat(CssColorFormat.Rgb));
        Assert.Equal(CssColorFormat.Hex, CssColors.NextFormat(CssColorFormat.Hsl));
        Assert.Equal(CssColorFormat.Hex, CssColors.NextFormat(CssColorFormat.Named));
    }

    // No editor

    private static MainWindow Open(TempFolder temp, TempFolder settings, string file, string text)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(settings.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File(file, text));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Text(MainWindow window) => window.ActiveDocument!.Document.Text;

    [AvaloniaFact]
    public void Cores_acompanham_o_que_e_digitado()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #fff; }");
        Assert.Single(window.ActiveColorMatches);

        window.ActiveDocument!.Document.Insert(0, "b { color: red; }\n");

        Assert.Equal(2, window.ActiveColorMatches.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Arquivo_que_nao_e_css_nao_tem_cores()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.txt", "a { color: #fff; }");
        Assert.Empty(window.ActiveColorMatches);
        window.Close();
    }

    [AvaloniaFact]
    public void Seletor_troca_a_cor_no_texto_e_e_um_so_desfazer()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        const string Original = "a { color: #ff0000; margin: 0; }";
        var window = Open(temp, settings, "a.css", Original);

        window.OpenColorPicker(Original.IndexOf('#'));
        Assert.True(window.IsColorPickerOpen);
        Assert.Equal("#ff0000", window.PickedColorText);

        window.SetPickedColor(120, 1, 1, 1);
        window.SetPickedColor(240, 1, 1, 1);
        Assert.Equal("a { color: #0000ff; margin: 0; }", Text(window));
        Assert.Equal("#0000ff", window.PickedColorText);

        window.CloseColorPicker();
        Assert.False(window.IsColorPickerOpen);

        window.ActiveDocument!.Document.UndoStack.Undo();
        Assert.Equal(Original, Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Abrir_e_fechar_sem_mexer_nao_altera_o_arquivo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        const string Original = "a { color: #F00; background: tomato; }";
        var window = Open(temp, settings, "a.css", Original);

        foreach (var match in window.ActiveColorMatches.ToList())
        {
            window.OpenColorPicker(match.Start);
            window.CloseColorPicker();
        }

        Assert.Equal(Original, Text(window));
        Assert.False(window.ActiveDocument!.IsDirty);
        Assert.False(window.ActiveDocument.Document.UndoStack.CanUndo);
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_o_formato_reescreve_a_cor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #336699; }");
        window.OpenColorPicker(11);

        window.CycleColorFormat();
        Assert.Equal("a { color: rgb(51, 102, 153); }", Text(window));
        window.CycleColorFormat();
        Assert.Equal("a { color: hsl(210, 50%, 40%); }", Text(window));
        window.CycleColorFormat();
        Assert.Equal("a { color: #336699; }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Nome_de_cor_vira_hexadecimal_ao_mudar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: red; }");
        window.OpenColorPicker(11);
        Assert.Equal("red", window.PickedColorText);

        window.SetPickedColor(240, 1, 1, 1);

        Assert.Equal("a { color: #0000ff; }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Transparencia_aparece_no_formato()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: rgb(0, 0, 255); }");
        window.OpenColorPicker(11);

        window.SetPickedColor(240, 1, 1, 0.5);

        Assert.Equal("a { color: rgba(0, 0, 255, 0.5); }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Digitar_fecha_o_seletor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #fff; }");
        window.OpenColorPicker(11);

        window.ActiveDocument!.Document.Insert(0, "x");

        Assert.False(window.IsColorPickerOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Esc_fecha_o_seletor_sem_fechar_outra_coisa()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #fff; }");
        window.OpenColorPicker(11);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsColorPickerOpen);
        Assert.NotNull(window.ActiveDocument);
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_de_aba_fecha_o_seletor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #fff; }");
        window.OpenColorPicker(11);

        window.OpenFile(temp.File("b.css", "b { color: red; }"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsColorPickerOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Offset_sem_cor_nao_abre_o_seletor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #fff; }");

        window.OpenColorPicker(0);

        Assert.False(window.IsColorPickerOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Setas_passam_pelo_quadradinho_sem_parar_duas_vezes()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        const string Css = "a{b:#fff;}";
        var window = Open(temp, settings, "a.css", Css);
        var hash = Css.IndexOf('#');
        window.GoToLocation(window.ActiveDocument!.FullPath, 1, hash);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(hash - 1, window.CaretOffset);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(hash, window.CaretOffset);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(hash + 1, window.CaretOffset);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(hash, window.CaretOffset);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(hash - 1, window.CaretOffset);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(13.5)]
    [InlineData(9)]
    [InlineData(24)]
    public void Quadradinho_nao_muda_a_altura_da_linha(double fontSize)
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a {\n  margin: 0;\n  color: #336699;\n}");
        window.SetEditorFontSize(fontSize);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(window.EditorLineHeight(2), window.EditorLineHeight(3), 2);
        window.Close();
    }

    [AvaloniaFact]
    public void Backspace_e_delete_apagam_de_primeira_junto_do_quadradinho()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        const string Css = "a{b:#fff;}";
        var window = Open(temp, settings, "a.css", Css);
        var hash = Css.IndexOf('#');

        // Chega pela direita e pela esquerda, as duas posições que a tecla poderia encontrar.
        window.GoToLocation(window.ActiveDocument!.FullPath, 1, hash + 1);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a{b#fff;}", Text(window));

        window.ActiveDocument.Document.UndoStack.Undo();
        Dispatcher.UIThread.RunJobs();
        window.GoToLocation(window.ActiveDocument.FullPath, 1, hash + 1);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a{b:fff;}", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Clicar_no_quadradinho_abre_e_clicar_fora_fecha()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #336699; }\n\n\n\n\n");
        var swatch = window.SwatchPointInWindow(11)!.Value;

        window.MouseDown(swatch, MouseButton.Left);
        window.MouseUp(swatch, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsColorPickerOpen);
        Assert.Equal("#336699", window.PickedColorText);

        var elsewhere = new Point(swatch.X + 500, swatch.Y + 300);
        window.MouseDown(elsewhere, MouseButton.Left);
        window.MouseUp(elsewhere, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsColorPickerOpen);
        Assert.Equal("a { color: #336699; }\n\n\n\n\n", Text(window));
        window.Close();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drag(Window window, Control control, double fromX, double fromY, double toX, double toY)
    {
        Point At(double x, double y) => control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), window)!.Value;

        window.MouseDown(At(fromX, fromY), MouseButton.Left);
        Settle();
        window.MouseMove(At((fromX + toX) / 2, (fromY + toY) / 2));
        Settle();
        window.MouseMove(At(toX, toY));
        Settle();
        window.MouseUp(At(toX, toY), MouseButton.Left);
        Settle();
    }

    [AvaloniaFact]
    public void Arrastar_no_seletor_muda_a_cor_e_e_um_so_desfazer()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        const string Original = "a { color: #ff0000; }\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n";
        var window = Open(temp, settings, "a.css", Original);
        window.OpenColorPicker(11);
        Settle();

        // Canto de baixo do quadro: sem brilho, a cor é preta, em qualquer tom.
        Drag(window, window.FindControl<ColorSquare>("ColorSquareView")!, 0.5, 0.5, 1, 1);
        Assert.True(window.IsColorPickerOpen);
        Assert.Equal("#000000", window.PickedColorText);

        // Canto de cima, à direita: cor pura.
        Drag(window, window.FindControl<ColorSquare>("ColorSquareView")!, 0.5, 0.5, 1, 0);
        Assert.Equal("#ff0000", window.PickedColorText);

        // O meio da barra de tom é ciano.
        Drag(window, window.FindControl<ColorStrip>("HueStrip")!, 0.1, 0.5, 0.5, 0.5);
        Assert.Equal("#00ffff", window.PickedColorText);

        // Metade da barra de transparência.
        Drag(window, window.FindControl<ColorStrip>("AlphaStrip")!, 0.9, 0.5, 0.5, 0.5);
        Assert.Equal("#00ffff80", window.PickedColorText);
        Assert.StartsWith("a { color: #00ffff80; }", Text(window));

        window.CloseColorPicker();
        window.ActiveDocument!.Document.UndoStack.Undo();
        Assert.Equal(Original, Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Clicar_no_texto_da_cor_nao_abre_o_seletor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a { color: #336699; }");
        var swatch = window.SwatchPointInWindow(11)!.Value;

        var onText = new Point(swatch.X + 40, swatch.Y);
        window.MouseDown(onText, MouseButton.Left);
        window.MouseUp(onText, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsColorPickerOpen);
        window.Close();
    }
}
