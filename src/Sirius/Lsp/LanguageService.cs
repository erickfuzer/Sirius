using Avalonia.Threading;
using Sirius.Editing;

namespace Sirius.Lsp;

// Mantém um servidor por linguagem e os documentos abertos em dia com ele.
// Tudo aqui acontece na thread da interface.
public sealed class LanguageService : IDisposable
{
    private sealed class OpenEntry
    {
        public required string LanguageId { get; init; }
        public required string ServerId { get; init; }
        public required string Path { get; set; }
        public int Version { get; set; } = 1;
        public bool ChangePending { get; set; }
        public bool Announced { get; set; }
    }

    private readonly Dictionary<string, Task<LanguageClient?>> _clients = new();
    private readonly Dictionary<OpenDocument, OpenEntry> _open = new();
    private readonly Dictionary<string, List<LspDiagnostic>> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _changeDelay;
    private readonly Dispatcher _uiDispatcher = Dispatcher.UIThread;
    private string? _root;

    public LanguageService()
    {
        _changeDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => FlushChanges());
    }

    // Os testes trocam o programa real por um servidor falso.
    public Func<ServerSpec, string, ServerConnection?> Launcher { get; set; } = LanguageServerCatalog.Launch;
    public Func<ServerSpec, bool> IsInstalled { get; set; } = server => LanguageServerCatalog.FindExecutable(server.Executable) is not null;

    public event Action? DiagnosticsChanged;
    public event Action<ServerSpec>? ServerFailed;

    public IEnumerable<(string Path, LspDiagnostic Diagnostic)> AllDiagnostics =>
        _diagnostics.SelectMany(file => file.Value.Select(diagnostic => (file.Key, diagnostic)));

    public IReadOnlyList<LspDiagnostic> DiagnosticsFor(string path) =>
        _diagnostics.TryGetValue(path, out var list) ? list : [];

    // O servidor desta linguagem que falta instalar, se houver.
    public ServerSpec? MissingServerFor(string path)
    {
        var entry = LanguageServerCatalog.ForPath(path);
        return entry is { } value && !IsInstalled(value.Server) ? value.Server : null;
    }

    public void SetRoot(string? root)
    {
        ShutdownAll();
        _root = root;
    }

    public void ShutdownAll()
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsCompletedSuccessfully)
                client.Result?.Dispose();
            else
                client.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);
        }
        _clients.Clear();
        _open.Clear();
        if (_diagnostics.Count > 0)
        {
            _diagnostics.Clear();
            DiagnosticsChanged?.Invoke();
        }
    }

    public void Retry(IEnumerable<OpenDocument> documents, ServerSpec server)
    {
        if (_clients.Remove(server.Id, out var previous) && previous.IsCompletedSuccessfully)
            previous.Result?.Dispose();
        foreach (var document in documents)
        {
            if (LanguageServerCatalog.ForPath(document.FullPath)?.Server.Id == server.Id)
            {
                _open.Remove(document);
                DocumentOpened(document);
            }
        }
    }

    public void DocumentOpened(OpenDocument document)
    {
        if (_root is null || _open.ContainsKey(document) || LanguageServerCatalog.ForPath(document.FullPath) is not { } language)
            return;
        if (!IsInstalled(language.Server))
            return;

        _open[document] = new OpenEntry { LanguageId = language.LanguageId, ServerId = language.Server.Id, Path = document.FullPath };
        _ = AnnounceAsync(document, language.Server);
    }

    private async Task AnnounceAsync(OpenDocument document, ServerSpec server)
    {
        var client = await ClientFor(server);
        if (client is null || !_open.TryGetValue(document, out var entry) || entry.Announced)
            return;

        entry.Announced = true;
        entry.ChangePending = false;
        client.DidOpen(entry.Path, entry.LanguageId, entry.Version, document.Document.Text);
    }

    private Task<LanguageClient?> ClientFor(ServerSpec server)
    {
        if (!_clients.TryGetValue(server.Id, out var task))
        {
            task = StartClientAsync(server, _root!);
            _clients[server.Id] = task;
        }
        return task;
    }

    private async Task<LanguageClient?> StartClientAsync(ServerSpec server, string root)
    {
        LanguageClient? client = null;
        try
        {
            var connection = await Task.Run(() => Launcher(server, root));
            if (connection is null)
                return null;

            client = new LanguageClient(connection);
            client.DiagnosticsPublished += (path, list) => _uiDispatcher.Post(() => Publish(path, list));
            client.Closed += () => _uiDispatcher.Post(() => OnServerClosed(server, root));
            await client.InitializeAsync(root);
            return client;
        }
        catch (Exception e) when (e is LspException or OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            client?.Dispose();
            ServerFailed?.Invoke(server);
            return null;
        }
    }

    private void OnServerClosed(ServerSpec server, string root)
    {
        // Encerramento pedido por nós já tirou o servidor da lista.
        if (_root != root || !_clients.Remove(server.Id))
            return;

        foreach (var (document, entry) in _open.Where(pair => pair.Value.ServerId == server.Id).ToList())
        {
            _open.Remove(document);
            _diagnostics.Remove(entry.Path);
        }
        DiagnosticsChanged?.Invoke();
        ServerFailed?.Invoke(server);
    }

    private void Publish(string path, List<LspDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            _diagnostics.Remove(path);
        else
            _diagnostics[path] = diagnostics;
        DiagnosticsChanged?.Invoke();
    }

    public void DocumentChanged(OpenDocument document)
    {
        if (!_open.TryGetValue(document, out var entry))
            return;
        entry.Version++;
        entry.ChangePending = true;
        _changeDelay.Stop();
        _changeDelay.Start();
    }

    public void FlushChanges()
    {
        _changeDelay.Stop();
        foreach (var (document, entry) in _open)
        {
            if (entry.ChangePending && entry.Announced && ReadyClient(entry.ServerId) is { } client)
            {
                entry.ChangePending = false;
                client.DidChange(entry.Path, entry.Version, document.Document.Text);
            }
        }
    }

    public void DocumentSaved(OpenDocument document)
    {
        FlushChanges();
        if (_open.TryGetValue(document, out var entry) && entry.Announced)
            ReadyClient(entry.ServerId)?.DidSave(entry.Path);
    }

    public void DocumentClosed(OpenDocument document)
    {
        if (!_open.Remove(document, out var entry))
            return;
        if (entry.Announced)
            ReadyClient(entry.ServerId)?.DidClose(entry.Path);
        if (_diagnostics.Remove(entry.Path))
            DiagnosticsChanged?.Invoke();
    }

    // O arquivo mudou de nome: para o servidor, é fechar um e abrir outro.
    public void DocumentMoved(OpenDocument document, string oldPath)
    {
        if (_open.Remove(document, out var entry))
        {
            if (entry.Announced)
                ReadyClient(entry.ServerId)?.DidClose(oldPath);
            if (_diagnostics.Remove(oldPath))
                DiagnosticsChanged?.Invoke();
        }
        DocumentOpened(document);
    }

    // O cliente pronto para este documento, com o servidor já sabendo do texto atual.
    public LanguageClient? ClientForDocument(OpenDocument document)
    {
        if (!_open.TryGetValue(document, out var entry) || !entry.Announced)
            return null;
        FlushChanges();
        return ReadyClient(entry.ServerId);
    }

    private LanguageClient? ReadyClient(string serverId) =>
        _clients.TryGetValue(serverId, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    public void Dispose() => ShutdownAll();
}
