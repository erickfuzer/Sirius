using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Lsp;
using Xunit;

namespace Sirius.Tests;

public class SymbolsTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static object Range(int line, int character, int endLine, int endCharacter) => new
    {
        start = new { line, character },
        end = new { line = endLine, character = endCharacter },
    };

    // Do jeito que o Intelephense responde: em árvore, com selectionRange no nome.
    private static object Symbol(string name, int kind, int line, object[]? children = null) => new
    {
        name,
        kind,
        range = Range(line, 0, line + 2, 1),
        selectionRange = Range(line, 9, line, 9 + name.Length),
        children = children ?? [],
    };

    [Fact]
    public void Le_a_resposta_em_arvore_com_profundidade_e_quem_contem()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            Symbol("Pessoa", 5, 7, [Symbol("saudar", 6, 10), Symbol("$nome", 7, 8)]),
            Symbol("calcular_imc", 12, 3),
        });

        var symbols = LspParsers.DocumentSymbols(Json(json));

        Assert.Equal(["calcular_imc", "Pessoa", "$nome", "saudar"], symbols.Select(s => s.Name));
        var saudar = symbols.Single(s => s.Name == "saudar");
        Assert.Equal((1, "Pessoa", 10, 9), (saudar.Depth, saudar.Container, saudar.Line, saudar.Character));
    }

    [Fact]
    public void Le_a_resposta_em_lista_de_servidores_antigos()
    {
        var json = """
            [
              {"name":"saudar","kind":6,"containerName":"Pessoa","location":{"uri":"file:///c%3A/a.php","range":{"start":{"line":4,"character":2},"end":{"line":4,"character":8}}}},
              {"name":"Pessoa","kind":5,"location":{"uri":"file:///c%3A/a.php","range":{"start":{"line":1,"character":0},"end":{"line":9,"character":1}}}}
            ]
            """;

        var symbols = LspParsers.DocumentSymbols(Json(json));

        Assert.Equal(["Pessoa", "saudar"], symbols.Select(s => s.Name));
        Assert.Equal((1, "Pessoa"), (symbols[1].Depth, symbols[1].Container));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"kind\":5}]")]
    public void Resposta_vazia_ou_estranha_nao_quebra(string json)
    {
        Assert.Empty(LspParsers.DocumentSymbols(Json(json)));
    }

    [Theory]
    [InlineData("a.php", "calcular_imc", 12, 0, null, "função")]
    [InlineData("a.php", "Pessoa", 5, 0, null, "classe")]
    [InlineData("a.php", "saudar", 6, 1, "Pessoa", "método")]
    [InlineData("a.php", "LIMITE", 14, 0, null, "constante")]
    [InlineData("a.php", "$peso", 13, 0, null, "variável")]
    [InlineData("a.php", "p", 8, 0, null, "tag")]
    [InlineData("a.css", ".cartao h2", 5, 0, null, "seletor")]
    [InlineData("a.css", "@media (max-width: 600px)", 2, 0, null, "regra")]
    [InlineData("a.css", "--cor", 13, 0, null, "variável")]
    [InlineData("a.html", "div.cartao", 8, 2, "main", "tag")]
    [InlineData("a.html", "h1", 5, 0, null, "seletor")]
    [InlineData("a.js", "total", 13, 0, null, "variável")]
    public void Cada_tipo_tem_o_nome_que_se_usa_no_curso(string file, string name, int kind, int depth, string? container, string expected)
    {
        Assert.Equal(expected, MainWindow.KindName(file, new LspSymbol(name, null, kind, 0, 0, depth, container)));
    }

    // Na janela

    private static (MainWindow Window, FakeLanguageServer Server) Open(TempFolder temp, string name, string text)
    {
        var path = temp.File(name, text);
        var server = new FakeLanguageServer();
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => true;
        window.Language.Launcher = (_, _) => server.Connection;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        return (window, server);
    }

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

    private const string Php = "<?php\n$peso = 70;\nfunction calcular_imc($peso, $altura) {\n    return $peso / ($altura * $altura);\n}\nclass Pessoa {\n    public function saudar() {}\n}\n";

    private static object[] PhpSymbols() =>
    [
        Symbol("$peso", 13, 1),
        Symbol("calcular_imc", 12, 2, [Symbol("$peso", 13, 2), Symbol("$altura", 13, 2)]),
        Symbol("Pessoa", 5, 5, [Symbol("saudar", 6, 6)]),
    ];

    [AvaloniaFact]
    public async Task Ctrl_Shift_O_lista_as_funcoes_sem_as_variaveis_de_dentro_e_leva_a_linha()
    {
        using var temp = new TempFolder();
        var (window, server) = Open(temp, "a.php", Php);
        server.DocumentSymbols = _ => PhpSymbols();

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsPaletteOpen);
        await Until(() => window.PaletteCommandNames("@").Any(n => n.Contains("calcular_imc")));

        var names = window.PaletteCommandNames("@").ToList();
        Assert.Equal(["$peso", "calcular_imc", "Pessoa", "   saudar"], names);

        // Com filtro, sem o recuo, e só o que tem o pedaço digitado.
        Assert.Equal(["saudar"], window.PaletteCommandNames("@sau"));

        window.OpenPalette("@sau");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsPaletteOpen);
        Assert.Equal(7, window.CaretLine);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_lista_mostra_o_tipo_e_a_linha()
    {
        using var temp = new TempFolder();
        var (window, server) = Open(temp, "a.php", Php);
        server.DocumentSymbols = _ => PhpSymbols();

        window.OpenSymbols();
        await Until(() => window.PaletteCommandNames("@").Count() > 1);

        Assert.Contains(("calcular_imc", "função · linha 3"), window.PaletteCommands("@calc"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Editar_o_arquivo_pede_a_lista_de_novo()
    {
        using var temp = new TempFolder();
        var (window, server) = Open(temp, "a.php", Php);
        var asked = 0;
        server.DocumentSymbols = _ => { asked++; return PhpSymbols(); };

        window.OpenSymbols();
        await Until(() => window.PaletteCommandNames("@").Count() > 1);
        window.PaletteCommandNames("@");
        Assert.Equal(1, asked);

        window.ActiveDocument!.Document.Insert(0, " ");
        window.PaletteCommandNames("@");
        await Until(() => asked == 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_sem_nada_e_filtro_sem_resultado_avisam()
    {
        using var temp = new TempFolder();
        var (window, server) = Open(temp, "a.php", "<?php\n");

        window.OpenSymbols();
        await Until(() => !window.IsLoadingSymbols && window.PaletteCommandNames("@").First().StartsWith("Nenhuma"));

        server.DocumentSymbols = _ => PhpSymbols();
        window.ActiveDocument!.Document.Insert(0, " ");
        window.PaletteCommandNames("@");
        await Until(() => window.PaletteCommandNames("@").Count() > 1);
        Assert.Equal(["Nada com \"xyz\""], window.PaletteCommandNames("@xyz"));
        window.Close();
    }

    // Antes, abrir a lista com o servidor ainda abrindo redesenhava a barra de dentro do próprio desenho,
    // sem parar, e o processo caía com estouro de pilha (0xc00000fd) uma vez em seis rodadas da suíte.
    [AvaloniaFact]
    public void Servidor_que_ainda_esta_abrindo_mostra_que_espera_sem_cair()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", Php);
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => true;
        window.Language.Launcher = (_, _) => null;
        window.Show();
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        window.OpenSymbols();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsPaletteOpen);
        Assert.Equal(["Esperando o servidor da linguagem abrir…"], window.PaletteCommandNames("@"));
        window.ClosePalette();
        window.Close();
    }

    [AvaloniaFact]
    public void Texto_puro_diz_que_nao_tem_lista()
    {
        using var temp = new TempFolder();
        var path = temp.File("notas.txt", "oi");
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();

        window.OpenSymbols();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Este tipo de arquivo não tem lista de funções"], window.PaletteCommandNames("@"));
        Assert.Contains("Ir para função, classe ou seletor", window.PaletteCommandNames("função"));
        window.Close();
    }
}
