using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Lsp;
using Xunit;

namespace Sirius.Tests;

public class LineEditingTests
{
    private static void Toggle(TextDocument document, int first, int last, CommentStyle style) =>
        LineEditing.ToggleComment(document, first, last, style);

    [Theory]
    [InlineData("a.php", "<?php\necho 1;", 6, "//")]
    [InlineData("a.php", "<p>oi</p>", 0, "<!--")]
    [InlineData("a.php", "<?php echo 1; ?>\n<p>oi</p>", 17, "<!--")]
    [InlineData("a.php", "<?php", 0, "//")] // A própria linha do <?php.
    [InlineData("a.html", "<style>\na {}\n</style>", 8, "/*")]
    [InlineData("a.html", "<script>\nx()\n</script>", 9, "//")]
    [InlineData("a.css", "a {}", 0, "/*")]
    [InlineData("a.scss", "a {}", 0, "//")]
    [InlineData("a.js", "x", 0, "//")]
    [InlineData("a.py", "x", 0, "#")]
    [InlineData("a.sql", "x", 0, "--")]
    public void Escolhe_o_comentario_de_cada_trecho(string name, string text, int offset, string expected)
    {
        var style = LineEditing.CommentStyleAt(name, text, offset);
        Assert.Equal(expected, style?.Line ?? style?.BlockStart);
    }

    [Fact]
    public void Texto_puro_nao_tem_comentario() => Assert.Null(LineEditing.CommentStyleAt("a.txt", "x", 0));

    [Fact]
    public void Comenta_no_menor_recuo_e_pula_linhas_vazias()
    {
        var document = new TextDocument("    if (a) {\n\n        b();\n    }");

        Toggle(document, 1, 4, CommentStyle.Slashes);

        Assert.Equal("    // if (a) {\n\n    //     b();\n    // }", document.Text);
    }

    [Fact]
    public void Descomenta_quando_todas_ja_estao_comentadas()
    {
        var document = new TextDocument("    // if (a) {\n\n    //     b();\n    //}");

        Toggle(document, 1, 4, CommentStyle.Slashes);

        Assert.Equal("    if (a) {\n\n        b();\n    }", document.Text);
    }

    [Fact]
    public void Com_uma_linha_sem_comentario_comenta_todas()
    {
        var document = new TextDocument("// a\nb");

        Toggle(document, 1, 2, CommentStyle.Slashes);

        Assert.Equal("// // a\n// b", document.Text);
    }

    [Fact]
    public void Comentar_e_um_passo_so_de_desfazer()
    {
        var document = new TextDocument("a\nb\nc");

        Toggle(document, 1, 3, CommentStyle.Hash);
        document.UndoStack.Undo();

        Assert.Equal("a\nb\nc", document.Text);
    }

    [Fact]
    public void Html_envolve_e_desembrulha_o_bloco()
    {
        var document = new TextDocument("  <p>\n    oi\n  </p>  ");

        Toggle(document, 1, 3, CommentStyle.Html);
        Assert.Equal("  <!-- <p>\n    oi\n  </p> -->  ", document.Text);

        Toggle(document, 1, 3, CommentStyle.Html);
        Assert.Equal("  <p>\n    oi\n  </p>  ", document.Text);
    }

    [Fact]
    public void Css_desembrulha_comentario_sem_espacos()
    {
        var document = new TextDocument("/*a {}*/");

        Toggle(document, 1, 1, CommentStyle.Css);

        Assert.Equal("a {}", document.Text);
    }

    [Fact]
    public void Linha_vazia_sozinha_recebe_o_comentario()
    {
        var document = new TextDocument("");

        Toggle(document, 1, 1, CommentStyle.Slashes);

        Assert.Equal("// ", document.Text);
    }

