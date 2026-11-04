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
    private const double SidebarWidth = 232;
    private const double SearchSidebarWidth = 360;

    private readonly ObservableCollection<TreeRow> _treeRows = new();
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

        SearchInput.TextChanged += (_, _) => ScheduleSearch();
        SearchInput.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Normal, (_, _) => _ = RunSearchAsync());

        // Uma edição pode mudar a cor das linhas de baixo, como ao abrir um comentário.
        _redrawDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) =>
        {
            _redrawDelay!.Stop();
            CodeEditor.TextArea.TextView.Redraw();
        });

        _statusTimer =new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => ClearStatus());

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
    public bool IsPaletteOpen => PaletteOverlay.IsVisible;
    public bool IsDialogOpen => DialogOverlay.IsVisible;
    public bool IsFocusMode => !Sidebar.IsVisible;
    public string ThemeName => Palette.Current.Name;
    public bool IsSearchOpen => SearchView.IsVisible;
    public IReadOnlyList<SearchRow> SearchRows => _searchRows;
    public string SearchStatusText => SearchStatus.Text ?? "";
    public int CaretLine => CodeEditor.TextArea.Caret.Line;
    public string SelectedText => CodeEditor.SelectedText;

    // Projeto

    public void OpenFolder(string folder)
    {
        RootFolder = Path.GetFullPath(folder);
        _settings.LastFolder = RootFolder;
        _settings.Save(_settingsPath);
        _expanded.Clear();
        _projectFiles = new List<string>();
        CloseSearch();
        ClearSearchResults();

        ProjectName.Text = Path.GetFileName(RootFolder.TrimEnd(Path.DirectorySeparatorChar));
        var branch = ProjectTree.CurrentBranch(RootFolder);
        BranchName.Text = branch ?? "";
        BranchPanel.IsVisible = branch is not null;

        RefreshTree();
        UpdateEmptyState();

        var root = RootFolder;
        ProjectIndexed = Task.Run(() => ProjectTree.FindFiles(root)).ContinueWith(
            t => { if (RootFolder == root) _projectFiles = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshTree()
    {
        _treeRows.Clear();
        if (RootFolder is not null)
            AddTreeLevel(RootFolder, 0);
    }

    private void AddTreeLevel(string folder, int depth)
    {
        var palette = Palette.Current;
        foreach (var entry in ProjectTree.List(folder))
        {
            var expanded = entry.IsFolder && _expanded.Contains(entry.FullPath);
            var active = !entry.IsFolder && string.Equals(ActiveDocument?.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase);
            var document = _documents.Find(d => string.Equals(d.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));
            _treeRows.Add(new TreeRow(
                entry.Name, entry.FullPath, entry.IsFolder,
                new Thickness(10 + depth * 14, 0, 0, 0),
                entry.IsFolder ? (expanded ? ChevronOpen : ChevronClosed) : null,
                active ? FontWeight.SemiBold : FontWeight.Normal,
                active ? palette.Tint : Brushes.Transparent,
                entry.IsFolder ? palette.Muted : palette.Ink,
                document?.IsDirty ?? false));
            if (expanded)
                AddTreeLevel(entry.FullPath, depth + 1);
        }
    }

    private void OnTreeRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TreeRow row)
            return;
        if (row.IsFolder)
        {
            if (!_expanded.Remove(row.FullPath))
                _expanded.Add(row.FullPath);
            RefreshTree();
        }
        else
        {
            OpenFile(row.FullPath);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Abrir pasta", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null || !await CloseAllAsync())
            return;
        OpenFolder(path);
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
                document = OpenDocument.Load(fullPath);
            }
            catch (Exception e) when (e is TextFileException or IOException or UnauthorizedAccessException)
            {
                ShowStatus(e is TextFileException ? e.Message : "Não foi possível abrir este arquivo.");
                return;
            }
            var opened = document;
            opened.Document.UndoStack.PropertyChanged += (_, _) => OnDirtyMaybeChanged();
            opened.Document.Changed += (_, _) =>
            {
                if (ActiveDocument == opened && opened.Highlighter is not null && !_redrawDelay.IsEnabled)
                    _redrawDelay.Start();
            };
            _documents.Add(opened);
        }
        Activate(document);
    }

    private void Activate(OpenDocument? document)
    {
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
            Dispatcher.UIThread.Post(() => CodeEditor.TextArea.Focus());
        }

        CodeEditor.IsVisible = document is not null;
        Breadcrumb.Text = document is null || RootFolder is null ? "" : BreadcrumbFor(document.FullPath);
        ExpandTo(document?.FullPath);
        RefreshTabs();
        RefreshTree();
        UpdateEmptyState();
        UpdateCaretPosition();
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
        var relative = Path.GetRelativePath(RootFolder!, Path.GetDirectoryName(path)!);
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
                active ? palette.Ink : palette.Muted, document.IsDirty));
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

        var index = _documents.IndexOf(document);
        _documents.RemoveAt(index);
        if (ActiveDocument == document)
        {
            ActiveDocument = null;
            Activate(_documents.Count == 0 ? null : _documents[Math.Min(index, _documents.Count - 1)]);
        }
        else
        {
            RefreshTabs();
            RefreshTree();
        }
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

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        switch (e.Key)
        {
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && RootFolder is not null:
                e.Handled = true;
                OpenSearch();
                break;
            case Key.K:
                e.Handled = true;
                OpenPalette();
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
        }
    }

    // Barra de comando

    private void OnOpenPalette(object? sender, RoutedEventArgs e) => OpenPalette();

    public void OpenPalette()
    {
        PaletteOverlay.IsVisible = true;
        PaletteInput.Text = "";
        _paletteIndex = 0;
        RefreshPalette();
        Dispatcher.UIThread.Post(() => PaletteInput.Focus());
    }

    public void ClosePalette()
    {
        PaletteOverlay.IsVisible = false;
        if (ActiveDocument is not null)
            CodeEditor.TextArea.Focus();
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e) => ClosePalette();

    private List<(string Name, string Hint, Action Run)> AllCommands(string query)
    {
        var commands = new List<(string Name, string Hint, Action Run)>();
        var matches = _projectFiles
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(query.Length == 0 ? 6 : PaletteFileLimit);
        foreach (var file in matches)
            commands.Add((file.Name, RootFolder is null ? "" : BreadcrumbFor(file.Path), () => OpenFile(file.Path)));

        var actions = new List<(string Name, string Hint, Action Run)>
        {
            ("Abrir pasta", "Ctrl O", () => _ = PickFolderAsync()),
            ("Trocar tema", Palette.Current == Palette.Claro ? "Grafite" : "Claro", ToggleTheme),
            ("Modo foco", "Ctrl .", ToggleFocus),
        };
        if (RootFolder is not null)
            actions.Insert(0, ("Buscar no projeto", "Ctrl Shift F", OpenSearch));
        if (ActiveDocument is { } active)
        {
            actions.Insert(0, ("Salvar arquivo", "Ctrl S", () => _ = SaveAsync(active)));
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
            .Select((c, i) => new CommandRow(c.Name, c.Hint, i == _paletteIndex ? palette.Tint : Brushes.Transparent, c.Run))
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
        Sidebar.IsVisible = true;
        FocusButton.Classes.Set("on", false);
        Sidebar.Width = SearchSidebarWidth;
        TreeScroll.IsVisible = false;
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
        Sidebar.Width = SidebarWidth;
        SearchView.IsVisible = false;
        TreeScroll.IsVisible = true;
        if (ActiveDocument is not null)
            CodeEditor.TextArea.Focus();
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
                _searchRows.Add(new SearchRow(true, Path.GetFileName(hit.FullPath), hint, hit.FullPath, 0, 0, 0, "", "", "", Brushes.Transparent));
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
        Dispatcher.UIThread.Post(() => SearchList.ContainerFromIndex(target)?.BringIntoView());
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
        CodeEditor.TextArea.SelectionBrush = palette.Selection;
        CodeEditor.TextArea.SelectionBorder = null;
        CodeEditor.TextArea.Caret.CaretBrush = palette.Accent;

        RefreshTabs();
        RefreshTree();
        if (_searchIndex >= 0 && _searchIndex < _searchRows.Count)
            _searchRows[_searchIndex] = _searchRows[_searchIndex] with { Background = palette.Tint };
        CodeEditor.TextArea.TextView.Redraw();
        _gutter.InvalidateVisual();
    }

    private void OnToggleFocus(object? sender, RoutedEventArgs e) => ToggleFocus();

    public void ToggleFocus()
    {
        Sidebar.IsVisible = !Sidebar.IsVisible;
        FocusButton.Classes.Set("on", !Sidebar.IsVisible);
    }

    private void UpdateCaretPosition()
    {
        var caret = CodeEditor.TextArea.Caret;
        CaretPosition.IsVisible = ActiveDocument is not null;
        CaretPosition.Text = $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void UpdateEmptyState()
    {
        EmptyState.IsVisible = ActiveDocument is null;
        EmptyOpenFolder.IsVisible = RootFolder is null;
        EmptyTitle.Text = RootFolder is null ? "Abra uma pasta para começar" : "Escolha um arquivo";
        EmptyHint.Text = RootFolder is null ? "Ctrl O abre uma pasta, Ctrl K mostra os comandos." : "Clique na lista ao lado ou use Ctrl K para encontrar.";
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
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogPrimary.Content = primary;
        DialogSecondary.Content = secondary;
        DialogSecondary.IsVisible = secondary is not null;
        DialogOverlay.IsVisible = true;
        _dialog = new TaskCompletionSource<DialogChoice>();
        Dispatcher.UIThread.Post(() => DialogPrimary.Focus());
        return _dialog.Task;
    }

    private void FinishDialog(DialogChoice choice)
    {
        DialogOverlay.IsVisible = false;
        var dialog = _dialog;
        _dialog = null;
        dialog?.TrySetResult(choice);
    }

    private void OnDialogPrimary(object? sender, RoutedEventArgs e) => FinishDialog(DialogChoice.Primary);
    private void OnDialogSecondary(object? sender, RoutedEventArgs e) => FinishDialog(DialogChoice.Secondary);
    private void OnDialogCancel(object? sender, RoutedEventArgs e) => FinishDialog(DialogChoice.Cancel);

    // Usado pelos testes para responder a uma confirmação aberta.
    public void AnswerDialog(bool primary) => FinishDialog(primary ? DialogChoice.Primary : DialogChoice.Secondary);

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
    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || !_documents.Any(d => d.IsDirty))
            return;

        e.Cancel = true;
        if (await CloseAllAsync())
        {
            _allowClose = true;
            Close();
        }
    }
}
