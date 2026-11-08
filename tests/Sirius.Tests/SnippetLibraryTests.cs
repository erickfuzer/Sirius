using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

// Os atalhos de fábrica do VS Code, como foreach no PHP e log no JavaScript, e as sugestões que a Sirius
// mostra sozinha na lista: atalhos, Emmet de HTML e Emmet de CSS.
public class SnippetLibraryTests
{
    [Theory]
    [InlineData("php", "foreach")]
    [InlineData("php", "keyval")]
    [InlineData("php", "kvp")]
    [InlineData("javascript", "log")]
    [InlineData("javascript", "forof")]
    [InlineData("typescript", "ctor")]
    [InlineData("markdown", "bold")]
    [InlineData("csharp", "#region")]
    [InlineData("c", "#region")]
    [InlineData("cpp", "#endregion")]
    public void Atalhos_de_cada_linguagem(string language, string prefix) =>
        Assert.Contains(SnippetLibrary.For(language), entry => entry.Prefix == prefix);

    [Theory]
    [InlineData("php")]
    [InlineData("javascript")]
    [InlineData("typescript")]
    [InlineData("markdown")]
    [InlineData("csharp")]
    [InlineData("c")]
    [InlineData("cpp")]
    public void Todos_os_corpos_viram_texto(string language)
    {
        Assert.NotEmpty(SnippetLibrary.For(language));
        foreach (var entry in SnippetLibrary.For(language))
            // O "quote" do Markdown só transforma o texto selecionado: sem seleção, é vazio mesmo.
            Assert.True(Snippet.Parse(entry.Body).Text.Length > 0 || entry.Body.StartsWith("${1:${TM_SELECTED_TEXT/"), entry.Prefix);
    }

    [Fact]
    public void Linguagem_sem_atalhos_fica_vazia() => Assert.Empty(SnippetLibrary.For("python"));

    [Theory]
    [InlineData("a.php", "<?php\n|", "php")]
    [InlineData("a.php", "<p>|", null)]
    [InlineData("a.php", "<script>|", "javascript")]
    [InlineData("a.html", "<script>\n|", "javascript")]
    [InlineData("a.html", "<p>|", null)]
    [InlineData("a.js", "|", "javascript")]
    [InlineData("a.jsx", "|", "javascript")]
    [InlineData("a.ts", "|", "typescript")]
    [InlineData("a.md", "|", "markdown")]
    [InlineData("a.cs", "|", "csharp")]
    [InlineData("a.h", "|", "c")]
    [InlineData("a.cpp", "|", "cpp")]
    [InlineData("a.css", "|", null)]
    [InlineData("a.py", "|", null)]
    public void Linguagem_no_cursor(string file, string marked, string? expected)
    {
        var caret = marked.IndexOf('|');
        Assert.Equal(expected, SnippetLibrary.LanguageAt(file, marked.Remove(caret, 1), caret));
    }

    [Theory]
    [InlineData("foreach", "fe", true)]
    [InlineData("foreach", "FOR", true)]
    [InlineData("foreach", "ef", false)]
    [InlineData("foreach", "oreach", false)]
    [InlineData("if", "", true)]
    public void Letras_em_ordem_como_no_vs_code(string prefix, string word, bool matches) =>
        Assert.Equal(matches, SnippetLibrary.Matches(prefix, word));

    [Fact]
    public void Os_que_comecam_pela_palavra_vem_primeiro()
    {
        var found = SnippetLibrary.Matching("php", "fo").Select(entry => entry.Prefix).ToList();
        Assert.Equal("for", found[0]);
        Assert.Contains("foreach", found);
    }

    [Theory]
    [InlineData("  fo|", 2, "fo")]
    [InlineData("#reg|", 0, "#reg")]
    [InlineData("|", 0, "")]
    public void Palavra_antes_do_cursor(string marked, int start, string word)
    {
        var caret = marked.IndexOf('|');
        Assert.Equal((start, word), SnippetLibrary.WordBefore(marked.Remove(caret, 1), caret));
    }

    [Theory]
    [InlineData("$fo|")]
    [InlineData("x.fo|")]
    [InlineData("$x->fo|")]
    [InlineData("A::fo|")]
    public void Nome_do_codigo_nao_e_atalho(string marked)
    {
        var caret = marked.IndexOf('|');
        Assert.Null(SnippetLibrary.WordBefore(marked.Remove(caret, 1), caret));
    }

    // Na janela

    private static MainWindow Open(TempFolder temp, TempFolder settings, string file, string text)
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
    public void Atalho_do_php_aparece_na_lista_e_entra_com_os_campos()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php", "<?php\n");