    [Fact]
    public void Selecao_que_termina_no_inicio_da_linha_nao_a_inclui()
    {
        var document = new TextDocument("a\nb\nc");

        Assert.Equal((1, 2), LineEditing.LinesOf(document, 0, 4));
        Assert.Equal((1, 3), LineEditing.LinesOf(document, 0, 5));
        Assert.Equal((2, 2), LineEditing.LinesOf(document, 2, 2));
    }

    [Fact]
    public void Duplicar_usa_a_quebra_de_linha_do_arquivo()
    {
        var document = new TextDocument("a\r\nb");

        Assert.Equal(1, LineEditing.Duplicate(document, 2, 2));
        Assert.Equal("a\r\nb\r\nb", document.Text);

        Assert.Equal(2, LineEditing.Duplicate(document, 1, 2));
        Assert.Equal("a\r\nb\r\na\r\nb\r\nb", document.Text);
    }

    [Fact]
    public void Mover_troca_com_a_linha_vizinha()
    {
        var document = new TextDocument("a\nb\nc");

        Assert.True(LineEditing.Move(document, 2, 3, up: true));
        Assert.Equal("b\nc\na", document.Text);

        Assert.True(LineEditing.Move(document, 1, 1, up: false));
        Assert.Equal("c\nb\na", document.Text);
    }

    [Fact]
    public void Mover_na_ponta_nao_faz_nada()
    {
        var document = new TextDocument("a\nb");

        Assert.False(LineEditing.Move(document, 1, 1, up: true));
        Assert.False(LineEditing.Move(document, 2, 2, up: false));
        Assert.Equal("a\nb", document.Text);
    }

    [Fact]
    public void Mover_mantem_o_crlf()
    {
        var document = new TextDocument("a\r\nb\r\n");

        LineEditing.Move(document, 1, 1, up: false);

        Assert.Equal("b\r\na\r\n", document.Text);
    }

    [Fact]
    public void Pastas_recentes_sem_repeticao_e_com_limite()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 10; i++)
            settings.RememberFolder($@"C:\p{i}");
        settings.RememberFolder(@"c:\P5");

        Assert.Equal(AppSettings.RecentFolderLimit, settings.RecentFolders.Count);
        Assert.Equal(@"c:\P5", settings.RecentFolders[0]);
        Assert.Single(settings.RecentFolders, f => f.Equals(@"C:\p5", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\p9", settings.RecentFolders[1]);
    }

    [Theory]
    [InlineData(@"C:\projetos", @"C:\projetos")]
    [InlineData(@"C:\Users\ana\Documents\cursos", @"…\Documents\cursos")]
    [InlineData("", "")]
    public void Caminho_curto_das_pastas_recentes(string folder, string expected) =>
        Assert.Equal(expected, MainWindow.ShortFolder(folder));

    [Fact]
    public void Formatar_sem_trocas_devolve_lista_vazia() =>
        Assert.Empty(LspParsers.TextEditList(System.Text.Json.JsonDocument.Parse("null").RootElement));
}

