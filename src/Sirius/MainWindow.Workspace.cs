using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Editing;

namespace Sirius;

// O dia a dia: vigiar o disco, reabrir as abas, aba sem nome e substituir no projeto.
public partial class MainWindow
{
    private const int MaxReloadRetries = 10;
    private const int MaxDiskMoves = 500;

    private readonly List<(string Old, string New)> _diskRenames = new();
    private readonly List<string> _diskCreated = new();

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _diskDelay;
    private bool _treeChangedOnDisk;
    private bool _checkingDisk;
    private bool _sessionFrozen;
    private bool _restoringSession;
    private int _reloadRetries;
    private int _nextUntitled = 1;

    // Guardado na thread da interface: as threads de fundo nunca devem chamar Dispatcher.UIThread.
    private readonly Dispatcher _uiDispatcher = Dispatcher.UIThread;

    // Os testes respondem sem abrir a janela de escolher arquivo.
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    // Salvar sozinho: um segundo depois da última tecla, para a tela e o disco não ficarem diferentes, o que
    // confundia o Claude Code, o Executar e o Git. Os testes encurtam a espera.
    public TimeSpan AutoSaveDelay { get; set; } = TimeSpan.FromSeconds(1);
    private DispatcherTimer? _autoSaveTimer;

    private void ScheduleAutoSave()
    {
        if (!_settings.AutoSave)
            return;
        _autoSaveTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _autoSaveTimer.Stop();
        _autoSaveTimer.Interval = AutoSaveDelay;
        _autoSaveTimer.Tick -= OnAutoSaveTick;
        _autoSaveTimer.Tick += OnAutoSaveTick;
        _autoSaveTimer.Start();
    }

    private void OnAutoSaveTick(object? sender, EventArgs e) => AutoSaveNow();

    // Aba sem nome pediria um lugar, e um arquivo que outro programa mudou ou apagou espera a pessoa decidir:
    // salvar sozinho por cima apagaria o trabalho de alguém sem perguntar.
    public void AutoSaveNow()
    {
        _autoSaveTimer?.Stop();
        if (!_settings.AutoSave)
            return;
        var saved = false;
        foreach (var document in _documents.Where(d => d is { IsDirty: true, IsUntitled: false, IsImage: false }).ToList())
        {
            if (document.ChangedOnDisk || document.MissingOnDisk)
                continue;
            try
            {
                document.Save();
                _language.DocumentSaved(document);
                saved = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShowStatus($"Não foi possível salvar {document.Name} sozinho.");
            }
        }
        if (saved)
        {
            RefreshTabs();
            RefreshTree();
        }
    }

