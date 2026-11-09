using System.Text;
using System.Text.Json.Nodes;
using Sirius.Core;
using Sirius.Lsp;

namespace Sirius;

// O que o Claude Code vê da Sirius pelo MCP. A janela ouve no pipe da pasta aberta e responde em texto, montado na
// thread da interface. Nada aqui muda o projeto, a depuração ou a janela: o Claude só lê.
public partial class MainWindow
{
    private const int McpMaxVariableLines = 150;
    private const int McpMaxValueChars = 300;
    private const int McpOutputLines = 60;
    private const int McpPageOutputChars = 3000;

    private CancellationTokenSource? _mcpStop;
    private bool _mcpEnabled = Program.Instance is not null;

    // Só no programa de verdade: os testes abrem dezenas de janelas e ligam o canal só quando precisam.
    public bool McpEnabled
    {
        get => _mcpEnabled;
        set
        {
            _mcpEnabled = value;
            StartMcpHost();
        }
    }

    // Conectar o Claude Code: "claude mcp add" grava a Sirius na configuração dele, para todas as pastas.
    // Nas sondas, SIRIUS_MCP_SCOPE=local deixa a configuração presa à pasta de teste.
    private enum ClaudeState { Unknown, Busy, Missing, Connected, Disconnected }

    private ClaudeState _claudeState = ClaudeState.Unknown;
    private string _claudeStatus = "";
    private static string ClaudeScope => Environment.GetEnvironmentVariable("SIRIUS_MCP_SCOPE") is "local" ? "local" : "user";

    // Trocados nos testes, que nunca mexem na configuração do Claude Code de verdade.
    public Func<string?> FindClaude { get; set; } = FindClaudeExecutable;
    public Func<string, string[], Task<(int Code, string Output)>>? RunClaude { get; set; }

    public bool IsClaudeConnected => _claudeState == ClaudeState.Connected;
    public string ClaudeStatusText => _claudeStatus;

    private static string? FindClaudeExecutable()
    {
        var found = LanguageServerCatalog.FindExecutable("claude");
        if (found is null || found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return found;
        // O npm instala um atalho .cmd, e o programa de verdade fica dentro do pacote: chamá-lo direto evita o cmd.
        var inside = Path.Combine(Path.GetDirectoryName(found)!, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        return File.Exists(inside) ? inside : null;
    }

    private Task<(int Code, string Output)> ClaudeAsync(string claude, params string[] arguments) =>
        (RunClaude ?? RunClaudeAsync)(claude, arguments);

    private async Task<(int Code, string Output)> RunClaudeAsync(string claude, string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(claude)
        {
            WorkingDirectory = RootFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(info)!;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "Não foi possível abrir o Claude Code.");
        }
        using var started = process;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            return (-1, "O Claude Code demorou demais para responder.");
        }
        return (process.ExitCode, (await output) + (await error));
    }

    private async Task RefreshClaudeStateAsync()
    {
        if (FindClaude() is not { } claude)
        {
            _claudeState = ClaudeState.Missing;
            _claudeStatus = "O Claude Code não está instalado neste computador. Depois de instalar, volte aqui para conectar.";
            return;
        }
        if (_claudeState is ClaudeState.Unknown)
            _claudeStatus = "Verificando...";
        RefreshSettings();
        var (code, _) = await ClaudeAsync(claude, "mcp", "get", "sirius");
        _claudeState = code == 0 ? ClaudeState.Connected : ClaudeState.Disconnected;
        _claudeStatus = _claudeState == ClaudeState.Connected
            ? "Conectado. Vale para o Claude Code aberto a partir de agora; um que já estava aberto precisa ser reiniciado."
            : "Desconectado. O Claude Code não vê nada da Sirius.";
    }

    private async Task SetClaudeConnectedAsync(bool connect)
    {
        if (FindClaude() is not { } claude || _claudeState == ClaudeState.Busy)
            return;
        _claudeState = ClaudeState.Busy;
        _claudeStatus = connect ? "Conectando..." : "Desconectando...";
        RefreshSettings();

        // Tira a anterior, que pode apontar para outra pasta da Sirius, antes de gravar a nova.
        await ClaudeAsync(claude, "mcp", "remove", "--scope", ClaudeScope, "sirius");
        if (connect)
        {
            var (code, output) = await ClaudeAsync(claude, "mcp", "add", "--scope", ClaudeScope, "sirius", "--", Environment.ProcessPath!, "--mcp");
            if (code != 0)
            {
                _claudeState = ClaudeState.Disconnected;
                _claudeStatus = "Não foi possível conectar: " + output.Trim().Split('\n').FirstOrDefault();
                return;
            }
        }
        _claudeState = ClaudeState.Unknown;
        await RefreshClaudeStateAsync();
    }