// Na janela: teclas, barra de comando e preferências.
public class LineEditingWindowTests
{
    private static AvaloniaEdit.TextEditor Editor(MainWindow window) => window.FindControl<AvaloniaEdit.TextEditor>("CodeEditor")!;
    private static MainWindow Open(TempFolder temp, AppSettings? settings = null, string? start = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(temp.Path, "settings.json"), start ?? temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static MainWindow OpenWith(TempFolder temp, string name, string text, int caret)
    {
        var path = temp.File(name, text);
        var window = Open(temp);
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        Editor(window).TextArea.Caret.Offset = caret;
        Editor(window).TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Ctrl_barra_comenta_e_descomenta_no_php()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.php", "<?php\necho 1;\n", 8);

        window.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("<?php\n// echo 1;\n", window.ActiveDocument!.Document.Text);

        window.KeyPressQwerty(PhysicalKey.Slash, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("<?php\necho 1;\n", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Comentar_em_html_usa_a_marca_do_html()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.html", "<p>oi</p>", 2);

        window.ToggleComment();

        Assert.Equal("<!-- <p>oi</p> -->", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Comentar_em_texto_puro_avisa()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.txt", "oi", 0);

        window.ToggleComment();

        Assert.Equal("oi", window.ActiveDocument!.Document.Text);
        Assert.Contains("comentários", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void Alt_seta_move_a_linha_e_o_cursor_vai_junto()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.js", "a\nbc\nd", 3);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a\nd\nbc", window.ActiveDocument!.Document.Text);
        Assert.Equal(3, window.CaretLine);
        Assert.Equal(2, Editor(window).TextArea.Caret.Column);

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("bc\na\nd", window.ActiveDocument!.Document.Text);
        Assert.Equal(1, window.CaretLine);
        window.Close();
    }

    [AvaloniaFact]
    public void Shift_alt_seta_duplica_e_a_selecao_segue_a_copia()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.js", "a\nb\nc", 0);
        Editor(window).Select(0, 3);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a\nb\na\nb\nc", window.ActiveDocument!.Document.Text);
        Assert.Equal("a\nb", window.SelectedText);
        Assert.Equal(3, Editor(window).TextArea.Selection.StartPosition.Line);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_g_leva_a_linha_digitada()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.js", "a\nb\n    c\nd", 0);

        window.KeyPressQwerty(PhysicalKey.G, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsPaletteOpen);
        window.KeyTextInput("3");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Ir para a linha 3"], window.PaletteCommandNames(":3"));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsPaletteOpen);
        Assert.Equal(3, window.CaretLine);
        Assert.Equal(5, Editor(window).TextArea.Caret.Column);
        window.Close();
    }

    [AvaloniaFact]
    public void Linha_alem_do_fim_vai_para_a_ultima()
    {
        using var temp = new TempFolder();
        var window = OpenWith(temp, "a.js", "a\nb", 0);

        Assert.Equal(["Ir para a linha 2"], window.PaletteCommandNames(":99"));
        Assert.Equal(["Digite o número da linha"], window.PaletteCommandNames(":x"));
        window.Close();
    }

    [AvaloniaFact]
    public void Quebra_de_linha_fica_guardada()
    {
        using var temp = new TempFolder();
        var settings = new AppSettings();
        var window = Open(temp, settings);
        Assert.False(window.IsWordWrapOn);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsWordWrapOn);
        window.Close();

        var reopened = Open(temp, AppSettings.Load(Path.Combine(temp.Path, "settings.json")));
        Assert.True(reopened.IsWordWrapOn);
        reopened.Close();
    }

    [AvaloniaFact]
    public void Pastas_recentes_aparecem_na_barra_e_na_tela_inicial()
    {
        using var temp = new TempFolder();
        var first = Directory.CreateDirectory(Path.Combine(temp.Path, "primeira")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(temp.Path, "segunda")).FullName;
        var settings = new AppSettings();
        var window = Open(temp, settings, first);
        window.OpenFolder(second);

        Assert.Equal([second, first], settings.RecentFolders.Take(2));
        window.OpenRecentFolders();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["primeira", "Abrir outra pasta"], window.PaletteCommandNames(""));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(first, window.RootFolder);
        window.Close();

        // Sem pasta aberta, a tela inicial lista as recentes, e a que sumiu do disco fica de fora.
        var missing = Path.Combine(temp.Path, "sumiu");
        settings.RememberFolder(missing);
        settings.LastFolder = null;
        var empty = new MainWindow(settings, Path.Combine(temp.Path, "settings.json"), null);
        empty.Language.IsInstalled = _ => false;
        empty.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(empty.RootFolder);
        Assert.Contains(empty.RecentRows, row => row.FullPath == first);
        Assert.Contains(empty.RecentRows, row => row.FullPath == second);
        Assert.DoesNotContain(empty.RecentRows, row => row.FullPath == missing);
        empty.Close();
    }
}