    private void InitializeWorkspace()
    {
        PickSavePath = PickSavePathAsync;
        _diskDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => OnDiskDelayElapsed());
        Activated += (_, _) =>
        {
            _ = CheckOpenDocumentsAsync();
            _ = RefreshGitAsync();
        };
        ReplaceInput.AddHandler(KeyDownEvent, OnReplaceKeyDown, RoutingStrategies.Tunnel);
    }

    // Vigiar o disco

    private void StartWatching(string root)
    {
        StopWatching();
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, e) => OnDiskEvent(root, e.FullPath, true, created: true);
            watcher.Deleted += (_, e) => OnDiskEvent(root, e.FullPath, true);
            watcher.Renamed += (_, e) => OnDiskEvent(root, e.FullPath, true, renamedFrom: e.OldFullPath);
            watcher.Changed += (_, e) => OnDiskEvent(root, e.FullPath, false);
            // Quando muita coisa muda de uma vez, o Windows descarta avisos: relemos tudo.
            watcher.Error += (_, _) => OnDiskEvent(root, root, true);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Sem vigilância a IDE continua funcionando; a árvore só não se atualiza sozinha.
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // Chega numa thread de fundo.
    private void OnDiskEvent(string root, string path, bool structural, bool created = false, string? renamedFrom = null)
    {
        var ignored = ProjectTree.IsIgnored(root, path);
        // O interior do .git só importa quando muda o histórico ou o que está preparado.
        if (ignored && !IsGitStateFile(root, path) && renamedFrom is null)
            return;

        _uiDispatcher.Post(() =>
        {
            if (RootFolder != root)
                return;
            // Guardados para as abas seguirem o nome novo. Uma enxurrada, como um npm install, só relê tudo.
            if (renamedFrom is not null && _diskRenames.Count < MaxDiskMoves)
                _diskRenames.Add((renamedFrom, path));
            else if (created && !ignored && _diskCreated.Count < MaxDiskMoves)
                _diskCreated.Add(path);
            _treeChangedOnDisk |= structural;
            if (_diskDelay is { IsEnabled: false })
                _diskDelay.Start();
        });
    }

    private static bool IsGitStateFile(string root, string path)
    {
        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return false;
        var relative = Path.GetRelativePath(root, path);
        var git = ".git" + Path.DirectorySeparatorChar;
        if (!relative.StartsWith(git, StringComparison.OrdinalIgnoreCase))
            return false;
        var inside = relative[git.Length..];
        return inside is "HEAD" or "index" or "MERGE_HEAD" || inside.StartsWith("refs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDiskDelayElapsed()
    {
        _diskDelay!.Stop();
        FollowMovesOnDisk();
        if (_treeChangedOnDisk)
        {
            _treeChangedOnDisk = false;
            RefreshTree();
            ReindexProject();
        }
        _ = CheckOpenDocumentsAsync();
        _ = RefreshGitAsync();
    }

    // Outro programa, como o Explorer, renomeou ou moveu arquivos abertos: as abas seguem o nome novo, como
    // quando se renomeia pela árvore.
    private void FollowMovesOnDisk()
    {
        if (_diskRenames.Count == 0 && _diskCreated.Count == 0)
            return;
        var renames = _diskRenames.ToList();
        var created = _diskCreated.ToList();
        _diskRenames.Clear();
        _diskCreated.Clear();

        var moved = new List<(string OldPath, string NewPath)>();
        foreach (var document in _documents.Where(d => !d.IsUntitled))
        {
            if (DiskMoves.Follow(document.FullPath, document.LastWriteUtc, renames, created) is not { } target)
                continue;
            var oldPath = document.FullPath;
            document.MoveTo(target);
            document.MissingReported = false;
            _language.DocumentMoved(document, oldPath);
            moved.Add((oldPath, target));
        }

        foreach (var folder in _expanded.ToList())
        {
            var target = DiskMoves.Apply(folder, renames);
            if (target != folder && Directory.Exists(target) && (!Directory.Exists(folder) || string.Equals(target, folder, StringComparison.OrdinalIgnoreCase)))
            {
                _expanded.Remove(folder);
                _expanded.Add(target);
            }
        }
        if (_lastFolderClicked is not null && !Directory.Exists(_lastFolderClicked) && DiskMoves.Apply(_lastFolderClicked, renames) is var clicked && Directory.Exists(clicked))
            _lastFolderClicked = clicked;

        if (moved.Count == 0)
            return;
        AfterPathsMoved();
        SaveSession();
        var (first, firstTarget) = moved[0];
        ShowStatus(moved.Count > 1
            ? $"{moved.Count} arquivos abertos mudaram de nome ou de pasta, e as abas acompanharam."
            : string.Equals(Path.GetDirectoryName(first), Path.GetDirectoryName(firstTarget), StringComparison.OrdinalIgnoreCase)
                ? $"{Path.GetFileName(first)} foi renomeado para {Path.GetFileName(firstTarget)}."
                : $"{Path.GetFileName(first)} foi movido e agora está {DescribeFolder(Path.GetDirectoryName(firstTarget)!)}.");
    }

    public async Task CheckOpenDocumentsAsync()
    {
        if (_checkingDisk || IsDialogOpen)
            return;

        FollowMovesOnDisk();
        _checkingDisk = true;
        try
        {
            foreach (var document in _documents.ToList())
            {
                if (!_documents.Contains(document))
                    continue;

                if (document.MissingOnDisk)
                {
                    if (!document.MissingReported)
                    {
                        document.MissingReported = true;
                        ShowStatus(document.IsImage
                            ? $"{document.Name} não existe mais no disco."
                            : $"{document.Name} não existe mais no disco. Salve para recriar.");
                    }
                    continue;
                }
                document.MissingReported = false;

                if (!document.NeedsDiskAttention)
                    continue;

                if (document.IsDirty)
                {
                    var choice = await AskAsync($"{document.Name} mudou no disco",
                        "Outro programa alterou este arquivo, e você tem alterações não salvas aqui.",
                        "Usar a versão do disco", "Manter as minhas");
                    if (choice != DialogChoice.Primary)
                    {
                        document.AcknowledgeDiskVersion();
                        continue;
                    }
                }
                Reload(document);
            }
        }
        finally
        {
            _checkingDisk = false;
        }
    }

    private void Reload(OpenDocument document)
    {
        try
        {
            document.ReloadFromDisk();
            _reloadRetries = 0;
            RefreshImage();
            RefreshTabs();
            RefreshTree();
            ShowStatus($"{document.Name} foi atualizado do disco");
        }
        catch (TextFileException e)
        {
            document.AcknowledgeDiskVersion();
            ShowStatus(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // O outro programa ainda está gravando: tentamos de novo daqui a pouco.
            if (++_reloadRetries <= MaxReloadRetries)
                _diskDelay?.Start();
        }
    }

    // Reabrir as abas

    private static string SessionKey(string root) => root.ToLowerInvariant();

    private void SaveSession()
    {
        if (RootFolder is null || _sessionFrozen || _restoringSession)
            return;

        if (ActiveDocument is { } active)
        {
            active.CaretOffset = CodeEditor.TextArea.Caret.Offset;
            active.VerticalOffset = CodeEditor.VerticalOffset;
        }

        var key = SessionKey(RootFolder);
        var files = _documents.Where(document => !document.IsUntitled).ToList();
        var breakpoints = BreakpointsSnapshot();
        if (files.Count == 0 && breakpoints.Count == 0 && _watches.Count == 0)
        {
            _settings.Sessions.Remove(key);
        }
        else
        {
            _settings.Sessions[key] = new SessionState
            {
                ActivePath = ActiveDocument is { IsUntitled: false } current ? current.FullPath : null,
                Breakpoints = breakpoints.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
                BreakpointConditions = ConditionsSnapshot().ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary(), StringComparer.OrdinalIgnoreCase),
                Watches = _watches.ToList(),
                Files = files.Select(document => new SessionFile
                {
                    Path = document.FullPath,
                    CaretOffset = document.CaretOffset,
                    VerticalOffset = document.VerticalOffset,
                }).ToList(),
            };
        }
        _settings.Save(_settingsPath);
    }

    private void RestoreSession()
    {
        if (RootFolder is null || !_settings.Sessions.TryGetValue(SessionKey(RootFolder), out var session))
            return;

        // Antes de abrir as abas, que pegam daqui os pontos de cada arquivo.
        foreach (var (path, lines) in session.Breakpoints)
            _closedBreakpoints[path] = lines;
        _watches.Clear();
        _watches.AddRange(session.Watches);
        foreach (var (path, conditions) in session.BreakpointConditions)
            _closedConditions[path] = conditions;

        _restoringSession = true;
        try
        {
            _restoringTabs = true;
            foreach (var file in session.Files.Where(file => File.Exists(file.Path)))
                OpenFile(file.Path);
            _restoringTabs = false;

            foreach (var file in session.Files)
            {
                if (_documents.Find(document => string.Equals(document.FullPath, file.Path, StringComparison.OrdinalIgnoreCase)) is { } document)
                {
                    document.CaretOffset = file.CaretOffset;
                    document.VerticalOffset = file.VerticalOffset;
                }
            }

            // Sem documento ativo, a troca não grava por cima da posição que acabamos de restaurar.
            var target = _documents.Find(document => string.Equals(document.FullPath, session.ActivePath, StringComparison.OrdinalIgnoreCase))
                ?? _documents.LastOrDefault();
            ActiveDocument = null;
            if (target is not null)
            {
                Activate(target);
                var vertical = target.VerticalOffset;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ActiveDocument == target)
                        CodeEditor.ScrollToVerticalOffset(vertical);
                }, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            _restoringTabs = false;
            _restoringSession = false;
        }
    }

    // Aba sem nome, Salvar como e Salvar tudo

    public void NewUntitled()
    {
        var folder = RootFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var document = OpenDocument.CreateUntitled(folder, $"Sem título {_nextUntitled++}");
        Register(document);
        Activate(document);
    }

    private async Task<string?> PickSavePathAsync(string suggested)
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(suggested)!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar como",
            SuggestedFileName = Path.GetFileName(suggested),
            SuggestedStartLocation = start,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> SaveAsAsync(OpenDocument document)
    {
        if (document.IsImage)
            return false;

        var path = await PickSavePath!(document.FullPath);
        if (path is null)
            return false;

        var full = Path.GetFullPath(path);
        if (_documents.Any(other => other != document && string.Equals(other.FullPath, full, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus("Este arquivo já está aberto em outra aba. Feche-a antes.");
            return false;
        }

        var oldPath = document.FullPath;
        try
        {
            document.SaveTo(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Não foi possível salvar este arquivo.");
            return false;
        }

        _language.DocumentMoved(document, oldPath);
        if (ActiveDocument == document)
        {
            _colorizer.Highlighter = document.Highlighter;
            Breadcrumb.Text = RootFolder is null ? "" : BreadcrumbFor(full);
            CodeEditor.TextArea.TextView.Redraw();
        }
        UpdateServerHint();
        ReindexProject();
        RefreshTabs();
        RefreshTree();
        SaveSession();
        ShowStatus("Salvo");
        return true;
    }

    public async Task<bool> SaveAllAsync()
    {
        foreach (var document in _documents.Where(d => d.IsDirty).ToList())
        {
            if (!await SaveAsync(document))
                return false;
        }
        return true;
    }

    // Substituir no projeto

    private void OnReplaceClick(object? sender, RoutedEventArgs e) => _ = ReplaceAllAsync(ReplaceInput.Text ?? "");

    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ReplaceAllAsync(ReplaceInput.Text ?? "");
        }
    }

    public async Task ReplaceAllAsync(string replacement)
    {
        var text = SearchInput.Text ?? "";
        // Com os usos no painel, substituir trocaria também o texto igual que não é o mesmo nome.
        if (text.Length == 0 || RootFolder is not { } root || _usesQuery is not null)
            return;

        _searchDelay.Stop();
        _searchCancel?.Cancel();
        SearchStatus.Text = "Calculando…";

        var query = new SearchQuery(text, _matchCase, _useRegex);
        var open = _documents.Where(d => !d.IsUntitled)
            .ToDictionary(d => d.FullPath, d => d.Document.Text, StringComparer.OrdinalIgnoreCase);
        var plan = await Task.Run(() => ProjectReplace.Plan(ProjectTree.FindFiles(root), query, replacement, open, CancellationToken.None));

        if (plan.InvalidQuery)
        {
            SearchStatus.Text = "Expressão regular inválida.";
            return;
        }
        if (plan.Items.Count == 0)
        {
            SearchStatus.Text = "Nada encontrado.";
            return;
        }

        var occurrences = plan.Total == 1 ? "1 ocorrência" : $"{plan.Total} ocorrências";
        var files = plan.Items.Count == 1 ? "1 arquivo" : $"{plan.Items.Count} arquivos";
        SearchStatus.Text = $"{occurrences} em {files}";
        var closed = plan.Items.Count(item => item.Encoding is not null);
        var body = closed == 0
            ? "As alterações ficam nas abas abertas, sem salvar, e dá para desfazer com Ctrl Z."
            : $"{(closed == 1 ? "1 arquivo fechado será gravado" : $"{closed} arquivos fechados serão gravados")} direto no disco, sem como desfazer pelo Ctrl Z. As abas abertas ficam alteradas, sem salvar.";

        var choice = await AskAsync($"Substituir {occurrences} em {files}?", body, "Substituir", null);
        if (choice != DialogChoice.Primary)
        {
            await RunSearchAsync();
            return;
        }

        var failed = 0;
        foreach (var item in plan.Items)
        {
            if (item.Encoding is null)
            {
                _documents.Find(d => string.Equals(d.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase))?.SetTextKeepingPosition(item.NewText);
                continue;
            }

            try
            {
                TextFile.Save(item.FullPath, item.NewText, item.Encoding);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        RefreshTabs();
        RefreshTree();
        await RunSearchAsync();
        SearchStatus.Text = failed == 0
            ? $"Substituídas {occurrences} em {files}."
            : $"Substituídas em {plan.Items.Count - failed} de {plan.Items.Count} arquivos. {failed} não puderam ser gravados.";
    }

    // Pastas recentes

    public IReadOnlyList<RecentRow> RecentRows => RecentPanel.IsVisible && RecentList.ItemsSource is IReadOnlyList<RecentRow> rows ? rows : [];

    // As que ainda existem, sem a que está aberta.
    public List<string> OtherRecentFolders() => _settings.RecentFolders
        .Where(folder => !string.Equals(folder, RootFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
        .ToList();

    private static RecentRow RecentRowFor(string folder)
    {
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
        return new RecentRow(Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed, ShortFolder(Path.GetDirectoryName(trimmed)), folder);
    }

    // Só o fim do caminho, que é o que distingue duas pastas de mesmo nome. O caminho inteiro fica na dica.
    public static string ShortFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return "";
        var parts = folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? folder : "…" + Path.DirectorySeparatorChar + parts[^2] + Path.DirectorySeparatorChar + parts[^1];
    }

    private void UpdateRecentList()
    {
        var rows = RootFolder is null ? OtherRecentFolders().Select(RecentRowFor).ToList() : [];
        RecentList.ItemsSource = rows;
        RecentPanel.IsVisible = rows.Count > 0;
    }

    private void OnRecentFolderClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecentRow row)
            OpenRecentFolder(row.FullPath);
    }

    private void OnProjectNameClick(object? sender, RoutedEventArgs e) => OpenRecentFolders();

    public void OpenRecentFolders()
    {
        var items = OtherRecentFolders()
            .Select(RecentRowFor)
            .Select(row => (row.Name, row.Folder, (Action)(() => OpenRecentFolder(row.FullPath))))
            .ToList();
        items.Add(("Abrir outra pasta", "Ctrl O", () => _ = PickFolderAsync()));
        OpenPalette();
        _paletteOverride = items;
        RefreshPalette();
    }

    private void OpenRecentFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            _settings.RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            _settings.Save(_settingsPath);
            UpdateRecentList();
            ShowStatus("Esta pasta não existe mais.");
            return;
        }
        OpenFolder(folder);
    }
}
