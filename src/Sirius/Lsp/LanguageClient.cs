using System.Text.Json;

namespace Sirius.Lsp;

// O que a Sirius sabe fazer com um servidor de linguagem já conectado.
public sealed class LanguageClient : IDisposable
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(60);

    private readonly LspConnection _connection;
    private readonly IDisposable _owner;

    public LanguageClient(ServerConnection server)
    {
        _owner = server.Owner;
        _connection = new LspConnection(server.Input, server.Output);
        _connection.NotificationReceived += OnNotification;
        _connection.Closed += () => Closed?.Invoke();
    }

    public IReadOnlyList<char> CompletionTriggers { get; private set; } = [];
    public bool SupportsCompletion { get; private set; }
    public bool SupportsDefinition { get; private set; }
    public bool SupportsHover { get; private set; }
    public bool SupportsRename { get; private set; }

    // Chegam na thread de leitura da conexão.
    public event Action<string, List<LspDiagnostic>>? DiagnosticsPublished;
    public event Action? Closed;

    public async Task InitializeAsync(string root)
    {
        _connection.Start();
        var parameters = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = LspParsers.ToUri(root),
            ["rootPath"] = root,
            ["workspaceFolders"] = new[] { new { uri = LspParsers.ToUri(root), name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) } },
            ["capabilities"] = new
            {
                textDocument = new
                {
                    synchronization = new { didSave = true },
                    publishDiagnostics = new { relatedInformation = false },
                    completion = new { completionItem = new { snippetSupport = false, documentationFormat = new[] { "plaintext", "markdown" } } },
                    hover = new { contentFormat = new[] { "plaintext", "markdown" } },
                    definition = new { linkSupport = false },
                    rename = new { prepareSupport = false },
                },
                workspace = new { workspaceFolders = true, configuration = true },
            },
        };

        using var timeout = new CancellationTokenSource(InitializeTimeout);
        var result = await _connection.RequestAsync("initialize", parameters, timeout.Token).ConfigureAwait(false);
        ReadCapabilities(result);
        _connection.Notify("initialized", new { });
    }

    private void ReadCapabilities(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("capabilities", out var capabilities))
            return;

        SupportsDefinition = IsEnabled(capabilities, "definitionProvider");
        SupportsHover = IsEnabled(capabilities, "hoverProvider");
        SupportsRename = IsEnabled(capabilities, "renameProvider");
        SupportsCompletion = IsEnabled(capabilities, "completionProvider");
        if (capabilities.TryGetProperty("completionProvider", out var completion)
            && completion.ValueKind == JsonValueKind.Object
            && completion.TryGetProperty("triggerCharacters", out var triggers)
            && triggers.ValueKind == JsonValueKind.Array)
        {
            CompletionTriggers = triggers.EnumerateArray()
                .Select(t => t.GetString())
                .Where(t => t is { Length: 1 })
                .Select(t => t![0])
                .ToList();
        }
    }

    private static bool IsEnabled(JsonElement capabilities, string name) =>
        capabilities.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);

    public void DidOpen(string path, string languageId, int version, string text) =>
        _connection.Notify("textDocument/didOpen", new { textDocument = new { uri = LspParsers.ToUri(path), languageId, version, text } });

    // Envia o texto inteiro, que todo servidor aceita, em vez de calcular cada diferença.
    public void DidChange(string path, int version, string text) =>
        _connection.Notify("textDocument/didChange", new
        {
            textDocument = new { uri = LspParsers.ToUri(path), version },
            contentChanges = new[] { new { text } },
        });

    public void DidSave(string path) =>
        _connection.Notify("textDocument/didSave", new { textDocument = new { uri = LspParsers.ToUri(path) } });

    public void DidClose(string path) =>
        _connection.Notify("textDocument/didClose", new { textDocument = new { uri = LspParsers.ToUri(path) } });

    public async Task<List<CompletionEntry>> CompletionAsync(string path, int line, int character, CancellationToken cancellation)
    {
        var result = await _connection.RequestAsync("textDocument/completion", Position(path, line, character), cancellation);
        return LspParsers.Completion(result);
    }

    public async Task<LspLocation?> DefinitionAsync(string path, int line, int character, CancellationToken cancellation)
    {
        var result = await _connection.RequestAsync("textDocument/definition", Position(path, line, character), cancellation);
        return LspParsers.Definition(result);
    }

    public async Task<string?> HoverAsync(string path, int line, int character, CancellationToken cancellation)
    {
        var result = await _connection.RequestAsync("textDocument/hover", Position(path, line, character), cancellation);
        return LspParsers.Hover(result);
    }

    public async Task<Dictionary<string, List<LspTextEdit>>> RenameAsync(string path, int line, int character, string newName, CancellationToken cancellation)
    {
        var parameters = new
        {
            textDocument = new { uri = LspParsers.ToUri(path) },
            position = new { line, character },
            newName,
        };
        var result = await _connection.RequestAsync("textDocument/rename", parameters, cancellation);
        return LspParsers.WorkspaceEdit(result);
    }

    public void Dispose()
    {
        _owner.Dispose();
        _connection.Dispose();
    }

    private static object Position(string path, int line, int character) => new
    {
        textDocument = new { uri = LspParsers.ToUri(path) },
        position = new { line, character },
    };

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method == "textDocument/publishDiagnostics" && parameters.TryGetProperty("uri", out var uri))
            DiagnosticsPublished?.Invoke(LspParsers.ToPath(uri.GetString()!), LspParsers.Diagnostics(parameters));
    }
}