        Type(window, "forea");
        Assert.True(window.IsCompletionOpen);
        Assert.Equal("foreach", window.CompletionLabels.First());
        Assert.True(window.AcceptCompletion("foreach"));
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("<?php\nforeach ($variable as $key => $value) {", Text(window));
        // O primeiro campo entra selecionado: digitar troca o nome.
        window.KeyTextInput("lista");
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("<?php\nforeach ($lista as", Text(window));
        Assert.True(window.IsInSnippet);
        window.Close();
    }

    [AvaloniaFact]
    public void Atalho_nao_aparece_dentro_de_texto_entre_aspas()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.js", "const x = \"");

        Type(window, "log");

        Assert.False(window.IsCompletionOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Javascript_sugere_log()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.js", "");

        Type(window, "log");
        Assert.True(window.AcceptCompletion("log"));
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("console.log();", Text(window));
        Assert.Equal("console.log(".Length, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Markdown_so_sugere_quando_pedido()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.md", "");

        Type(window, "bold");
        Assert.False(window.IsCompletionOpen);

        await window.RequestCompletionAsync(explicitRequest: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("bold", window.CompletionLabels);
        window.Close();
    }

    // Como no VS Code: seleciona as linhas de baixo para cima, Ctrl Espaço e quote, e cada linha ganha "> ".
    // Com o cursor no fim, a lista filtra pela palavra "dois", lá e aqui.
    [AvaloniaFact]
    public async Task Quote_do_markdown_cita_as_linhas_selecionadas()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.md", "linha um\nlinha dois");
        window.SelectForTests("linha um\nlinha dois".Length, 0);
        Dispatcher.UIThread.RunJobs();

        await window.RequestCompletionAsync(explicitRequest: true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.AcceptCompletion("quote"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("> linha um\n> linha dois", Text(window).Replace("\r\n", "\n"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pedir_sem_palavra_mostra_todos_os_atalhos()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.ts", "");

        await window.RequestCompletionAsync(explicitRequest: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SnippetLibrary.For("typescript").Count, window.CompletionLabels.Count());
        window.Close();
    }


    [AvaloniaFact]
    public void Ctrl_espaco_no_markdown_mostra_os_atalhos()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.md", "bol");

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("bold", window.CompletionLabels);
        Assert.Equal("bol", Text(window));
        window.Close();
    }
    [AvaloniaFact]
    public void Emmet_de_css_mostra_o_resultado_e_entra_pela_lista()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a {\n  ");

        Type(window, "m10");
        Assert.True(window.IsCompletionOpen);
        Assert.Equal(["m10"], window.CompletionLabels);
        Assert.True(window.AcceptCompletion("m10"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a {\n  margin: 10px;", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Emmet_de_css_no_atributo_style()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "<div style=\"\"></div>");
        window.SetCaretOffset("<div style=\"".Length);
        Dispatcher.UIThread.RunJobs();

        Type(window, "m10");
        Assert.True(window.IsCompletionOpen);
        Assert.Equal(["m10"], window.CompletionLabels);
        Assert.True(window.AcceptCompletion("m10"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("<div style=\"margin: 10px;\"></div>", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Emmet_de_css_no_sass_sai_sem_ponto_e_virgula()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.sass", "nav\n  ");

        Type(window, "m10");
        Assert.Equal(["m10"], window.CompletionLabels);
        Assert.True(window.AcceptCompletion("m10"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("nav\n  margin: 10px", Text(window));
        window.Close();
    }

    // Sem servidor para o Sass, a lista só teria o Emmet, e o Enter trocaria o seletor por uma propriedade.
    [AvaloniaFact]
    public void Seletor_aninhado_no_sass_nao_abre_a_lista()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.sass", "nav\n  ");

        Type(window, "li");
        Assert.False(window.IsCompletionOpen);
        Assert.Equal("nav\n  li", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Emmet_de_css_com_opcoes_seleciona_a_primeira()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "a {\n  ");

        Type(window, "d");
        Assert.True(window.AcceptCompletion("d"));
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("grid");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a {\n  display: grid;", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Chave_da_tabela_do_html_aparece_e_entra()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "");

        Type(window, "a:");
        Assert.Equal(["a:blank", "a:link", "a:mail", "a:tel"], window.CompletionLabels);
        Assert.True(window.AcceptCompletion("a:link"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("<a href=\"http://\"></a>", Text(window));
        Assert.Equal("<a href=\"http://".Length, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public void Tres_exclamacoes_viram_o_doctype()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "");

        Type(window, "!");
        Assert.True(window.AcceptCompletion("!!!"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("<!DOCTYPE html>", Text(window));
        window.Close();
    }
}
