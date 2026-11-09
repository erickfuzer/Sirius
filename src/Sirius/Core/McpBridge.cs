using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sirius.Core;

// O servidor MCP que o Claude Code abre: "Sirius.exe --mcp" fala JSON-RPC, uma mensagem por linha, pela entrada
// e saída padrão, e repassa cada pergunta para a janela da Sirius aberta na pasta onde o Claude está.
// Só leitura: nenhuma ferramenta muda o projeto, a depuração ou a janela.
public static class McpBridge
{
    private const string FallbackProtocol = "2025-06-18";

    private static JsonObject Tool(string name, string description) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    public static JsonArray Tools() =>
    [
        Tool("editor_atual",
            "O que a pessoa está vendo na Sirius, a IDE deste terminal: o arquivo aberto, a linha do cursor, o trecho selecionado com os números das linhas, as outras abas e se há alterações não salvas. Use quando ela disser \"isto\", \"esta linha\" ou \"o que selecionei\"."),
        Tool("problemas",
            "Os erros e avisos que a Sirius mostra nos arquivos abertos, vindos do servidor de linguagem (no PHP, o Intelephense), com arquivo, linha e mensagem."),
        Tool("depuracao",
            "O estado da depuração de PHP na Sirius: se está parada, em que arquivo e linha, a pilha de chamadas, as variáveis com os valores, as expressões vigiadas, o que a página já escreveu e os pontos de parada marcados."),
        Tool("saida_execucao",
            "As últimas linhas da saída do botão Executar da Sirius, como o servidor do PHP e os erros dele, e se ainda está rodando."),
    ];

    public static async Task<int> RunAsync(Stream input, Stream output, string folder)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
                continue;
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            // Avisos, como "notifications/initialized", não têm resposta.
            if (message?["id"] is not { } id)
                continue;
            var response = await AnswerAsync(message, folder);
            response["jsonrpc"] = "2.0";
            response["id"] = id.DeepClone();
            await writer.WriteLineAsync(response.ToJsonString());
        }
        return 0;
    }

    public static async Task<JsonObject> AnswerAsync(JsonObject message, string folder)
    {
        var parameters = message["params"] as JsonObject;
        switch ((string?)message["method"])
        {
            case "initialize":
                return Result(new JsonObject
                {
                    ["protocolVersion"] = (string?)parameters?["protocolVersion"] ?? FallbackProtocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "sirius", ["version"] = typeof(McpBridge).Assembly.GetName().Version?.ToString(3) ?? "0" },
                });
            case "ping":
                return Result(new JsonObject());
            case "tools/list":
                return Result(new JsonObject { ["tools"] = Tools() });
            case "tools/call":
                var name = (string?)parameters?["name"] ?? "";
                if (!Tools().Any(tool => (string?)tool?["name"] == name))
                    return Text($"A Sirius não tem a ferramenta {name}.", error: true);
                string? answer;
                try
                {
                    answer = await McpChannel.AskAsync(folder, name, parameters?["arguments"] as JsonObject);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
                {
                    answer = null;
                }
                if (answer is null || JsonNode.Parse(answer) is not JsonObject reply)
                    return Text($"Nenhuma Sirius está aberta com a pasta {folder}. Abra a pasta na Sirius e tente de novo.", error: true);
                return Text((string?)reply["text"] ?? "", (bool?)reply["error"] ?? false);
            default:
                return new JsonObject { ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Método desconhecido." } };
        }
    }

    private static JsonObject Result(JsonObject result) => new() { ["result"] = result };

    private static JsonObject Text(string text, bool error = false) => Result(new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    });
}
