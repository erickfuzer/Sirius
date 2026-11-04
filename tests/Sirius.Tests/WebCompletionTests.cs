using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Lsp;

namespace Sirius.Tests;

public class WebCompletionTests
{
    // Catálogo

    [Theory]
    [InlineData("a.html", "html", "html")]
    [InlineData("a.htm", "html", "html")]
    [InlineData("a.css", "css", "css")]
    [InlineData("a.scss", "css", "scss")]
    [InlineData("a.less", "css", "less")]
    public void Html_e_css_tem_servidor(string file, string serverId, string languageId)
    {
        var entry = LanguageServerCatalog.ForPath(file)!.Value;
        Assert.Equal(serverId, entry.Server.Id);
        Assert.Equal(languageId, entry.LanguageId);
    }

    [Theory]
    [InlineData("a.html")]
    [InlineData("a.css")]
    public void Os_dois_servidores_vem_do_mesmo_pacote(string file)
    {
        var server = LanguageServerCatalog.ForPath(file)!.Value.Server;

        Assert.Contains("npm install -g vscode-langservers-extracted", LanguageServerCatalog.InstallCommand(server));
        Assert.Equal(["html", "css"], LanguageServerCatalog.InstalledTogetherWith(server).Select(s => s.Id));
    }

    [Fact]
    public void Outros_servidores_instalam_so_a_si_mesmos()
    {
        var php = LanguageServerCatalog.ForPath("a.php")!.Value.Server;
        Assert.Equal([php], LanguageServerCatalog.InstalledTogetherWith(php));
    }

    // Modelos de texto

    [Theory]
    [InlineData("class=\"$1\"", "class=\"\"", 7)]
    [InlineData("color: $1;", "color: ;", 7)]
    [InlineData("strlen($0)", "strlen()", 7)]
    [InlineData("a($1, $2)$0", "a(, )", 2)]
    [InlineData("a($2, $1)", "a(, )", 4)]
    [InlineData("${1:nome} = 1", "nome = 1", 0)]
    [InlineData("<a href=\"${1:url}\">$0</a>", "<a href=\"url\"></a>", 9)]
    [InlineData("${1:a${2:b}c}", "abc", 0)]
    [InlineData("${1|sim,nao|}", "sim", 0)]
    [InlineData("${1|sim|}", "sim", 0)]
    [InlineData("\\$nome$1", "$nome", 5)]
    [InlineData("a\\}b$0", "a}b", 3)]
    [InlineData("a\\\\b$0", "a\\b", 3)]
    public void Modelo_vira_texto_e_posicao_do_cursor(string snippet, string text, int caret)
    {
        Assert.Equal((text, caret), LspParsers.ExpandSnippet(snippet));
    }

    [Theory]
    [InlineData("sem paradas")]
    [InlineData("custa $ 10")]
    [InlineData("$nome")]
    [InlineData("${nao fecha")]
    public void Texto_sem_parada_fica_igual_e_sem_posicao(string snippet)
    {
        Assert.Equal((snippet, (int?)null), LspParsers.ExpandSnippet(snippet));
    }

    // Respostas

