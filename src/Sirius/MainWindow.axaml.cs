using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Search;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Theming;

namespace Sirius;

public partial class MainWindow : Window
{
    private enum DialogChoice { Primary, Secondary, Cancel }

    private static readonly Geometry ChevronClosed = Geometry.Parse("M4 2L8 6L4 10");
    private static readonly Geometry ChevronOpen = Geometry.Parse("M2 4L6 8L10 4");
    private const int PaletteFileLimit = 50;
    // Quantas das primeiras linhas da barra são arquivos, que ganham o ícone.
    private int _paletteFileCount;
    private const double SidebarMinWidth = 160;
    private const double SidebarMaxWidth = 640;
    private const double EditorMinWidth = 320;
    private const double SearchMinWidth = 300;
    private const double EditorDefaultFontSize = 13.5;
    private const double EditorMinFontSize = 9;
    private const double EditorMaxFontSize = 32;

    private readonly RowList<TreeRow> _treeRows = new();
    private readonly ObservableCollection<TabRow> _tabRows = new();
    private readonly ObservableCollection<CommandRow> _commandRows = new();
    private readonly List<OpenDocument> _documents = new();
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly TypographicColorizer _colorizer = new();
    private readonly GutterMargin _gutter;
    private readonly DispatcherTimer _statusTimer;
    private readonly SearchPanel _search;
    private readonly ObservableCollection<SearchRow> _searchRows = new();
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _redrawDelay;

    private CancellationTokenSource? _searchCancel;
    private bool _matchCase;
    private bool _useRegex;
    private int _searchIndex = -1;
    private double _searchWidth = 340;
    private string? _lastFolderClicked;
    private Func<string, string?>? _dialogValidator;
    private List<string> _projectFiles = new();
    private List<CommandRow> _filteredCommands = new();
    private TaskCompletionSource<DialogChoice>? _dialog;
    private int _paletteIndex;
    private bool _allowClose;

    public MainWindow() : this(new AppSettings(), Path.Combine(Path.GetTempPath(), "sirius-design.json"), null)
    {
    }

    public MainWindow(AppSettings settings, string settingsPath, string? startPath)
    {
        InitializeComponent();
        _settings = settings;
        _settingsPath = settingsPath;
        FitToScreen();

        TreeList.ItemsSource = _treeRows;
        TabList.ItemsSource = _tabRows;
        PaletteList.ItemsSource = _commandRows;
        SearchList.ItemsSource = _searchRows;

        var area = CodeEditor.TextArea;
        _gutter = new GutterMargin(area);
        area.LeftMargins.Insert(0, _gutter);
        area.TextView.LineTransformers.Add(_colorizer);
        area.Caret.PositionChanged += (_, _) => UpdateCaretPosition();
        _search = SearchPanel.Install(CodeEditor);

        var options = CodeEditor.Options;
        options.AllowScrollBelowDocument = true;
        options.ConvertTabsToSpaces = true;
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.HighlightCurrentLine = false;

        PaletteInput.TextChanged += (_, _) => { _paletteIndex = 0; RefreshPalette(); };
        PaletteInput.AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        SearchInput.TextChanged += (_, _) => OnSearchTextChanged();
        SearchInput.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Normal, (_, _) => _ = RunSearchAsync());

