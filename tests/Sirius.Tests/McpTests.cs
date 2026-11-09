using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

// O que o Claude Code vê da Sirius pelo MCP: a ponte "Sirius.exe --mcp" e as respostas da janela.
public class McpTests
{
    private static JsonObject Request(int id, string method, JsonObject? parameters = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    private static string ToolText(JsonObject response) =>
        (string)response["result"]!["content"]![0]!["text"]!;

    // Sem janela

    [Fact]
    public async Task Apresenta_as_quatro_ferramentas_so_de_leitura()
    {
        var init = await McpBridge.AnswerAsync(Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }), Path.GetTempPath());
        Assert.Equal("2025-06-18", (string?)init["result"]!["protocolVersion"]);
        Assert.Equal("sirius", (string?)init["result"]!["serverInfo"]!["name"]);

        var list = await McpBridge.AnswerAsync(Request(2, "tools/list"), Path.GetTempPath());
        var names = list["result"]!["tools"]!.AsArray().Select(tool => (string?)tool!["name"]).ToList();
        Assert.Equal(["editor_atual", "problemas", "depuracao", "saida_execucao"], names);
    }

    [Fact]
    public async Task Sem_sirius_aberta_na_pasta_explica()
    {
        using var temp = new TempFolder();
        var call = Request(3, "tools/call", new JsonObject { ["name"] = "editor_atual", ["arguments"] = new JsonObject() });
        var answer = await McpBridge.AnswerAsync(call, temp.Path);

        Assert.True((bool?)answer["result"]!["isError"]);
        Assert.Contains("Nenhuma Sirius está aberta", ToolText(answer));
    }

    [Fact]
    public async Task Ferramenta_ou_metodo_desconhecido_e_recusado()
    {
        var tool = await McpBridge.AnswerAsync(Request(4, "tools/call", new JsonObject { ["name"] = "apagar_tudo" }), Path.GetTempPath());
        Assert.True((bool?)tool["result"]!["isError"]);

        var method = await McpBridge.AnswerAsync(Request(5, "resources/list"), Path.GetTempPath());
        Assert.Equal(-32601, (int?)method["error"]!["code"]);
    }

    [Fact]
    public async Task Conversa_uma_mensagem_por_linha_e_ignora_avisos()
    {
        var lines = string.Join('\n',
            Request(1, "initialize").ToJsonString(),
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Request(2, "ping").ToJsonString()) + "\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(lines));
        using var output = new MemoryStream();

        await McpBridge.RunAsync(input, output, Path.GetTempPath());

        var answers = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, answers.Length);
        Assert.Equal(2, (int?)JsonNode.Parse(answers[1])!["id"]);
    }

    [Fact]
    public void Cada_pasta_tem_o_seu_canal()
    {
        Assert.Equal(McpChannel.PipeName(@"C:\Projetos\Loja"), McpChannel.PipeName(@"c:\projetos\loja\"));
        Assert.NotEqual(McpChannel.PipeName(@"C:\Projetos\Loja"), McpChannel.PipeName(@"C:\Projetos\Blog"));
    }

    // Na janela

    private static MainWindow Open(TempFolder temp, TempFolder config)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
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

    [AvaloniaFact]
    public void Editor_mostra_o_arquivo_a_selecao_e_as_outras_abas()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var index = temp.File("index.php", "<?php\n$peso = 70;\n$altura = 1.75;\n");
        var css = temp.File("estilo.css", "body {}");
        var window = Open(temp, config);
        window.OpenFile(css);
        window.OpenFile(index);
        var document = window.ActiveDocument!.Document;

        window.SetCaretOffset(document.GetLineByNumber(3).Offset);
        var nothing = window.McpAnswer("editor_atual");
        Assert.Contains("Arquivo aberto: index.php, cursor na linha 3", nothing);
        Assert.Contains("Nada selecionado. A linha do cursor:\n3: $altura = 1.75;", nothing.Replace("\r", ""));
        Assert.Contains("Outras abas: estilo.css.", nothing);

        window.SelectForTests(document.GetLineByNumber(2).Offset, document.GetLineByNumber(3).EndOffset);
        var selected = window.McpAnswer("editor_atual").Replace("\r", "");
        Assert.Contains("Nas linhas 2 a 3:\n2: $peso = 70;\n3: $altura = 1.75;", selected);
        window.Close();
    }

    [AvaloniaFact]
    public void Depuracao_desligada_mostra_os_pontos_marcados()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("calculo.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        window.ToggleBreakpoint(3);

        var text = window.McpAnswer("depuracao").Replace("\r", "");
        Assert.Contains("A depuração não está ligada.", text);
        Assert.Contains("Pontos de parada:\n  calculo.php, linha 3", text);
        Assert.Equal("Nada foi executado nesta janela ainda.", window.McpAnswer("saida_execucao"));
        Assert.Equal("Nenhum erro ou aviso nos arquivos abertos.", window.McpAnswer("problemas"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_pergunta_pelo_canal_e_a_janela_responde()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var index = temp.File("index.php", "<?php echo 1;\n");
        var window = Open(temp, config);
        window.OpenFile(index);
        window.McpEnabled = true;

        // O Claude pode ter sido aberto numa subpasta do projeto.
        var sub = Directory.CreateDirectory(Path.Combine(temp.Path, "css")).FullName;
        var call = Request(7, "tools/call", new JsonObject { ["name"] = "editor_atual", ["arguments"] = new JsonObject() });
        var asking = Task.Run(() => McpBridge.AnswerAsync(call, sub));
        await Until(() => asking.IsCompleted);

        var answer = await asking;
        Assert.False((bool?)answer["result"]!["isError"]);
        Assert.Contains("Arquivo aberto: index.php", ToolText(answer));

        // Fechada a janela, o canal some.
        window.Close();
        var after = Task.Run(() => McpChannel.AskAsync(temp.Path, "editor_atual", null));
        await Until(() => after.IsCompleted);
        Assert.Null(await after);
    }
}
