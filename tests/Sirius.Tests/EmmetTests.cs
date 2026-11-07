using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class EmmetTests
{
    private static string Out(string abbreviation) => Emmet.Expand(abbreviation)!.Value.Text;

    // Expansão

    [Theory]
    [InlineData("div", "<div></div>")]
    [InlineData(".cartao", "<div class=\"cartao\"></div>")]
    [InlineData("#topo", "<div id=\"topo\"></div>")]
    [InlineData("p.a.b#c", "<p class=\"a b\" id=\"c\"></p>")]
    [InlineData("a", "<a href=\"\"></a>")]
    [InlineData("a.link", "<a href=\"\" class=\"link\"></a>")]
    [InlineData("a[href=#]", "<a href=\"#\"></a>")]
    [InlineData("a[href=\"x y\" target=_blank]", "<a href=\"x y\" target=\"_blank\"></a>")]
    [InlineData("div[data-x]", "<div data-x=\"\"></div>")]
    [InlineData("p{Olá mundo}", "<p>Olá mundo</p>")]
    [InlineData("{só texto}", "só texto")]
    [InlineData("img", "<img src=\"\" alt=\"\">")]
    [InlineData("br", "<br>")]
    [InlineData("input", "<input type=\"text\">")]
    [InlineData("input:email", "<input type=\"email\">")]
    [InlineData("input[type=password]", "<input type=\"password\">")]
    [InlineData("btn", "<button></button>")]
    [InlineData("link:css", "<link rel=\"stylesheet\" href=\"style.css\">")]
    [InlineData("script:src", "<script src=\"\"></script>")]
    [InlineData("meu-componente", "<meu-componente></meu-componente>")]
    public void Elementos_simples(string abbreviation, string expected) => Assert.Equal(expected, Out(abbreviation));

    [Fact]
    public void Dentro_de_e_lista_repetida() =>
        Assert.Equal("<ul>\n    <li></li>\n    <li></li>\n    <li></li>\n</ul>", Out("ul>li*3"));

    [Fact]
    public void Exemplo_do_cartao() =>
        Assert.Equal("<div class=\"cartao\">\n    <h2></h2>\n    <p></p>\n</div>", Out("div.cartao>h2+p"));

    [Fact]
    public void Irmaos_ficam_em_linhas_separadas() =>
        Assert.Equal("<h1></h1>\n<p></p>", Out("h1+p"));

    [Fact]
    public void Subir_um_nivel() =>
        Assert.Equal("<div>\n    <p></p>\n</div>\n<span></span>", Out("div>p^span"));

    [Fact]
    public void Subir_alem_do_topo_para_no_topo() =>
        Assert.Equal("<div>\n    <p></p>\n</div>\n<span></span>", Out("div>p^^^^span"));

    [Fact]
    public void Grupos_repetem_juntos() =>
        Assert.Equal("<dt></dt>\n<dd></dd>\n<dt></dt>\n<dd></dd>", Out("(dt+dd)*2"));

    [Fact]
    public void Grupo_dentro_de_elemento() =>
        Assert.Equal("<div>\n    <header></header>\n    <main></main>\n</div>", Out("div>(header+main)"));

    [Fact]
    public void Numeracao_com_cifrao() =>
        Assert.Equal("<li class=\"item1\">Item 1</li>\n<li class=\"item2\">Item 2</li>", Out("li.item$*2{Item $}"));

    [Fact]
    public void Repeticao_vale_em_qualquer_ordem() =>
        Assert.Equal(Out("li.a*2{x}"), Out("li{x}.a*2"));

    [Fact]
    public void Numeracao_com_zeros() =>
        Assert.Equal("<p id=\"a01\"></p>\n<p id=\"a02\"></p>", Out("p#a$$*2"));

    [Fact]
    public void Numeracao_passa_para_os_filhos() =>
        Assert.Equal("<li><a href=\"\">L1</a></li>\n<li><a href=\"\">L2</a></li>", Out("li*2>a{L$}"));

    [Fact]
    public void Numero_no_nome_da_tag() =>
        Assert.Equal("<h1></h1>\n<h2></h2>", Out("h$*2"));

    [Theory]
    [InlineData("ul>.x", "<ul>\n    <li class=\"x\"></li>\n</ul>")]
    [InlineData("select>.x", "<select name=\"\" id=\"\">\n    <option class=\"x\"></option>\n</select>")]
    [InlineData("tr>.x", "<tr>\n    <td class=\"x\"></td>\n</tr>")]
    [InlineData("span>.x", "<span><span class=\"x\"></span></span>")]
    [InlineData("div>.x", "<div>\n    <div class=\"x\"></div>\n</div>")]
    public void Nome_implicito_segue_o_pai(string abbreviation, string expected) => Assert.Equal(expected, Out(abbreviation));

    [Fact]
    public void Poucos_elementos_em_linha_ficam_juntos() =>
        Assert.Equal("<p><a href=\"\"></a><span></span></p>", Out("p>a+span"));

    [Fact]
    public void Muitos_elementos_em_linha_quebram() =>
        Assert.Equal("<p>\n    <b></b>\n    <b></b>\n    <b></b>\n</p>", Out("p>b*3"));

    [Fact]
    public void Paradas_ficam_nos_campos_vazios()
    {
        var (text, stops) = Emmet.Expand("a+p")!.Value;
        Assert.Equal("<a href=\"\"></a>\n<p></p>", text);
        Assert.Equal([9, 11, 19], stops);
    }

    [Fact]
    public void Texto_preenchido_nao_e_parada()
    {
        var (_, stops) = Emmet.Expand("p{oi}")!.Value;
        Assert.Empty(stops);
    }

    [Fact]
    public void Pagina_inicial()
    {
        var (text, stops) = Emmet.Expand("!")!.Value;

        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"pt-BR\">", text);
        Assert.Contains("    <meta charset=\"UTF-8\">", text);
        Assert.Equal(2, stops.Count);
        Assert.Equal("Documento", text.Substring(stops[0], 9));
        Assert.Equal("</body>", text.Substring(stops[1] + 1, 7));
    }

    [Fact]
    public void Respeita_recuo_e_quebra_de_linha()
    {
        var (text, _) = Emmet.Expand("ul>li", "\t", "\r\n", "  ")!.Value;
        Assert.Equal("<ul>\r\n  \t<li></li>\r\n  </ul>", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Olá")]
    [InlineData("Div")]
    [InlineData("texto")]
    [InlineData("usuario.nome")]
    [InlineData("www.site.com")]
    [InlineData("3")]
    [InlineData("1.5")]
    [InlineData(".5")]
    [InlineData("p.")]
    [InlineData("ul>")]
    [InlineData("ul>li*")]
    [InlineData("ul>li*0")]
    [InlineData("ul>li*x")]
    [InlineData("(a")]
    [InlineData("a)")]
    [InlineData("(a)>b")]
    [InlineData("a[href")]
    [InlineData("a{oi")]
    [InlineData("a b")]
    [InlineData("input:")]
    [InlineData("foo:bar")]
    [InlineData("$a")]
    public void Texto_que_nao_e_abreviacao_e_recusado(string abbreviation) => Assert.Null(Emmet.Expand(abbreviation));

    [Theory]
    [InlineData("div*1001")]
    [InlineData("div*100>p*100")]
    public void Repeticao_exagerada_e_recusada(string abbreviation) => Assert.Null(Emmet.Expand(abbreviation));

    [Fact]
    public void Abreviacao_gigante_e_recusada() =>
        Assert.Null(Emmet.Expand("div" + string.Concat(Enumerable.Repeat(".a", 300))));

    // Onde vale

    private static string? At(string marked, string file = "a.html")
    {
        var caret = marked.IndexOf('|');
        return Emmet.TryExpandAt(marked.Remove(caret, 1), caret, file, "    ", "\n")?.Text;
    }

    [Theory]
    [InlineData("p|", "<p></p>")]
    [InlineData("    ul>li|", "<ul>\n        <li></li>\n    </ul>")]
    [InlineData("<div>p|", "<p></p>")]
    [InlineData("<div>ul>li|", "<ul>\n    <li></li>\n</ul>")]
    [InlineData("</div> p|", "<p></p>")]
    [InlineData("<div>\n    .a|\n</div>", "<div class=\"a\"></div>")]
    [InlineData("<?php echo 1; ?>\np|", "<p></p>")]
    [InlineData("a[href=x y]|", "<a href=\"x\"></a>")]
    [InlineData("p{Olá mundo}|", "<p>Olá mundo</p>")]
    public void Expande_em_html(string marked, string expected)
    {
        if (marked == "a[href=x y]|")
        {
            // Sem aspas, o espaço separa atributos, e "y" vira outro atributo.
            Assert.Equal("<a href=\"x\" y=\"\"></a>", At(marked));
            return;
        }
        Assert.Equal(expected, At(marked));
    }

    [Theory]
    [InlineData("Olá mundo|")]
    [InlineData("<p>Olá p|</p>")]
    [InlineData("texto a|")]
    [InlineData("<?php\n$a = p|")]
    [InlineData("<?php\np|")]
    [InlineData("<?php if ($x) { ?><?php a|")]
    [InlineData("<style>\n  a { color: red; }\n  p|\n</style>")]
    [InlineData("<script>\n  p|\n</script>")]
    [InlineData("<!-- comentário\np|\n-->")]
    [InlineData("<div class=\"p|\">")]
    [InlineData("<div\n  p|")]
    [InlineData("<di|")]
    [InlineData("<p>a</p>|")]
    [InlineData("|")]
    [InlineData("    |")]
    [InlineData("x !|")]
    public void Nao_expande_fora_do_lugar(string marked) => Assert.Null(At(marked));

    [Theory]
    [InlineData("a.php", true)]
    [InlineData("a.html", true)]
    [InlineData("a.htm", true)]
    [InlineData("a.vue", true)]
    [InlineData("a.svelte", true)]
    [InlineData("a.css", false)]
    [InlineData("a.js", false)]
    [InlineData("a.txt", false)]
    public void Tipos_de_arquivo(string file, bool expands) =>
        Assert.Equal(expands, At("p|", file) is not null);

    [Fact]
    public void Pagina_inicial_so_sozinha_na_linha()
    {
        Assert.NotNull(At("!|"));
        Assert.NotNull(At("  !|"));
        Assert.Null(At("<div>!|"));
    }

    [Fact]
    public void Volta_depois_de_fechar_style_e_php()
    {
        Assert.NotNull(At("<style>a{}</style>\np|"));
        Assert.NotNull(At("<script>a()</script>\np|"));
        Assert.NotNull(At("<!-- x -->\np|"));
    }

    // Na janela

    private static MainWindow Open(TempFolder temp, TempFolder settings, string file, string text, int? caret = null)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(settings.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File(file, text));
        Dispatcher.UIThread.RunJobs();
        window.SetCaretOffset(caret ?? text.Length);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Text(MainWindow window) => window.ActiveDocument!.Document.Text;

    private static void Tab(MainWindow window, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(PhysicalKey.Tab, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Exclamacao_dentro_do_php_explica_por_que_nao_expandiu()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.php", "<?php\n!");

        Tab(window);

        Assert.DoesNotContain("DOCTYPE", Text(window));
        Assert.Contains("<?php", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void Exclamacao_em_arquivo_sem_html_diz_onde_funciona()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "!");

        Tab(window);

        Assert.DoesNotContain("DOCTYPE", Text(window));
        Assert.Contains(".html", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void Exclamacao_na_aba_sem_nome_vira_pagina_mas_outras_abreviacoes_nao()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = new MainWindow(new AppSettings(), Path.Combine(settings.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        window.NewUntitled();
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("p");
        Dispatcher.UIThread.RunJobs();
        Tab(window);
        Assert.DoesNotContain("<p>", Text(window));

        window.ActiveDocument!.Document.Text = "!";
        window.SetCaretOffset(1);
        Dispatcher.UIThread.RunJobs();
        Tab(window);
        Assert.Contains("<!DOCTYPE html>", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Tab_expande_e_coloca_o_cursor_no_primeiro_campo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", ".cartao");

        Tab(window);

        Assert.Equal("<div class=\"cartao\"></div>", Text(window));
        Assert.Equal(20, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public void Tab_de_novo_passa_pelos_campos_e_depois_volta_ao_normal()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        // A primeira linha define a quebra de linha do que for escrito.
        var window = Open(temp, settings, "a.html", "\nul>li*2");

        Tab(window);
        Assert.Equal("\n<ul>\n    <li></li>\n    <li></li>\n</ul>", Text(window));
        Assert.Equal(14, window.CaretOffset);
        Assert.Equal(1, window.EmmetStopsLeft);

        // O que é digitado num campo empurra os seguintes.
        window.KeyTextInput("um");
        Dispatcher.UIThread.RunJobs();
        Tab(window);
        Assert.Equal("\n<ul>\n    <li>um</li>\n    <li></li>\n</ul>", Text(window));
        Assert.Equal(30, window.CaretOffset);
        Assert.Equal(0, window.EmmetStopsLeft);

        // Sem mais campos, é o Tab de sempre.
        Tab(window);
        Assert.Contains("<li>    </li>", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Tudo_se_desfaz_com_um_ctrl_z()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "ul>li*3");

        Tab(window);
        window.ActiveDocument!.Document.UndoStack.Undo();

        Assert.Equal("ul>li*3", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Sair_do_trecho_cancela_os_campos()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "x\n\nul>li*2");

        Tab(window);
        window.SetCaretOffset(1);
        Tab(window);

        Assert.Equal(0, window.EmmetStopsLeft);
        Assert.StartsWith("x   \n", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Trocar_de_aba_cancela_os_campos()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "ul>li*2");
        Tab(window);

        window.OpenFile(temp.File("b.html", "abc"));
        Dispatcher.UIThread.RunJobs();
        window.SetCaretOffset(0);
        Tab(window);

        Assert.Equal(0, window.EmmetStopsLeft);
        Assert.Equal("    abc", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Texto_comum_continua_recebendo_tab()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "Olá mundo");

        Tab(window);

        Assert.Equal("Olá mundo   ", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Css_continua_recebendo_tab()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.css", "p");

        Tab(window);

        Assert.Equal("p   ", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Shift_tab_nao_expande()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "p");

        Tab(window, RawInputModifiers.Shift);

        Assert.Equal("p", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Com_selecao_o_tab_recua_e_nao_expande()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "p\nq");
        window.SelectAllForTests();
        Dispatcher.UIThread.RunJobs();

        Tab(window);

        Assert.Equal("    p\n    q", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tab_expande_mesmo_com_a_lista_de_sugestoes_aberta()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var server = new FakeLanguageServer();
        var window = new MainWindow(new AppSettings(), Path.Combine(settings.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => true;
        window.Language.Launcher = (_, _) => server.Connection;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File("a.html", ""));
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (window.Language.ClientForDocument(window.ActiveDocument!) is null)
        {
            Assert.True(DateTime.UtcNow < limit);
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        server.Completion = _ => new { items = new[] { new { label = "ul", insertText = "ul" } } };

        window.ActiveDocument!.Document.Insert(0, "ul>li");
        window.SetCaretOffset(5);
        await window.RequestCompletionAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsCompletionOpen);

        Tab(window);

        Assert.Equal("<ul>\n    <li></li>\n</ul>", Text(window).Replace("\r\n", "\n"));
        Assert.False(window.IsCompletionOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Quebra_de_linha_segue_o_arquivo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings, "a.html", "x\r\nul>li");

        Tab(window);

        Assert.Equal("x\r\n<ul>\r\n    <li></li>\r\n</ul>", Text(window));
        window.Close();
    }
}