    private static CompletionEntry Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Assert.Single(LspParsers.Completion(document.RootElement));
    }

    [Fact]
    public void Le_o_trecho_a_substituir_e_o_pedido_de_abrir_de_novo()
    {
        var entry = Parse("""
            [{
              "label": "color",
              "insertTextFormat": 2,
              "textEdit": { "newText": "color: $1;", "range": { "start": { "line": 3, "character": 2 }, "end": { "line": 3, "character": 5 } } },
              "command": { "title": "Suggest", "command": "editor.action.triggerSuggest" }
            }]
            """);

        Assert.Equal("color: ;", entry.InsertText);
        Assert.Equal(7, entry.CaretInInsertText);
        Assert.Equal(new LspRange(3, 2, 3, 5), entry.Edit);
        Assert.True(entry.SuggestAgain);
    }

    [Fact]
    public void Texto_simples_nao_e_tratado_como_modelo()
    {
        var entry = Parse("""[{ "label": "$nome", "insertTextFormat": 1, "insertText": "$nome" }]""");

        Assert.Equal("$nome", entry.InsertText);
        Assert.Null(entry.CaretInInsertText);
        Assert.Null(entry.Edit);
        Assert.False(entry.SuggestAgain);
    }

    [Fact]
    public void Outro_comando_nao_abre_a_lista_de_novo() =>
        Assert.False(Parse("""[{ "label": "a", "command": { "title": "x", "command": "outro" } }]""").SuggestAgain);

    // Descrições

    [Fact]
    public void Tira_o_icone_em_base64_e_os_links_da_descricao()
    {
        const string Css = "Define a cor.\\\n\n![Baseline icon](data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMTgi)\n\nSyntax: &lt;color&gt;\n\n[MDN Reference](https://developer.mozilla.org/x)";

        var text = LspParsers.CleanDocumentation(Css);

        Assert.Equal("Define a cor.\n\nSyntax: <color>\n\nMDN Reference", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("![so icone](data:image/png;base64,AAAA)")]
    public void Descricao_sem_texto_vira_nada(string? documentation) =>
        Assert.Null(LspParsers.CleanDocumentation(documentation));

    [Fact]
    public void Descricao_gigante_e_cortada()
    {
        var text = LspParsers.CleanDocumentation(new string('a', 5000))!;
        Assert.True(text.Length <= 1501);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Texto_de_codigo_na_descricao_nao_e_estragado() =>
        Assert.Equal("strlen(string $x): int\n\nretorna $x[0]", LspParsers.CleanDocumentation("strlen(string $x): int\n\nretorna $x[0]"));

    [AvaloniaFact]
    public void Descricao_tem_largura_limitada_e_quebra_linha()
    {
        var item = new CompletionItem(new CompletionEntry("a", "detalhe", "texto", "a", null), null, 0, null);

        var block = Assert.IsType<Avalonia.Controls.TextBlock>(item.Description);

        Assert.Equal("detalhe\n\ntexto", block.Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, block.TextWrapping);
        Assert.True(block.MaxWidth <= 500);
    }

    [AvaloniaFact]
    public void Sem_descricao_nao_abre_a_janela_ao_lado() =>
        Assert.Null(new CompletionItem(new CompletionEntry("a", null, null, "a", null), null, 0, null).Description);

    [AvaloniaTheory]
    [InlineData("claro")]
    [InlineData("grafite")]
    public void Lista_usa_cores_da_paleta(string theme)
    {
        Sirius.Theming.Palette.Apply(theme);
        var resources = Avalonia.Application.Current!.Resources;

        Assert.True(resources.ContainsKey("Selection"));
        Assert.Same(Sirius.Theming.Palette.Current.Selection, resources["Selection"]);
        Sirius.Theming.Palette.Apply("claro");
    }

    // Na janela, com o servidor falso

    private static object Item(string label, string newText, int line, int start, int end, bool snippet = true, bool again = false) => new Dictionary<string, object?>
    {
        ["label"] = label,
        ["insertTextFormat"] = snippet ? 2 : 1,
        ["textEdit"] = new { newText, range = new { start = new { line, character = start }, end = new { line, character = end } } },
        ["command"] = again ? new { title = "x", command = "editor.action.triggerSuggest" } : null,
    };

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(MainWindow Window, FakeLanguageServer Server)> Open(TempFolder temp, string file, string text)
    {
        var server = new FakeLanguageServer();
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => true;
        window.Language.Launcher = (_, _) => server.Connection;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File(file, text));
        await Until(() => window.Language.ClientForDocument(window.ActiveDocument!) is not null);
        return (window, server);
    }

    private static string Text(MainWindow window) => window.ActiveDocument!.Document.Text;

    private static async Task Request(MainWindow window, int caret)
    {
        window.SetCaretOffset(caret);
        await window.RequestCompletionAsync();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Palavra_com_hifen_e_substituida_inteira()
    {
        using var temp = new TempFolder();
        const string Css = "a { background-co }";
        var (window, server) = await Open(temp, "a.css", Css);
        server.Completion = _ => new { items = new[] { Item("background-color", "background-color: $1;", 0, 4, 17) } };

        await Request(window, 17);
        Assert.True(window.AcceptCompletion("background-color"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a { background-color: ; }", Text(window));
        Assert.Equal(22, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cursor_fica_entre_as_aspas_do_atributo()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.html", "<div cla></div>");
        server.Completion = _ => new { items = new[] { Item("class", "class=\"$1\"", 0, 5, 8) } };

        await Request(window, 8);
        window.AcceptCompletion("class");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("<div class=\"\"></div>", Text(window));
        Assert.Equal(12, window.CaretOffset);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Troca_tambem_o_que_o_servidor_marcou_depois_do_cursor()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a { backXYZ }");
        server.Completion = _ => new { items = new[] { Item("background", "background", 0, 4, 11, snippet: false) } };

        await Request(window, 8);
        window.AcceptCompletion("background");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a { background }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Continuar_digitando_antes_de_aceitar_troca_o_trecho_certo()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a { ba }");
        server.Completion = _ => new { items = new[] { Item("background", "background: $1;", 0, 4, 6) } };

        await Request(window, 6);
        window.KeyTextInput("ck");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a { back }", Text(window));

        window.AcceptCompletion("background");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a { background: ; }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escolher_propriedade_abre_a_lista_de_valores()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a { col }");
        var requests = 0;
        server.Completion = _ => ++requests == 1
            ? new { items = new[] { Item("color", "color: $1;", 0, 4, 7, again: true) } }
            : new { items = new[] { Item("red", "red", 0, 11, 11, snippet: false) } };

        await Request(window, 7);
        window.AcceptCompletion("color");
        await Until(() => window.CompletionLabels.Contains("red"));

        Assert.Equal(2, requests);
        window.AcceptCompletion("red");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("a { color: red; }", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sem_pedido_de_abrir_de_novo_a_lista_fica_fechada()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a { col }");
        var requests = 0;
        server.Completion = _ =>
        {
            requests++;
            return new { items = new[] { Item("color", "color", 0, 4, 7, snippet: false) } };
        };

        await Request(window, 7);
        window.AcceptCompletion("color");
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, requests);
        Assert.False(window.IsCompletionOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Lista_grande_filtra_antes_de_cortar()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a { z }");
        var many = Enumerable.Range(0, 1500)
            .Select(i => (object)new { label = $"propriedade-{i:0000}", sortText = $"a{i:0000}" })
            .Append(new { label = "z-index", sortText = "z" })
            .ToArray();
        server.Completion = _ => new { items = many };

        await Request(window, 5);

        Assert.Equal(["z-index"], window.CompletionLabels);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sem_nada_digitado_nao_corta_cedo_demais()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a {  }");
        server.Completion = _ => new { items = Enumerable.Range(0, 800).Select(i => new { label = $"p{i}" }).ToArray() };

        await Request(window, 4);

        Assert.Equal(800, window.CompletionLabels.Count());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sugestao_sem_trecho_continua_usando_a_palavra_sob_o_cursor()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.php", "<?php\necho im");
        server.Completion = _ => new { items = new[] { new { label = "imprimir", insertText = "imprimir" } } };

        await Request(window, 13);
        window.AcceptCompletion("imprimir");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("<?php\necho imprimir", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Trecho_em_outra_linha_e_ignorado()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a {}\nbc");
        server.Completion = _ => new { items = new[] { Item("bcd", "bcd", 0, 0, 2, snippet: false) } };

        await Request(window, 7);
        window.AcceptCompletion("bcd");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("a {}\nbcd", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Servidor_de_html_recebe_o_pedido_de_olhar_style_e_script()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.html", "<p></p>");

        var options = server.InitializeParameters!.Value.GetProperty("initializationOptions");
        Assert.True(options.GetProperty("embeddedLanguages").GetProperty("css").GetBoolean());
        Assert.True(options.GetProperty("embeddedLanguages").GetProperty("javascript").GetBoolean());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cliente_avisa_que_entende_modelos_de_texto()
    {
        using var temp = new TempFolder();
        var (window, server) = await Open(temp, "a.css", "a {}");

        var item = server.InitializeParameters!.Value.GetProperty("capabilities").GetProperty("textDocument").GetProperty("completion").GetProperty("completionItem");
        Assert.True(item.GetProperty("snippetSupport").GetBoolean());
        window.Close();
    }

    [AvaloniaFact]
    public void Sem_servidor_a_capsula_oferece_instalar_html_e_css()
    {
        using var temp = new TempFolder();
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.OpenFile(temp.File("a.css", "a {}"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("CSS", window.MissingServerName);

        window.OpenFile(temp.File("a.html", "<p></p>"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("HTML", window.MissingServerName);
        window.Close();
    }
}
