using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class AutoPairsTests
{
    [Theory]
    [InlineData("", '(', true)]
    [InlineData("a", '"', false)] // d'água
    [InlineData("\\", '"', false)]
    [InlineData("= ", '"', true)]
    [InlineData("echo \"it ", '\'', false)] // Dentro de um texto já aberto.
    [InlineData("// isto ", '"', false)]
    [InlineData("a = 'x' ", '"', true)]
    [InlineData("<a href=\"http://x\" ", '"', true)] // O // de http:// não é comentário.
    public void Fecha_conforme_o_que_vem_antes(string before, char opener, bool closes) =>
        Assert.Equal(closes, AutoPairs.ShouldClose(before, before.Length, opener));

    [Theory]
    [InlineData("texto", false)]
    [InlineData(" texto", true)]
    [InlineData(";", true)]
    [InlineData(")", true)]
    [InlineData("\n", true)]
    public void Fecha_conforme_o_que_vem_depois(string after, bool closes) =>
        Assert.Equal(closes, AutoPairs.ShouldClose(after, 0, '('));

    [Theory]
    [InlineData("<div>", "</div>")]
    [InlineData("<div class=\"a\">", "</div>")]
    [InlineData("<br>", null)]
    [InlineData("<IMG src=\"a\">", null)]
    [InlineData("<div/>", null)]
    [InlineData("</div>", null)]
    [InlineData("<!DOCTYPE html>", null)]
    [InlineData("<a title=\"x>", null)] // O > ainda é parte do atributo.
    [InlineData("<?php $a = 1; ?>", null)]
    [InlineData("<?php echo '<div>", null)]
    [InlineData("<style>\na<b>", null)]
    [InlineData("<script>if (a<b>", null)]
    [InlineData("<!-- <div>", null)]
    public void Fecha_tag_so_quando_cabe(string text, string? closing) =>
        Assert.Equal(closing, AutoPairs.TagToClose(text, text.Length));

    [Fact]
    public void Nao_fecha_tag_que_ja_tem_fechamento_logo_depois()
    {
        const string Before = "<p>";
        Assert.Null(AutoPairs.TagToClose(Before + "\n</p>", Before.Length));
        Assert.Equal("</p>", AutoPairs.TagToClose(Before + "</div>", Before.Length));
    }

    [Theory]
    [InlineData("a {", "}", true)]
    [InlineData("f(", ")", true)]
    [InlineData("[", "]", true)]
    [InlineData("{", ")", false)]
    [InlineData("<div>", "</div>", true)]
    [InlineData("<b></b>", "</div>", false)]
    [InlineData("<br/>", "</div>", false)]
    [InlineData("a>", "b", false)]
    public void Enter_abre_linha_entre_o_par(string before, string after, bool opens) =>
        Assert.Equal(opens, AutoPairs.OpensOnEnter(before + after, before.Length));

    // Na janela

    private static MainWindow Open(TempFolder temp, TempFolder settings, string file, string text = "")
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(settings.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File(file, text));
        Dispatcher.UIThread.RunJobs();
        window.SetCaretOffset(text.Length);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Text(MainWindow window) => window.ActiveDocument!.Document.Text;

    private static void Type(MainWindow window, string text)
    {
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Press(MainWindow window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Aspas_e_parenteses_vem_em_par_com_o_cursor_no_meio()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php");

        Type(window, "f(\"");

        Assert.Equal("f(\"\")", Text(window));
        Assert.Equal(3, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public void Digitar_o_fechamento_pula_o_que_a_sirius_acrescentou()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php");

        Type(window, "f(\"oi\");");

        Assert.Equal("f(\"oi\");", Text(window));
        Assert.Equal(8, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public void Fechamento_escrito_pela_pessoa_nao_e_pulado()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        const string Before = "f(g(x";
        var window = Open(temp, settings, "a.php", Before + ")");
        window.SetCaretOffset(Before.Length);
        Dispatcher.UIThread.RunJobs();

        Type(window, ")");

        Assert.Equal("f(g(x))", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Apostrofo_no_meio_da_palavra_nao_duplica()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html");

        Type(window, "d'água");

        Assert.Equal("d'água", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Selecao_fica_entre_as_aspas()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php", "nome");
        window.SelectAllForTests();
        Dispatcher.UIThread.RunJobs();

        Type(window, "\"");

        Assert.Equal("\"nome\"", Text(window));
        Assert.Equal("nome", window.SelectedText);
        window.Close();
    }

    [AvaloniaFact]
    public void Backspace_apaga_o_par_vazio_inteiro()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php");
        Type(window, "(");

        Press(window, PhysicalKey.Backspace);

        Assert.Equal("", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Backspace_nao_leva_o_fechamento_escrito_pela_pessoa()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php", "()");
        window.SetCaretOffset(1);
        Dispatcher.UIThread.RunJobs();

        Press(window, PhysicalKey.Backspace);

        Assert.Equal(")", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_entre_chaves_abre_a_linha_do_meio_com_recuo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "\n  a ");
        Type(window, "{");

        Press(window, PhysicalKey.Enter);
        Type(window, "x");

        Assert.Equal("\n  a {\n      x\n  }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_z_desfaz_o_par_de_uma_vez()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php");
        Type(window, "(");

        window.ActiveDocument!.Document.UndoStack.Undo();

        Assert.Equal("", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Tag_se_fecha_com_o_cursor_no_meio()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html");

        Type(window, "<div>");

        Assert.Equal("<div></div>", Text(window));
        Assert.Equal(5, window.CaretOffset);

        Press(window, PhysicalKey.Enter);
        Type(window, "x");
        Assert.Equal("<div>\n    x\n</div>", Text(window).ReplaceLineEndings("\n"));
        window.Close();
    }

    [AvaloniaFact]
    public void Tag_dentro_do_php_e_arquivo_de_texto_ficam_como_digitados()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php", "<?php\necho ");
        Type(window, "$a->b");
        Assert.Equal("<?php\necho $a->b", Text(window));
        window.Close();

        using var other = new TempFolder();
        var notes = Open(temp, other, "n.txt");
        Type(notes, "f(\"<b>");
        Assert.Equal("f(\"<b>", Text(notes));
        notes.Close();
    }

    [AvaloniaFact]
    public void Emmet_continua_funcionando_com_o_fechamento_automatico()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html");

        Type(window, "div{oi");
        Press(window, PhysicalKey.Tab);
        Assert.Equal("<div>oi</div>", Text(window));
        window.Close();

        using var other = new TempFolder();
        var link = Open(temp, other, "b.html");
        Type(link, "a[href=\"x");
        Press(link, PhysicalKey.Tab);
        Assert.Equal("<a href=\"x\"></a>", Text(link));
        link.Close();
    }
}