    private void StartMcpHost()
    {
        StopMcpHost();
        if (!_mcpEnabled || RootFolder is not { } folder)
            return;
        var stop = new CancellationTokenSource();
        _mcpStop = stop;
        _ = Task.Run(() => ListenMcpAsync(folder, stop.Token));
    }

    private void StopMcpHost()
    {
        _mcpStop?.Cancel();
        _mcpStop = null;
    }

    // Chega numa thread de fundo.
    private async Task ListenMcpAsync(string folder, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            System.IO.Pipes.NamedPipeServerStream server;
            try
            {
                server = McpChannel.CreateServer(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Sem canal, o Claude só não vê a Sirius; o resto continua igual.
                return;
            }
            try
            {
                await server.WaitForConnectionAsync(stop);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                await server.DisposeAsync();
                return;
            }
            _ = Task.Run(() => AnswerMcpAsync(server));
        }
    }

    private async Task AnswerMcpAsync(System.IO.Pipes.NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var line = await McpChannel.ReadLineAsync(server, timeout.Token);
                var tool = line is null ? null : (string?)(JsonNode.Parse(line) as JsonObject)?["tool"];
                var text = await _uiDispatcher.InvokeAsync(() => McpAnswer(tool ?? ""));
                var reply = new JsonObject { ["text"] = text };
                await McpChannel.WriteLineAsync(server, reply.ToJsonString(), timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // A ponte desistiu ou mandou algo estranho: nada a fazer.
            }
        }
    }

    public string McpAnswer(string tool) => tool switch
    {
        "editor_atual" => McpEditor(),
        "problemas" => McpProblems(),
        "depuracao" => McpDebug(),
        "saida_execucao" => McpRunOutput(),
        _ => $"Ferramenta desconhecida: {tool}",
    };

    private string Relative(string path) =>
        RootFolder is { } root && Core.FileOperations.IsInside(path, root) ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;

    private string McpEditor()
    {
        var text = new StringBuilder();
        text.AppendLine($"Pasta do projeto: {RootFolder ?? "nenhuma"}");
        if (ActiveDocument is not { } document)
            return text.Append("Nenhum arquivo aberto.").ToString();

        if (document.IsImage)
        {
            text.Append($"Arquivo aberto: {Relative(document.FullPath)}, uma imagem.");
        }
        else
        {
            var caret = CodeEditor.TextArea.Caret;
            var name = document.IsUntitled ? $"{document.Name} (aba sem nome, nunca salva)" : Relative(document.FullPath);
            text.AppendLine($"Arquivo aberto: {name}, cursor na linha {caret.Line}, coluna {caret.Column}{(document.IsDirty ? ", com alterações ainda não salvas" : "")}.");
            var lines = document.Document;
            if (CodeEditor.TextArea.Selection.IsEmpty)
            {
                text.AppendLine("Nada selecionado. A linha do cursor:");
                text.AppendLine($"{caret.Line}: {lines.GetText(lines.GetLineByNumber(caret.Line))}");
            }
            else
            {
                var (first, last) = SelectedLines();
                text.AppendLine($"Selecionado: \"{Shorten(CodeEditor.SelectedText, 2000)}\"");
                text.AppendLine($"Nas linhas {first} a {last}:");
                for (var number = first; number <= last && number < first + 80; number++)
                    text.AppendLine($"{number}: {lines.GetText(lines.GetLineByNumber(number))}");
            }
        }

        var others = _documents.Where(d => d != document).Select(d => (d.IsUntitled ? d.Name : Relative(d.FullPath)) + (d.IsDirty ? " (não salvo)" : "")).ToList();
        if (others.Count > 0)
            text.Append($"Outras abas: {string.Join(", ", others)}.");
        return text.ToString().TrimEnd();
    }

    private string McpProblems()
    {
        var all = _language.AllDiagnostics
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Diagnostic.Line)
            .ToList();
        if (all.Count == 0)
        {
            return _documents.Count == 0
                ? "Nenhum arquivo aberto, e a Sirius só verifica os arquivos abertos."
                : "Nenhum erro ou aviso nos arquivos abertos.";
        }

        var text = new StringBuilder();
        foreach (var file in all.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(Relative(file.Key));
            foreach (var (_, diagnostic) in file)
            {
                var kind = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => "erro",
                    DiagnosticSeverity.Warning => "aviso",
                    _ => "dica",
                };
                text.AppendLine($"  linha {diagnostic.Line + 1}, coluna {diagnostic.Character + 1}, {kind}: {diagnostic.Message}");
            }
        }
        return text.ToString().TrimEnd();
    }

    private string McpDebug()
    {
        var text = new StringBuilder();
        if (_debug is null)
            text.AppendLine(IsListeningToServer ? "A Sirius está ouvindo o servidor, esperando uma página parar." : "A depuração não está ligada.");
        else if (_pause is null)
            text.AppendLine("A depuração está ligada, mas o programa não está parado: falta abrir a página ou chegar a um ponto de parada.");

        if (_pause is { } pause)
        {
            var frames = pause.Frames;
            var top = frames[0];
            text.AppendLine($"Parado em {Relative(top.Path)}, linha {top.Line}, em {top.Name}.");
            if (SourceLine(top.Path, top.Line) is { } source)
                text.AppendLine($"{top.Line}: {source.Trim()}");
            if (frames.Count > 1)
            {
                text.AppendLine("Pilha de chamadas, de onde parou até o começo:");
                foreach (var frame in frames)
                    text.AppendLine($"  {frame.Name} em {Relative(frame.Path)}, linha {frame.Line}");
            }

            var shown = SelectedFrame ?? top;
            text.AppendLine(shown == top ? "Variáveis:" : $"Variáveis do nível escolhido na tela ({shown.Name}):");
            var lines = 0;
            foreach (var variable in shown.Variables)
                AppendVariable(text, variable, 1, ref lines);
            if (shown.Variables.Count == 0)
                text.AppendLine("  nenhuma");

            if (pause.Watches is { Count: > 0 } watches)
            {
                text.AppendLine("Expressões vigiadas:");
                foreach (var watch in watches)
                    AppendVariable(text, watch, 1, ref lines);
            }

            var output = (_pageOutput + _heldOutput).ToString();
            if (output.Length > 0)
            {
                text.AppendLine("O que a página já escreveu:");
                text.AppendLine(output.Length > McpPageOutputChars ? "..." + output[^McpPageOutputChars..] : output);
            }
        }

        var breakpoints = BreakpointsSnapshot();
        var conditions = ConditionsSnapshot();
        if (breakpoints.Count > 0)
        {
            text.AppendLine("Pontos de parada:");
            foreach (var (path, lines) in breakpoints)
            {
                foreach (var line in lines.Order())
                {
                    var condition = conditions.GetValueOrDefault(path)?.GetValueOrDefault(line);
                    text.AppendLine($"  {Relative(path)}, linha {line}{(condition is null ? "" : $", só quando {condition}")}");
                }
            }
        }
        else
        {
            text.AppendLine("Nenhum ponto de parada marcado.");
        }
        return text.ToString().TrimEnd();
    }

    private string? SourceLine(string path, int line)
    {
        var open = _documents.Find(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (open is { IsImage: false } && line >= 1 && line <= open.Document.LineCount)
            return open.Document.GetText(open.Document.GetLineByNumber(line));
        try
        {
            return File.ReadLines(path).Skip(line - 1).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AppendVariable(StringBuilder text, DebugVariable variable, int depth, ref int lines)
    {
        if (lines >= McpMaxVariableLines)
        {
            if (lines++ == McpMaxVariableLines)
                text.AppendLine("  (mais variáveis, cortadas)");
            return;
        }
        lines++;
        var value = variable.Children.Count > 0 && variable.Value.Length == 0 ? "" : $" = {Shorten(variable.Value, McpMaxValueChars)}";
        text.AppendLine($"{new string(' ', depth * 2)}{variable.Name}{value} ({variable.Type})");
        if (depth < 4)
        {
            foreach (var child in variable.Children)
                AppendVariable(text, child, depth + 1, ref lines);
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private string McpRunOutput()
    {
        if (_task is null)
            return "Nada foi executado nesta janela ainda.";
        var all = _task.Buffer.AllText().Split('\n');
        var tail = all.Skip(Math.Max(0, all.Length - McpOutputLines));
        var state = _task.IsRunning ? "Ainda rodando." : $"Terminou com o código {_task.ExitCode}.";
        return $"Comando: {RunCommandForProject() ?? "detectado pela Sirius"}. {state}\nÚltimas linhas da saída:\n{string.Join('\n', tail)}";
    }
}