        // Uma edição pode mudar a cor das linhas de baixo, como ao abrir um comentário.
        _redrawDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            _redrawDelay!.Stop();
            CodeEditor.TextArea.TextView.Redraw();
        });

        _statusTimer =new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => ClearStatus());

        InitializeTerminal();
        InitializeLanguage();
        InitializeWorkspace();
        InitializeGit();
        InitializeColors();
        InitializeImages();
        InitializeDrop();
        InitializeDebug();
        InitializeEditing();
        InitializeSvg();

        CodeEditor.AddHandler(PointerWheelChangedEvent, OnEditorWheel, RoutingStrategies.Tunnel);
        ApplyEditorFontSize(_settings.EditorFontSize);
        DialogInput.AddHandler(KeyDownEvent, OnDialogInputKeyDown, RoutingStrategies.Tunnel);
        Sidebar.Width = Math.Clamp(_settings.SidebarWidth, SidebarMinWidth, SidebarMaxWidth);
        TreeHeader.IsVisible = false;

        ApplyTheme(_settings.Theme);
        UpdateEmptyState();
        UpdateCaretPosition();
        ClearStatus();

        var start = startPath ?? _settings.LastFolder;
        if (start is not null && Directory.Exists(start))
            OpenFolder(start);
        else if (start is not null && File.Exists(start))
        {
            OpenFolder(Path.GetDirectoryName(Path.GetFullPath(start))!);
            OpenFile(start);
        }
    }

    public string? RootFolder { get; private set; }
    public Task ProjectIndexed { get; private set; } = Task.CompletedTask;
    public OpenDocument? ActiveDocument { get; private set; }
    public IReadOnlyList<OpenDocument> Documents => _documents;
    public IReadOnlyList<TreeRow> TreeRows => _treeRows;
    public IReadOnlyList<TabRow> TabRows => _tabRows;
    public bool IsPaletteOpen => PaletteOverlay.IsVisible;
    public bool IsDialogOpen => DialogOverlay.IsVisible;
    public bool IsFocusMode => !Sidebar.IsVisible;
    public double CurrentSidebarWidth => Sidebar.Width;
    public double EditorFontSize => CodeEditor.FontSize;

    // Os testes trocam a Lixeira real por uma falsa.
    public Action<string> MoveToTrash { get; set; } = FileOperations.MoveToTrash;
    public string ThemeName => Palette.Current.Name;
    public bool IsSearchOpen => SearchView.IsVisible;
    public IReadOnlyList<SearchRow> SearchRows => _searchRows;
    public string SearchStatusText => SearchStatus.Text ?? "";
    public int CaretLine => CodeEditor.TextArea.Caret.Line;
    public string SelectedText => CodeEditor.SelectedText;

    // Projeto

    public void OpenFolder(string folder)
    {
        SaveSession();
        _closedBreakpoints.Clear();
        _closedConditions.Clear();
        _watches.Clear();
        RootFolder = Path.GetFullPath(folder);
        _settings.LastFolder = RootFolder;
        _settings.RememberFolder(RootFolder);
        _settings.Save(_settingsPath);
        ProjectButton.IsVisible = true;
        _expanded.Clear();
        _projectFiles = new List<string>();
        _lastFolderClicked = null;
        TreeHeader.IsVisible = true;
        StopAllSessions();
        _language.SetRoot(RootFolder);
        CloseDiff();
        CloseGit();
        ResetHistory();
        _gitExcluded.Clear();
        _gitBaseTexts.Clear();
        GitState = null;
        _ = RefreshGitAsync();
        CloseSearch();
        ClearSearchResults();

        ProjectName.Text = Path.GetFileName(RootFolder.TrimEnd(Path.DirectorySeparatorChar));
        var branch = ProjectTree.CurrentBranch(RootFolder);
        BranchName.Text = branch ?? "";
        BranchPanel.IsVisible = branch is not null;

        RefreshTree();
        UpdateEmptyState();

        ReindexProject();
        StartWatching(RootFolder);
        RestoreSession();
    }

    private void ReindexProject()
    {
        if (RootFolder is not { } root)
            return;
        ProjectIndexed = Task.Run(() => ProjectTree.FindFiles(root)).ContinueWith(
            t => { if (RootFolder == root) _projectFiles = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshTree()
    {
        var rows = new List<TreeRow>();
        if (RootFolder is not null)
            AddTreeLevel(RootFolder, 0, rows);

        // Sem mudanças, a lista fica intacta: não pisca nem fecha um menu aberto.
        _treeRows.ReplaceAll(rows);
    }

    private void AddTreeLevel(string folder, int depth, List<TreeRow> rows)
    {
        var palette = Palette.Current;
        foreach (var entry in ProjectTree.List(folder))
        {
            var expanded = entry.IsFolder && _expanded.Contains(entry.FullPath);
            var active = !entry.IsFolder && string.Equals(ActiveDocument?.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase);
            var document = _documents.Find(d => string.Equals(d.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));

            var gitLetter = "";
            IBrush gitBrush = Brushes.Transparent;
            if (entry.IsFolder)
            {
                if (_gitChangedFolders.Contains(entry.FullPath))
                {
                    gitLetter = "•";
                    gitBrush = palette.Muted;
                }
            }
            else if (_gitMarks.TryGetValue(entry.FullPath, out var mark))
            {
                gitLetter = mark.Letter;
                gitBrush = BrushOf(mark.Kind);
            }

            rows.Add(new TreeRow(
                entry.Name, entry.FullPath, entry.IsFolder,
                new Thickness(6 + depth * 12, 0, 0, 0),
                entry.IsFolder ? (expanded ? ChevronOpen : ChevronClosed) : null,
                active ? FontWeight.SemiBold : FontWeight.Normal,
                active ? palette.Tint : Brushes.Transparent,
                entry.IsFolder ? palette.Muted : gitLetter.Length > 0 ? gitBrush : palette.Ink,
                document?.IsDirty ?? false,
                gitLetter, gitBrush,
                entry.IsFolder ? null : FileIcons.For(entry.Name)));
            if (expanded)
                AddTreeLevel(entry.FullPath, depth + 1, rows);
        }
    }

    private void OnTreeRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TreeRow row)
            return;
        if (row.IsFolder)
        {
            _lastFolderClicked = row.FullPath;
            if (!_expanded.Remove(row.FullPath))
                _expanded.Add(row.FullPath);
            RefreshTree();
        }
        else
        {
            _lastFolderClicked = Path.GetDirectoryName(row.FullPath);
            OpenFile(row.FullPath);
        }
    }

    // Criar e excluir na árvore

    // Onde os botões do topo criam: a última pasta usada, a pasta do arquivo aberto ou a raiz.
    private string? DefaultTargetFolder()
    {
        if (_lastFolderClicked is not null && Directory.Exists(_lastFolderClicked))
            return _lastFolderClicked;
        if (ActiveDocument is { } document)
            return Path.GetDirectoryName(document.FullPath);
        return RootFolder;
    }

    private static string FolderOf(TreeRow row) =>
        row.IsFolder ? row.FullPath : Path.GetDirectoryName(row.FullPath)!;

    private void OnNewFile(object? sender, RoutedEventArgs e) => _ = CreateFileAsync(DefaultTargetFolder());
    private void OnNewFolder(object? sender, RoutedEventArgs e) => _ = CreateFolderAsync(DefaultTargetFolder());
    private void OnNewFileAtRoot(object? sender, RoutedEventArgs e) => _ = CreateFileAsync(RootFolder);
    private void OnNewFolderAtRoot(object? sender, RoutedEventArgs e) => _ = CreateFolderAsync(RootFolder);

    private void OnTreeNewFile(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TreeRow row)
            _ = CreateFileAsync(FolderOf(row));
    }

    private void OnTreeNewFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TreeRow row)
            _ = CreateFolderAsync(FolderOf(row));
    }

    private void OnTreeDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TreeRow row)
            _ = DeleteAsync(row.FullPath);
    }

    private void OnTreeRename(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TreeRow row)
            _ = RenameAsync(row.FullPath);
    }

    private string? ValidateRename(string path, string name)
    {
        var problem = FileOperations.ValidateName(name);
        if (problem is not null)
            return problem;
        var onlyCaseChanged = string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        return !onlyCaseChanged && FileOperations.Exists(Path.Combine(Path.GetDirectoryName(path)!, name))
            ? "Já existe um item com este nome."
            : null;
    }

    public async Task RenameAsync(string path)
    {
        var current = Path.GetFileName(path);
        var nameLength = Directory.Exists(path) ? current.Length : Path.GetFileNameWithoutExtension(current).Length;
        var name = await AskTextAsync($"Renomear {current}", "Digite o novo nome.", "Renomear", n => ValidateRename(path, n), current, nameLength);
        if (name is null || name == current)
            return;

        string target;
        try
        {
            target = FileOperations.Rename(path, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Não foi possível renomear. Algum programa pode estar usando este item.");
            return;
        }

        // Abas, pastas abertas e resultados de busca apontavam para o nome antigo.
        foreach (var document in _documents.Where(d => FileOperations.IsInside(d.FullPath, path)))
        {
            var oldPath = document.FullPath;
            document.MoveTo(target + oldPath[path.Length..]);
            _language.DocumentMoved(document, oldPath);
        }
        foreach (var folder in _expanded.Where(f => FileOperations.IsInside(f, path)).ToList())
        {
            _expanded.Remove(folder);
            _expanded.Add(target + folder[path.Length..]);
        }
        if (_lastFolderClicked is not null && FileOperations.IsInside(_lastFolderClicked, path))
            _lastFolderClicked = target + _lastFolderClicked[path.Length..];
        ClearSearchResults();

        if (ActiveDocument is { } active)
        {
            _colorizer.Highlighter = active.Highlighter;
            Breadcrumb.Text = BreadcrumbFor(active.FullPath);
            CodeEditor.TextArea.TextView.Redraw();
        }
        UpdateServerHint();
        ReindexProject();
        RefreshTabs();
        RefreshTree();
        ShowStatus("Renomeado");
    }

    private TreeRow? FocusedTreeRow() =>
        (FocusManager?.GetFocusedElement() as Control)?.DataContext as TreeRow;

    private string DescribeFolder(string folder)
    {
        var relative = Path.GetRelativePath(RootFolder!, folder);
        return relative == "." ? "na raiz do projeto" : "em " + relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string? ValidateNewName(string folder, string name)
    {
        var problem = FileOperations.ValidateName(name);
        if (problem is not null)
            return problem;
        return FileOperations.Exists(Path.Combine(folder, name)) ? "Já existe um item com este nome." : null;
    }

    public async Task CreateFileAsync(string? folder)
    {
        if (folder is null || RootFolder is null)
            return;
        var name = await AskTextAsync("Novo arquivo", $"O arquivo será criado {DescribeFolder(folder)}.", "Criar", n => ValidateNewName(folder, n));
        if (name is null)
            return;

        try
        {
            var path = FileOperations.CreateFile(folder, name);
            _lastFolderClicked = folder;
            _expanded.Add(folder);
            ReindexProject();
            OpenFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Não foi possível criar o arquivo.");
        }
    }

    public async Task CreateFolderAsync(string? folder)
    {
        if (folder is null || RootFolder is null)
            return;
        var name = await AskTextAsync("Nova pasta", $"A pasta será criada {DescribeFolder(folder)}.", "Criar", n => ValidateNewName(folder, n));
        if (name is null)
            return;

        try
        {
            var path = FileOperations.CreateFolder(folder, name);
            _lastFolderClicked = path;
            _expanded.Add(folder);
            _expanded.Add(path);
            RefreshTree();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Não foi possível criar a pasta.");
        }
    }

    public async Task DeleteAsync(string path)
    {
        var affected = _documents.Where(d => FileOperations.IsInside(d.FullPath, path)).ToList();
        var body = "Você poderá recuperar pela Lixeira do Windows.";
        if (affected.Any(d => d.IsDirty))
            body += " Há alterações não salvas aqui, e elas serão perdidas.";

        var choice = await AskAsync($"Mover {Path.GetFileName(path)} para a Lixeira?", body, "Mover para a Lixeira", null);
        if (choice != DialogChoice.Primary)
            return;

        try
        {
            MoveToTrash(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowStatus("Não foi possível excluir.");
            return;
        }

        foreach (var document in affected)
            Forget(document);
        if (_lastFolderClicked is not null && FileOperations.IsInside(_lastFolderClicked, path))
            _lastFolderClicked = null;
        _expanded.RemoveWhere(folder => FileOperations.IsInside(folder, path));
        ReindexProject();
        RefreshTabs();
        RefreshTree();
        ShowStatus("Movido para a Lixeira");
    }

    // Tira a aba sem perguntar nada. Quem chama já cuidou das alterações.
    private void Forget(OpenDocument document)
    {
        _language.DocumentClosed(document);
        KeepBreakpoints(document);
        var index = _documents.IndexOf(document);
        _documents.RemoveAt(index);
        if (ActiveDocument == document)
        {
            ActiveDocument = null;
            Activate(_documents.Count == 0 ? null : _documents[Math.Min(index, _documents.Count - 1)]);
        }
        SaveSession();
    }

    // Barra lateral

    private void OnSidebarDrag(object? sender, VectorEventArgs e) => ResizeSidebar(e.Vector.X);

    private void OnSidebarDragDone(object? sender, VectorEventArgs e)
    {
        if (!IsWidePanelOpen)
        {
            _settings.SidebarWidth = Sidebar.Width;
            _settings.Save(_settingsPath);
        }
    }

    public void ResizeSidebar(double delta)
    {
        var limit = Math.Max(SidebarMinWidth, Math.Min(SidebarMaxWidth, Bounds.Width - EditorMinWidth));
        Sidebar.Width = Math.Clamp(Sidebar.Width + delta, SidebarMinWidth, limit);
        if (IsWidePanelOpen)
            _searchWidth = Sidebar.Width;
    }

    private void SetSidebarVisible(bool visible)
    {
        Sidebar.IsVisible = visible;
        SidebarSplitter.IsVisible = visible;
        FocusButton.Classes.Set("on", !visible);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Abrir pasta", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await SwitchFolderAsync(path);
    }

    private async Task SwitchFolderAsync(string path)
    {
        if (!await ConfirmStopTaskAsync())
            return;

        // Fechar as abas aqui é por causa da troca de pasta: a sessão da pasta atual continua valendo.
        SaveSession();
        _sessionFrozen = true;
        var closed = await CloseAllAsync();
        _sessionFrozen = false;
        if (closed)
        {
            _sessionFrozen = true;
            OpenFolder(path);
            _sessionFrozen = false;
        }
    }

    // Arquivos e abas

    public void OpenFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var document = _documents.Find(d => string.Equals(d.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            try
            {
                document = ImageFile.IsImage(fullPath) ? OpenDocument.LoadImage(fullPath) : OpenDocument.Load(fullPath);
            }
            catch (Exception e) when (e is TextFileException or IOException or UnauthorizedAccessException)
            {
                ShowStatus(e is TextFileException ? e.Message : "Não foi possível abrir este arquivo.");
                return;
            }
            Register(document);
        }
        Activate(document);
    }

    private void Register(OpenDocument document)
    {
        document.Document.UndoStack.PropertyChanged += (_, _) => OnDirtyMaybeChanged();
        document.Document.Changed += (_, _) =>
        {
            _language.DocumentChanged(document);
            if (ActiveDocument == document)
                ScheduleGitMarks();
            if (ActiveDocument == document && document.Highlighter is not null && !_redrawDelay.IsEnabled)
                _redrawDelay.Start();
        };
        RestoreBreakpoints(document);
        _documents.Add(document);
        _language.DocumentOpened(document);
    }

    private void Activate(OpenDocument? document)
    {
        CloseCompletion();
        CloseColorPicker();
        DiffHost.IsVisible = false;
        if (ActiveDocument is not null)
        {
            ActiveDocument.CaretOffset = CodeEditor.TextArea.Caret.Offset;
            ActiveDocument.VerticalOffset = CodeEditor.VerticalOffset;
        }

        ActiveDocument = document;
        if (document is not null)
        {
            _colorizer.Highlighter = document.Highlighter;
            CodeEditor.Document = document.Document;
            CodeEditor.TextArea.Caret.Offset = Math.Min(document.CaretOffset, document.Document.TextLength);
            CodeEditor.ScrollToVerticalOffset(document.VerticalOffset);
            _gutter.SnapThread();
            CodeEditor.TextArea.TextView.Redraw();
            Dispatcher.UIThread.Post(FocusActiveDocument);
        }

        UpdateGutterBreakpoints();
        ShowDocumentSurface();
        RefreshImage();
        Breadcrumb.Text = document is null || RootFolder is null ? "" : BreadcrumbFor(document.FullPath);
        ToolTip.SetTip(Breadcrumb, document is { IsUntitled: false } ? Path.GetDirectoryName(document.FullPath) : null);
        ExpandTo(document?.FullPath);
        RefreshTabs();
        RefreshTree();
        UpdateEmptyState();
        UpdateCaretPosition();
        UpdateServerHint();
        SaveSession();
        _gutter.SetLineMarks(null);
        _ = UpdateGitMarksAsync();
    }

    private void ExpandTo(string? path)
    {
        if (path is null || RootFolder is null || !path.StartsWith(RootFolder, StringComparison.OrdinalIgnoreCase))
            return;
        for (var folder = Path.GetDirectoryName(path); folder is not null && folder.Length > RootFolder.Length; folder = Path.GetDirectoryName(folder))
            _expanded.Add(folder);
    }

    private string BreadcrumbFor(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var relative = Path.GetRelativePath(RootFolder!, folder);
        // Arquivo de fora do projeto: o fim do caminho diz mais que uma fila de "..", e o inteiro empurraria as abas.
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return ShortFolder(folder);
        return relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar.ToString(), " / ");
    }

    private void RefreshTabs()
    {
        var palette = Palette.Current;
        _tabRows.Clear();
        foreach (var document in _documents)
        {
            var active = document == ActiveDocument;
            _tabRows.Add(new TabRow(document.Name, document.FullPath,
                active ? FontWeight.SemiBold : FontWeight.Normal,
                active ? palette.Ink : palette.Muted, document.IsDirty, FileIcons.For(document.Name)));
        }
    }

    private void OnDirtyMaybeChanged()
    {
        if (_tabRows.Zip(_documents).Any(pair => pair.First.IsDirty != pair.Second.IsDirty))
        {
            RefreshTabs();
            RefreshTree();
        }
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (DocumentFrom(sender) is { } document)
            Activate(document);
    }

    private async void OnTabClose(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DocumentFrom(sender) is { } document)
            await CloseDocumentAsync(document);
    }

    private async void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Visual).Properties.IsMiddleButtonPressed && DocumentFrom(sender) is { } document)
        {
            e.Handled = true;
            await CloseDocumentAsync(document);
        }
    }

    private OpenDocument? DocumentFrom(object? sender) =>
        (sender as Control)?.DataContext is TabRow row ? _documents.Find(d => d.FullPath == row.FullPath) : null;

    public async Task<bool> CloseDocumentAsync(OpenDocument document)
    {
        if (document.IsDirty)
        {
            Activate(document);
            var choice = await AskAsync($"Salvar {document.Name}?", "Suas alterações serão perdidas se você não salvar.", "Salvar", "Descartar");
            if (choice == DialogChoice.Cancel || (choice == DialogChoice.Primary && !await SaveAsync(document)))
                return false;
        }

        Forget(document);
        RefreshTabs();
        RefreshTree();
        return true;
    }

    private async Task<bool> CloseAllAsync()
    {
        foreach (var document in _documents.ToList())
        {
            if (!await CloseDocumentAsync(document))
                return false;
        }
        return true;
    }

    public async Task<bool> SaveAsync(OpenDocument document)
    {
        if (document.IsImage)
            return true;

        if (document.IsUntitled)
            return await SaveAsAsync(document);

        if (document.ChangedOnDisk)
        {
            var choice = await AskAsync($"{document.Name} mudou no disco", "Outro programa alterou este arquivo depois que você o abriu. Salvar agora substitui essa versão.", "Substituir", null);
            if (choice != DialogChoice.Primary)
                return false;
        }

        try
        {
            document.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Não foi possível salvar este arquivo.");
            return false;
        }

        _language.DocumentSaved(document);
        RefreshTabs();
        RefreshTree();
        ShowStatus("Salvo");
        return true;
    }

    // Teclado

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsDialogOpen)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                FinishDialog(DialogChoice.Cancel);
            }
            return;
        }

        if (e.Key == Key.Escape && IsColorPickerOpen)
        {
            e.Handled = true;
            CloseColorPicker();
            return;
        }

        if (e.Key == Key.Escape && IsSettingsOpen)
        {
            e.Handled = true;
            CloseSettings();
            return;
        }

        if (e.Key == Key.Escape && IsPaletteOpen)
        {
            e.Handled = true;
            ClosePalette();
            return;
        }

        if (e.Key == Key.Escape && IsSearchOpen && SearchInput.IsFocused)
        {
            e.Handled = true;
            CloseSearch();
            return;
        }

        if (e.Key == Key.Escape && IsDiffOpen && !TerminalHost.IsFocused)
        {
            e.Handled = true;
            CloseDiff();
            return;
        }

        if (e.Key == Key.Escape && IsGitOpen && GitMessage.IsFocused)
        {
            e.Handled = true;
            CloseGit();
            return;
        }

        // Com a lista de sugestões aberta, o Tab e o Esc são dela. Fora isso, num trecho com campos,
        // o Tab vai ao próximo, o Shift Tab volta e o Esc sai, como no VS Code.
        if (_completion is null && CodeEditor.TextArea.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift
                && MoveInSnippet(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && IsInSnippet)
            {
                e.Handled = true;
                EndSnippet();
                return;
            }
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && TryOpenPairOnEnter())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && TryDeleteEmptyPair())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (IsDebugPaused)
                DebugContinue();
            else
                await RunOrStopAsync();
            return;
        }

        if (e.Key == Key.F10 && IsDebugPaused)
        {
            e.Handled = true;
            DebugNext();
            return;
        }

        if (e.Key == Key.F11 && IsDebugPaused)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                DebugOut();
            else
                DebugInto();
            return;
        }

        if (e.Key == Key.F2 && FocusedTreeRow() is { } focusedRow)
        {
            e.Handled = true;
            await RenameAsync(focusedRow.FullPath);
            return;
        }

        if (e.Key == Key.F2 && CodeEditor.TextArea.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            await RenameSymbolAsync();
            return;
        }

        // Sem exigir o foco no editor: quem clicou em um botão ou voltou do navegador ainda olha para o código.
        if (e.Key == Key.F12 && !TerminalHost.IsFocused)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                await FindUsesAsync();
            else
                await GoToDefinitionAsync();
            return;
        }

        if (HandleAltKey(e))
        {
            e.Handled = true;
            return;
        }

        // No Windows o AltGr chega como Ctrl e Alt juntos. Em teclados brasileiros ele digita
        // caracteres como ? e /, então nunca pode ser tratado como atalho.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            return;

        // No terminal, estas combinações pertencem ao programa que está rodando.
        if (TerminalHost.IsFocused && e.Key is Key.S or Key.O or Key.W or Key.N or Key.OemPeriod
            or Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)
            return;

        switch (e.Key)
        {
            case Key.Space when CodeEditor.TextArea.IsKeyboardFocusWithin:
                e.Handled = true;
                await RequestCompletionAsync(explicitRequest: true);
                break;
            case Key.OemPlus or Key.Add when IsImageShown:
                e.Handled = true;
                ZoomImage(1);
                break;
            case Key.OemMinus or Key.Subtract when IsImageShown:
                e.Handled = true;
                ZoomImage(-1);
                break;
            case Key.D0 or Key.NumPad0 when IsImageShown:
                e.Handled = true;
                ZoomImage(0);
                break;
            case Key.OemPlus or Key.Add:
                e.Handled = true;
                SetEditorFontSize(EditorFontSize + 1);
                break;
            case Key.OemMinus or Key.Subtract:
                e.Handled = true;
                SetEditorFontSize(EditorFontSize - 1);
                break;
            case Key.D0 or Key.NumPad0:
                e.Handled = true;
                SetEditorFontSize(EditorDefaultFontSize);
                break;
            case Key.J:
                e.Handled = true;
                ToggleTerminal();
                break;
            case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && RootFolder is not null:
                e.Handled = true;
                OpenGit();
                break;
            case Key.G when !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && ActiveDocument is { IsImage: false } && !TerminalHost.IsFocused:
                e.Handled = true;
                OpenGoToLine();
                break;
            // O / do teclado americano é o ; do ABNT2, que também tem uma tecla / só sua, e há o / do teclado numérico.
            case Key.Oem2 or Key.AbntC1 or Key.Divide when CodeEditor.TextArea.IsKeyboardFocusWithin:
                e.Handled = true;
                ToggleComment();
                break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && RootFolder is not null:
                e.Handled = true;
                OpenSearch();
                break;
            case Key.K:
                e.Handled = true;
                OpenPalette();
                break;
            case Key.N:
                e.Handled = true;
                NewUntitled();
                break;
            case Key.S when ActiveDocument is not null && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                await SaveAsAsync(ActiveDocument);
                break;
            case Key.S when ActiveDocument is not null:
                e.Handled = true;
                await SaveAsync(ActiveDocument);
                break;
            case Key.O:
                e.Handled = true;
                await PickFolderAsync();
                break;
            case Key.W when ActiveDocument is not null:
                e.Handled = true;
                await CloseDocumentAsync(ActiveDocument);
                break;
            case Key.OemPeriod:
                e.Handled = true;
                ToggleFocus();
                break;
            case Key.OemComma when !TerminalHost.IsFocused:
                e.Handled = true;
                OpenSettings();
                break;
        }
    }

    // Barra de comando

    private void OnOpenPalette(object? sender, RoutedEventArgs e) => OpenPalette();

    public void OpenPalette() => OpenPalette("");

    public void OpenPalette(string initial)
    {
        _paletteOverride = null;
        SettingsOverlay.IsVisible = false;
        PaletteOverlay.IsVisible = true;
        PaletteInput.Text = initial;
        _paletteIndex = 0;
        RefreshPalette();
        Dispatcher.UIThread.Post(() =>
        {
            PaletteInput.Focus();
            PaletteInput.CaretIndex = initial.Length;
        });
    }

    public void ClosePalette()
    {
        PaletteOverlay.IsVisible = false;
        FocusActiveDocument();
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e) => ClosePalette();

    private List<(string Name, string Hint, Action Run)> AllCommands(string query)
    {
        _paletteFileCount = 0;
        if (_paletteOverride is { } overrides)
            return overrides.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (LineCommands(query) is { } lineCommands)
            return lineCommands;

        var commands = new List<(string Name, string Hint, Action Run)>();
        _paletteFileCount = 0;
        var matches = _projectFiles
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(query.Length == 0 ? 6 : PaletteFileLimit);
        foreach (var file in matches)
            commands.Add((file.Name, RootFolder is null ? "" : BreadcrumbFor(file.Path), () => OpenFile(file.Path)));
        _paletteFileCount = commands.Count;

        var actions = new List<(string Name, string Hint, Action Run)>
        {
            ("Abrir pasta", "Ctrl O", () => _ = PickFolderAsync()),
            ("Trocar tema", Palette.Current == Palette.Claro ? "Grafite" : "Claro", ToggleTheme),
            ("Modo foco", "Ctrl .", ToggleFocus),
            ("Preferências", "Ctrl ,", OpenSettings),
            ("Quebrar linhas longas", (_settings.WordWrap ? "Ligado" : "Desligado") + " · Alt Z", ToggleWordWrap),
        };
        if (OtherRecentFolders().Count > 0)
            actions.Insert(1, ("Abrir pasta recente", "", OpenRecentFolders));
        if (RootFolder is not null)
        {
            actions.Insert(0, ("Buscar no projeto", "Ctrl Shift F", OpenSearch));
            actions.Add(("Abrir Git", "Ctrl Shift G", OpenGit));
            actions.Add((IsListeningToServer ? "Parar de ouvir o servidor" : "Depurar páginas do servidor", "Apache", () => _ = ToggleServerDebugAsync()));
            actions.Add(("Desligar o Xdebug do php.ini", "Apache", () => _ = DisableServerXdebugAsync()));
            if (GitState is not null)
            {
                actions.Add(("Enviar commits", "git push", () => _ = PushAsync()));
                actions.Add(("Receber novidades", "git pull", () => _ = PullAsync()));
                actions.Add(("Trocar de branch", "", () => _ = OpenBranchPickerAsync()));
                if (GitState.Remotes.Count > 0)
                    actions.Add(("Trocar o endereço do remoto", "git remote set-url", () => _ = ChangeRemoteUrlAsync()));
                actions.Add(("Histórico de commits", "", OpenGitHistory));
            }
            actions.Add(("Novo arquivo", "", () => _ = CreateFileAsync(DefaultTargetFolder())));
            actions.Add(("Nova pasta", "", () => _ = CreateFolderAsync(DefaultTargetFolder())));
            actions.Insert(0, (IsTaskRunning ? "Parar execução" : "Executar projeto", "F5", () => _ = RunOrStopAsync()));
            actions.Add(("Definir comando de execução", RunCommandForProject() ?? "", () => _ = AskAndSaveRunCommandAsync()));
            if (RunningSiteUrl is { } site)
                actions.Insert(0, ("Abrir site no navegador", site, OpenSite));
        }
        actions.Add((_terminalOpen ? "Fechar terminal" : "Abrir terminal", "Ctrl J", ToggleTerminal));
        actions.Add(("Nova aba", "Ctrl N", NewUntitled));
        actions.Add(("Procurar versão nova", $"Versão {CurrentVersion}", () => _ = CheckForUpdatesAsync(manual: true)));
        if (_pendingUpdate is { } update)
        {
            actions.Insert(0, ("Atualizar a Sirius", $"Versão {update}", () => _ = InstallUpdateAsync(update)));
            actions.Add(("Ver as novidades da versão nova", $"Versão {update}", () => OpenReleasePage(update)));
            actions.Add(("Ignorar a versão nova", $"Versão {update}", IgnorePendingUpdate));
        }
        actions.Add(("Aviso de versão nova", _settings.CheckForUpdates ? "Ligado" : "Desligado", ToggleUpdateChecks));
        if (ActiveDocument is { IsImage: true } image)
        {
            actions.Add(("Renomear arquivo", "F2 na lista", () => _ = RenameAsync(image.FullPath)));
            actions.Add(("Aproximar imagem", "Ctrl +", () => ZoomImage(1)));
            actions.Add(("Afastar imagem", "Ctrl -", () => ZoomImage(-1)));
            actions.Add(("Ajustar imagem à área", "Ctrl 0", () => ZoomImage(0)));
            actions.Add(("Imagem no tamanho real", "Dois cliques", ImageView.ShowActualSize));
            actions.Add(("Fechar aba", "Ctrl W", () => _ = CloseDocumentAsync(image)));
        }
        else if (ActiveDocument is { } active)
        {
            actions.Insert(0, ("Salvar arquivo", "Ctrl S", () => _ = SaveAsync(active)));
            actions.Add(("Salvar como", "Ctrl Shift S", () => _ = SaveAsAsync(active)));
            actions.Add(("Salvar tudo", "", () => _ = SaveAllAsync()));
            actions.Add(("Comentar ou descomentar linhas", "Ctrl /", ToggleComment));
            actions.Add(("Duplicar linha", "Shift Alt ↓", () => DuplicateLines()));
            actions.Add(("Mover linha para cima", "Alt ↑", () => MoveLines(up: true)));
            actions.Add(("Mover linha para baixo", "Alt ↓", () => MoveLines(up: false)));
            actions.Add(("Formatar arquivo", "Shift Alt F", () => _ = FormatDocumentAsync()));
            if (CanUseEmmet)
                actions.Insert(1, ("Expandir Emmet", "Tab, como ! ou ul>li", ExpandEmmet));
            if (SvgRender.Supports(active.FullPath))
                actions.Insert(1, (_settings.SvgPreview ? "Esconder a prévia do SVG" : "Mostrar a prévia do SVG", "", ToggleSvgPreview));
            actions.Add(("Ir para a linha", "Ctrl G", OpenGoToLine));
            if (CanSetBreakpointHere)
                actions.Add(("Parar nesta linha só quando…", "condição", () => _ = EditBreakpointConditionAsync()));
            if (IsDebugging)
                actions.Add(("Vigiar uma expressão", "depuração", () => _ = AddWatchAsync()));
            actions.Add(("Ir para a definição", "F12", () => _ = GoToDefinitionAsync()));
            actions.Add(("Encontrar todos os usos", "Shift F12", () => _ = FindUsesAsync()));
            actions.Add(("Renomear símbolo", "F2 no editor", () => _ = RenameSymbolAsync()));
            actions.Add(("Mostrar sugestões", "Ctrl Espaço", () => _ = RequestCompletionAsync(explicitRequest: true)));
            actions.Add(("Mostrar problemas", "", ShowProblems));
            if (!active.IsUntitled)
                actions.Add(("Renomear arquivo", "F2 na lista", () => _ = RenameAsync(active.FullPath)));
            actions.Add(("Aumentar fonte do editor", "Ctrl +", () => SetEditorFontSize(EditorFontSize + 1)));
            actions.Add(("Diminuir fonte do editor", "Ctrl -", () => SetEditorFontSize(EditorFontSize - 1)));
            actions.Add(("Tamanho padrão da fonte", "Ctrl 0", () => SetEditorFontSize(EditorDefaultFontSize)));
            actions.Add(("Procurar no arquivo", "Ctrl F", () => _search.Open()));
            actions.Add(("Fechar aba", "Ctrl W", () => _ = CloseDocumentAsync(active)));
        }
        commands.AddRange(actions.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
        return commands;
    }

    private void RefreshPalette()
    {
        var palette = Palette.Current;
        var query = PaletteInput.Text?.Trim() ?? "";
        _commandRows.Clear();
        _filteredCommands = AllCommands(query)
            .Select((c, i) => new CommandRow(c.Name, c.Hint, i == _paletteIndex ? palette.Tint : Brushes.Transparent, c.Run,
                i < _paletteFileCount ? FileIcons.For(c.Name) : null))
            .ToList();
        foreach (var row in _filteredCommands)
            _commandRows.Add(row);
        PaletteEmpty.IsVisible = _filteredCommands.Count == 0;
        Dispatcher.UIThread.Post(() => PaletteList.ContainerFromIndex(_paletteIndex)?.BringIntoView());
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _filteredCommands.Count > 0:
                e.Handled = true;
                _paletteIndex = (_paletteIndex + 1) % _filteredCommands.Count;
                RefreshPalette();
                break;
            case Key.Up when _filteredCommands.Count > 0:
                e.Handled = true;
                _paletteIndex = (_paletteIndex - 1 + _filteredCommands.Count) % _filteredCommands.Count;
                RefreshPalette();
                break;
            case Key.Enter when _filteredCommands.Count > 0:
                e.Handled = true;
                RunCommand(_filteredCommands[_paletteIndex]);
                break;
        }
    }

    private void OnCommandClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CommandRow row)
            RunCommand(row);
    }

    private void RunCommand(CommandRow row)
    {
        ClosePalette();
        row.Run();
    }

    // Busca no projeto

    public void OpenSearch()
    {
        var seed = CodeEditor.IsVisible ? CodeEditor.SelectedText : "";
        SetSidebarVisible(true);
        if (!IsWidePanelOpen)
        {
            _settings.SidebarWidth = Sidebar.Width;
            Sidebar.Width = Math.Max(_searchWidth, SearchMinWidth);
        }
        TreeView.IsVisible = false;
        GitView.IsVisible = false;
        SearchView.IsVisible = true;
        if (seed.Length > 0 && !seed.Contains('\n') && !seed.Contains('\r'))
            SearchInput.Text = seed;
        Dispatcher.UIThread.Post(() =>
        {
            SearchInput.Focus();
            SearchInput.SelectAll();
        });
    }

    public void CloseSearch()
    {
        _searchDelay?.Stop();
        if (IsSearchOpen)
        {
            Sidebar.Width = _settings.SidebarWidth;
            SearchView.IsVisible = false;
            TreeView.IsVisible = true;
        }
        FocusActiveDocument();
    }

    private void OnCloseSearch(object? sender, RoutedEventArgs e) => CloseSearch();

    private void OnToggleMatchCase(object? sender, RoutedEventArgs e)
    {
        SetMatchCase(!_matchCase);
    }

    private void OnToggleRegex(object? sender, RoutedEventArgs e)
    {
        SetRegex(!_useRegex);
    }

    public void SetMatchCase(bool value)
    {
        _matchCase = value;
        MatchCaseButton.Classes.Set("on", value);
        ScheduleSearch();
    }

    public void SetRegex(bool value)
    {
        _useRegex = value;
        RegexButton.Classes.Set("on", value);
        ScheduleSearch();
    }

    private void ScheduleSearch()
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    public async Task RunSearchAsync()
    {
        _searchDelay.Stop();
        _searchCancel?.Cancel();

        // Uma busca de texto tira os usos do painel, e o substituir volta a valer.
        _usesQuery = null;
        ReplaceBar.IsVisible = true;
        UsesHint.IsVisible = false;

        var text = SearchInput.Text ?? "";
        if (text.Length == 0 || RootFolder is null)
        {
            ClearSearchResults();
            return;
        }

        var cancel = new CancellationTokenSource();
        _searchCancel = cancel;
        var root = RootFolder;
        var query = new SearchQuery(text, _matchCase, _useRegex);
        var unsaved = _documents.Where(d => d.IsDirty).ToDictionary(d => d.FullPath, d => d.Document.Text, StringComparer.OrdinalIgnoreCase);
        SearchStatus.Text = "Buscando…";

        try
        {
            var (files, result) = await Task.Run(() =>
            {
                var found = ProjectTree.FindFiles(root);
                return (found, ProjectSearch.Run(found, query, unsaved, cancel.Token));
            }, cancel.Token);

            if (_searchCancel != cancel)
                return;
            _projectFiles = files;
            ShowSearchResult(result);
        }
        catch (OperationCanceledException)
        {
            // Uma busca mais nova já tomou o lugar desta.
        }
    }

    private void ShowSearchResult(SearchResult result)
    {
        ClearSearchResults();
        if (result.InvalidQuery)
        {
            SearchStatus.Text = "Expressão regular inválida.";
            return;
        }

        string? currentFile = null;
        foreach (var hit in result.Hits)
        {
            if (hit.FullPath != currentFile)
            {
                currentFile = hit.FullPath;
                var hint = BreadcrumbFor(hit.FullPath);
                _searchRows.Add(new SearchRow(true, Path.GetFileName(hit.FullPath), hint, hit.FullPath, 0, 0, 0, "", "", "", Brushes.Transparent, FileIcons.For(Path.GetFileName(hit.FullPath))));
            }
            _searchRows.Add(new SearchRow(false, "", "", hit.FullPath, hit.Line, hit.Column, hit.Length, hit.Before, hit.Match, hit.After, Brushes.Transparent));
        }

        if (result.Hits.Count == 0)
            SearchStatus.Text = "Nada encontrado.";
        else if (result.Truncated)
            SearchStatus.Text = $"Mostrando as primeiras {ProjectSearch.MaxHits} linhas. Refine a busca para ver o resto.";
        else
            SearchStatus.Text = $"{Count(result.Hits.Count, "linha", "linhas")} em {Count(result.FileCount, "arquivo", "arquivos")}";
    }

    private static string Count(int value, string singular, string plural) =>
        $"{value} {(value == 1 ? singular : plural)}";

    private void ClearSearchResults()
    {
        _searchRows.Clear();
        _searchIndex = -1;
        SearchStatus.Text = "";
    }

    private void OnSearchRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SearchRow row)
            ActivateSearchRow(_searchRows.IndexOf(row), keepSearchFocus: false);
    }

    public void ActivateSearchRow(int index, bool keepSearchFocus)
    {
        if (index < 0 || index >= _searchRows.Count)
            return;

        MarkSearchRow(index);
        var row = _searchRows[index];
        OpenFile(row.FullPath);
        if (!string.Equals(ActiveDocument?.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (!row.IsFile)
        {
            var document = ActiveDocument!.Document;
            if (row.Line <= document.LineCount)
            {
                var line = document.GetLineByNumber(row.Line);
                var start = Math.Min(line.Offset + row.Column, line.EndOffset);
                var length = Math.Min(row.Length, line.EndOffset - start);
                CodeEditor.Select(start, length);
                CodeEditor.TextArea.Caret.Offset = start + length;
                Dispatcher.UIThread.Post(() => CodeEditor.ScrollToLine(row.Line), DispatcherPriority.Loaded);
            }
        }

        if (keepSearchFocus)
            Dispatcher.UIThread.Post(() => SearchInput.Focus());
    }

    private void MarkSearchRow(int index)
    {
        if (_searchIndex >= 0 && _searchIndex < _searchRows.Count)
            _searchRows[_searchIndex] = _searchRows[_searchIndex] with { Background = Brushes.Transparent };
        _searchIndex = index;
        _searchRows[index] = _searchRows[index] with { Background = Palette.Current.Tint };
        var target = index;
        // A lista é virtualizada: um resultado fora da tela ainda não tem linha, e o ScrollIntoView a cria.
        Dispatcher.UIThread.Post(() => SearchList.ScrollIntoView(target));
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_searchRows.Any(r => r.IsHit))
            return;

        e.Handled = true;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
        var index = _searchIndex;
        do
        {
            index = (index + step + _searchRows.Count) % _searchRows.Count;
        }
        while (_searchRows[index].IsFile);
        ActivateSearchRow(index, keepSearchFocus: true);
    }

    // Tamanho da fonte do editor

    private void OnEditorWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
            return;
        e.Handled = true;
        SetEditorFontSize(EditorFontSize + Math.Sign(e.Delta.Y));
    }

    public void SetEditorFontSize(double size)
    {
        ApplyEditorFontSize(size);
        _settings.EditorFontSize = EditorFontSize;
        _settings.Save(_settingsPath);
        ShowStatus($"Fonte do editor: {EditorFontSize:0.#}");
    }

    private void ApplyEditorFontSize(double size)
    {
        var clamped = Math.Clamp(size, EditorMinFontSize, EditorMaxFontSize);
        CodeEditor.FontSize = clamped;
        _gutter.SetScale(clamped / EditorDefaultFontSize);
    }

    // Tema, foco e status

    private void OnToggleTheme(object? sender, RoutedEventArgs e) => ToggleTheme();

    public void ToggleTheme() => ApplyTheme(Palette.Current == Palette.Claro ? Palette.Grafite.Name : Palette.Claro.Name);

    private void ApplyTheme(string name)
    {
        Palette.Apply(name);
        var palette = Palette.Current;
        _settings.Theme = palette.Name;
        _settings.Save(_settingsPath);

        SheetBorder.BoxShadow = palette.SheetShadow;
        Capsule.BoxShadow = palette.PopShadow;
        PaletteCard.BoxShadow = palette.PopShadow;
        DialogCard.BoxShadow = palette.PopShadow;
        SettingsCard.BoxShadow = palette.PopShadow;
        ColorCard.BoxShadow = palette.PopShadow;
        CodeEditor.TextArea.SelectionBrush = palette.Selection;
        CodeEditor.TextArea.SelectionBorder = null;
        CodeEditor.TextArea.Caret.CaretBrush = palette.Accent;
        TerminalHost.ApplyScheme(TerminalScheme.For(palette));
        RefreshProblems();
        RefreshGitRows();
        RebuildHistoryRows();
        RefreshVariableRows();

        RefreshTabs();
        RefreshTree();
        if (_searchIndex >= 0 && _searchIndex < _searchRows.Count)
            _searchRows[_searchIndex] = _searchRows[_searchIndex] with { Background = palette.Tint };
        CodeEditor.TextArea.TextView.Redraw();
        _gutter.InvalidateVisual();
        ImageView.InvalidateVisual();
        SvgView.InvalidateVisual();
    }

    private void OnToggleFocus(object? sender, RoutedEventArgs e) => ToggleFocus();

    public void ToggleFocus()
    {
        SetSidebarVisible(!Sidebar.IsVisible);
    }

    private void UpdateCaretPosition()
    {
        var caret = CodeEditor.TextArea.Caret;
        CaretPosition.IsVisible = ActiveDocument is not null;
        CaretPosition.Text = ActiveDocument is { IsImage: true } ? ImageView.Description : $"Ln {caret.Line}, Col {caret.Column}";
        // Clicar na posição leva a outra linha, como no Ctrl G.
        var clickable = ActiveDocument is { IsImage: false };
        CaretPosition.Cursor = clickable ? new Cursor(StandardCursorType.Hand) : null;
        ToolTip.SetTip(CaretPosition, clickable ? "Ir para a linha (Ctrl G)" : null);
    }

    private void UpdateEmptyState()
    {
        EmptyState.IsVisible = ActiveDocument is null;
        EmptyOpenFolder.IsVisible = RootFolder is null;
        UpdateRecentList();
        EmptyTitle.Text = RootFolder is null ? "Abra uma pasta para começar" : "Escolha um arquivo";
        EmptyHint.Text = RootFolder is null ? "Use Ctrl O ou arraste uma pasta para cá. Ctrl K mostra os comandos." : "Clique na lista ao lado, use Ctrl K ou arraste um arquivo para cá.";
    }

    private void ShowStatus(string message)
    {
        CapsuleStatus.Text = message;
        CapsuleStatus.IsVisible = true;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ClearStatus()
    {
        _statusTimer.Stop();
        CapsuleStatus.Text = "";
        CapsuleStatus.IsVisible = false;
    }

    // Confirmação

    private Task<DialogChoice> AskAsync(string title, string body, string primary, string? secondary)
    {
        ShowDialog(title, body, primary, secondary);
        DialogInputBox.IsVisible = false;
        _dialogValidator = null;
        Dispatcher.UIThread.Post(() => DialogPrimary.Focus());
        return _dialog!.Task;
    }

    // Pede um texto e devolve null se a pessoa cancelar.
    private async Task<string?> AskTextAsync(string title, string body, string primary, Func<string, string?> validator, string initial = "", int? selectLength = null)
    {
        ShowDialog(title, body, primary, null);
        DialogInputBox.IsVisible = true;
        DialogInput.Text = initial;
        _dialogValidator = validator;
        Dispatcher.UIThread.Post(() =>
        {
            DialogInput.Focus();
            DialogInput.SelectionStart = 0;
            DialogInput.SelectionEnd = selectLength ?? initial.Length;
        });
        var choice = await _dialog!.Task;
        return choice == DialogChoice.Primary ? (DialogInput.Text ?? "") : null;
    }

    private void ShowDialog(string title, string body, string primary, string? secondary)
    {
        // Só cabe uma pergunta por vez. A anterior conta como cancelada, a resposta segura,
        // senão quem esperava por ela ficaria parado para sempre.
        _dialog?.TrySetResult(DialogChoice.Cancel);

        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogPrimary.Content = primary;
        DialogSecondary.Content = secondary;
        DialogSecondary.IsVisible = secondary is not null;
        DialogError.IsVisible = false;
        DialogOverlay.IsVisible = true;
        _dialog = new TaskCompletionSource<DialogChoice>();
    }

    private void FinishDialog(DialogChoice choice)
    {
        if (choice == DialogChoice.Primary && _dialogValidator?.Invoke(DialogInput.Text ?? "") is { } problem)
        {
            DialogError.Text = problem;
            DialogError.IsVisible = true;
            return;
        }

        DialogOverlay.IsVisible = false;
        var dialog = _dialog;
        _dialog = null;
        dialog?.TrySetResult(choice);
    }

    private void OnDialogInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FinishDialog(DialogChoice.Primary);
        }
    }

    private void OnDialogPrimary(object? sender, RoutedEventArgs e) => FinishDialog(DialogChoice.Primary);
    private void OnDialogSecondary(object? sender, RoutedEventArgs e) => FinishDialog(DialogChoice.Secondary);
    private void OnDialogCancel(object? sender, RoutedEventArgs e) => FinishDialog(DialogChoice.Cancel);

    // Usado pelos testes para responder a uma confirmação aberta.
    public void AnswerDialog(bool primary) => FinishDialog(primary ? DialogChoice.Primary : DialogChoice.Secondary);
    public void CancelDialog() => FinishDialog(DialogChoice.Cancel);

    // Janela

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // Cabe na área livre da tela, sem a barra de tarefas, antes de aparecer.
    private void FitToScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
            return;
        var (width, height, position) = WindowPlacement.Fit(screen.WorkingArea, screen.Scaling, Width, Height);
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = position;
    }

    public PixelPoint WindowPositionForTests => Position;
    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || (!IsTaskRunning && !_documents.Any(d => d.IsDirty)))
            return;

        e.Cancel = true;

        // As abas fecham por causa da saída, mas devem voltar na próxima vez.
        SaveSession();
        _sessionFrozen = true;
        if (await ConfirmStopTaskAsync() && await CloseAllAsync())
        {
            _allowClose = true;
            Close();
        }
        else
        {
            _sessionFrozen = false;
        }
    }

    private async Task<bool> ConfirmStopTaskAsync()
    {
        if (!IsTaskRunning)
            return true;
        var choice = await AskAsync("Há um programa em execução", $"Continuar vai encerrar {_taskCommand}.", "Encerrar", null);
        return choice == DialogChoice.Primary;
    }
}
