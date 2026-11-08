using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class SnippetTests
{
    private static string Fields(SnippetText snippet) =>
        string.Join(" ", snippet.Stops.OrderBy(s => s.Offset).Select(s => $"{s.Index}:{snippet.Text.Substring(s.Offset, s.Length)}@{s.Offset}"));

    [Fact]
    public void Campo_com_texto_marca_o_trecho_inteiro()
    {
        var snippet = Snippet.Parse("foreach ($${1:variable} as $${2:value}) {\n\t$0\n}", indent: "    ");

        Assert.Equal("foreach ($variable as $value) {\n    \n}", snippet.Text);
        Assert.Equal("1:variable@10 2:value@23 0:@36", Fields(snippet));
        Assert.Equal([1, 2, 0], snippet.Order);
    }

    [Fact]
    public void Campo_repetido_mostra_o_mesmo_texto()
    {
        var snippet = Snippet.Parse("for ($${1:i} = 0; $${1:i} < $2; $${1:i}++)");

        Assert.Equal("for ($i = 0; $i < ; $i++)", snippet.Text);
        Assert.Equal(3, snippet.Stops.Count(s => s.Index == 1));
    }

    [Fact]
    public void Campo_sem_texto_antes_do_que_tem_tambem_e_preenchido()
    {
        Assert.Equal("ab = ab", Snippet.Parse("$1 = ${1:ab}").Text);
    }

    [Fact]
    public void Escolha_entra_com_a_primeira_opcao()
    {
        var snippet = Snippet.Parse("${1|public,private,protected|} function");

        Assert.Equal("public function", snippet.Text);
        Assert.Equal("1:public@0", Fields(snippet));
    }

    [Theory]
    [InlineData("class ${TM_FILENAME_BASE} {}", "class Pagina {}")]
    [InlineData("**${1:${TM_SELECTED_TEXT}}**", "****")]
    [InlineData("${TM_SELECTED_TEXT/^/> /gm}", "")]
    [InlineData("${NADA:padrão}", "padrão")]
    [InlineData("\\$a <=> \\$b", "$a <=> $b")]
    public void Variaveis_e_escapes(string body, string text)
    {
        var parsed = Snippet.Parse(body, name => name == "TM_FILENAME_BASE" ? "Pagina" : null);
        Assert.Equal(text, parsed.Text);
    }

    [Fact]
    public void Linhas_novas_ganham_o_recuo_da_linha_e_o_tab_vira_o_recuo_escolhido()
    {
        var snippet = Snippet.Parse("if (${1:x}) {\n\t$0\n}", indent: "  ", newline: "\r\n", leading: "    ");

        Assert.Equal("if (x) {\r\n      \r\n    }", snippet.Text);
    }
}

// O mecanismo na janela: os campos, o Tab, o Shift Tab, o Esc e o campo repetido.
public class SnippetWindowTests
{
    private static MainWindow Open(TempFolder temp, string file, string text)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File(file, text));
        Dispatcher.UIThread.RunJobs();
        window.SetCaretOffset(text.Length);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Press(MainWindow window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(MainWindow window, string text)
    {
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Text(MainWindow window) => window.ActiveDocument!.Document.Text;

    [AvaloniaFact]
    public void Texto_do_campo_entra_selecionado_e_o_tab_anda_e_volta()
    {
        using var temp = new TempFolder();
        var window = Open(temp, "a.php", "<?php\n");

        window.InsertSnippet(6, 0, Snippet.Parse("foreach ($${1:lista} as $${2:item}) {\n\t$0\n}"));

        Assert.Equal("lista", window.SelectedText);
        Assert.Equal(2, window.SnippetStopsLeft);
        Press(window, PhysicalKey.Tab);
        Assert.Equal("item", window.SelectedText);
        Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Equal("lista", window.SelectedText);

        // Digitar troca o texto selecionado, e o Tab segue para o próximo campo.
        Type(window, "nomes");
        Press(window, PhysicalKey.Tab);
        Type(window, "nome");
        Press(window, PhysicalKey.Tab);

        Assert.StartsWith("<?php\nforeach ($nomes as $nome) {", Text(window));
        Assert.False(window.IsInSnippet);
        window.Close();
    }

    [AvaloniaFact]
    public void Campo_repetido_acompanha_o_que_se_digita()
    {
        using var temp = new TempFolder();
        var window = Open(temp, "a.js", "");

        window.InsertSnippet(0, 0, Snippet.Parse("for (let ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++) {\n\t$0\n}"));
        Type(window, "k");

        Assert.StartsWith("for (let k = 0; k < n; k++) {", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Esc_sai_dos_campos_e_o_tab_volta_ao_normal()
    {
        using var temp = new TempFolder();
        var window = Open(temp, "a.js", "");
        window.InsertSnippet(0, 0, Snippet.Parse("f(${1:a}, ${2:b})"));

        Press(window, PhysicalKey.Escape);

        Assert.False(window.IsInSnippet);
        Assert.Equal("f(a, b)", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Exclamacao_segue_os_campos_do_vs_code()
    {
        using var temp = new TempFolder();
        var window = Open(temp, "index.html", "!");

        window.ExpandEmmet();
        Dispatcher.UIThread.RunJobs();

        // Como no VS Code: a largura e a escala da tela vêm antes do título.
        Assert.Equal("device-width", window.SelectedText);
        Press(window, PhysicalKey.Tab);
        Assert.Equal("1.0", window.SelectedText);
        Press(window, PhysicalKey.Tab);
        Assert.Equal("Documento", window.SelectedText);
        Type(window, "Minha página");
        Assert.Contains("<title>Minha página</title>", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Tudo_se_desfaz_com_um_ctrl_z()
    {
        using var temp = new TempFolder();
        var window = Open(temp, "a.js", "x");

        window.InsertSnippet(0, 1, Snippet.Parse("console.log(${1:x});$0"));
        window.ActiveDocument!.Document.UndoStack.Undo();

        Assert.Equal("x", Text(window));
        window.Close();
    }
}
