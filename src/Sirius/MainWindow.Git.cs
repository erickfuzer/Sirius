using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Lsp;
using Sirius.Theming;

namespace Sirius;

// O Git dentro da IDE: painel de mudanças, commit, comparação, envio e branches.
public partial class MainWindow
{
    private const int MaxMarkedChars = 2_000_000;
    private static readonly TimeSpan FetchInterval = TimeSpan.FromSeconds(60);

    private readonly RowList<GitRow> _gitRows = new();
    private readonly HashSet<string> _gitExcluded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Letter, GitChangeKind Kind)> _gitMarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gitChangedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _gitBaseTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DiffRowKind> _diffLeftKinds = new();
    private readonly List<DiffRowKind> _diffRightKinds = new();
    private readonly DiffNumberMargin _diffLeftNumbers = new();
    private readonly DiffNumberMargin _diffRightNumbers = new();
    private readonly TypographicColorizer _diffLeftColors = new();
    private readonly TypographicColorizer _diffRightColors = new();

    private DispatcherTimer? _gitMarksDelay;
    private Task? _gitLoop;
    private bool _gitRefreshPending;
    private bool _syncingDiffScroll;
    private int _gitMarksRequest;
    private string? _gitHeadOid;
    private DateTime _lastFetchUtc;
    private List<(string Name, string Hint, Action Run)>? _paletteOverride;

    public GitSnapshot? GitState { get; private set; }
    public IReadOnlyList<GitRow> GitRows => _gitRows;
    public bool IsGitOpen => GitView.IsVisible;
    public bool IsDiffOpen => DiffHost.IsVisible;
    public string GitStatusMessage => GitStatusLine.Text ?? "";
    public string GitMessageText => GitMessage.Text ?? "";
    public string DiffLeftText => DiffLeft.Document?.Text ?? "";
    public string DiffRightText => DiffRight.Document?.Text ?? "";
    public IReadOnlyList<DiffRowKind> DiffLeftKinds => _diffLeftKinds;
    public IReadOnlyList<DiffRowKind> DiffRightKinds => _diffRightKinds;
    public IReadOnlyDictionary<int, GitLineKind> GitLineMarks => _gutter.LineMarks;
    public string TreeMarkFor(string path) => _gitMarks.TryGetValue(path, out var mark) ? mark.Letter : "";

    private bool IsWidePanelOpen => IsSearchOpen || IsGitOpen;

    // Os testes usam uma configuração própria para não tocar na do usuário.
    public IReadOnlyDictionary<string, string>? GitEnvironment { get; set; }

    private GitClient Git() => new(GitState?.TopLevel ?? RootFolder!, GitEnvironment);

    private void InitializeGit()
    {
        GitList.ItemsSource = _gitRows;
        InitializeGitHistory();
        GitMessage.AddHandler(KeyDownEvent, OnGitMessageKeyDown, RoutingStrategies.Tunnel);
        _gitMarksDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) =>
        {
            _gitMarksDelay!.Stop();
            _ = UpdateGitMarksAsync();
        });

        DiffLeft.TextArea.LeftMargins.Insert(0, _diffLeftNumbers);
        DiffRight.TextArea.LeftMargins.Insert(0, _diffRightNumbers);
        DiffLeft.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackground(() => _diffLeftKinds, false));
        DiffRight.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackground(() => _diffRightKinds, true));
        DiffLeft.TextArea.TextView.BackgroundRenderers.Add(new DiffFocusOutline(() => _conflictFocus));
        DiffRight.TextArea.TextView.BackgroundRenderers.Add(new DiffFocusOutline(() => _conflictFocus));
        DiffLeft.TextArea.TextView.LineTransformers.Add(_diffLeftColors);
        DiffRight.TextArea.TextView.LineTransformers.Add(_diffRightColors);
        DiffLeft.Options.EnableHyperlinks = false;
        DiffRight.Options.EnableHyperlinks = false;
        DiffLeft.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncDiffScroll(DiffLeft, DiffRight);
        DiffRight.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncDiffScroll(DiffRight, DiffLeft);
    }

    // Atualização

    // Pedidos feitos durante uma consulta geram mais uma rodada, e nunca duas ao mesmo tempo.
    public Task RefreshGitAsync()
    {
        _gitRefreshPending = true;
        if (_gitLoop is { IsCompleted: false })
            return _gitLoop;
        _gitLoop = RefreshGitLoopAsync();
        return _gitLoop;
    }

    private async Task RefreshGitLoopAsync()
    {
        while (_gitRefreshPending)
        {
            _gitRefreshPending = false;
            if (RootFolder is not { } root)
            {
                ApplyGit(null);
                return;
            }

            var snapshot = await new GitClient(root, GitEnvironment).SnapshotAsync();
            if (RootFolder == root)
                ApplyGit(snapshot);
        }
    }

    private void ApplyGit(GitSnapshot? snapshot)
    {
        GitState = snapshot;
        _gitMarks.Clear();
        _gitChangedFolders.Clear();

        if (snapshot is null)
        {
            BranchPanel.IsVisible = false;
            GitCount.IsVisible = false;
            _gitRows.Clear();
            _gitBaseTexts.Clear();
            _gitHeadOid = null;
            ResetHistory();
            GitEmptyPanel.IsVisible = true;
            GitRepoPanel.IsVisible = false;
            UpdateGitEmptyPanel();
            RefreshTree();
            _gutter.SetLineMarks(null);
            SetChangeBlocks(null);
            return;
        }

        GitEmptyPanel.IsVisible = false;
        GitRepoPanel.IsVisible = true;

        var status = snapshot.Status;
        if (status.HeadOid != _gitHeadOid)
        {
            _gitHeadOid = status.HeadOid;
            _gitBaseTexts.Clear();
            if (IsHistoryShown)
                _ = LoadHistoryAsync();
        }

        var branch = status.Branch ?? (status.HeadOid is { } oid ? oid[..Math.Min(7, oid.Length)] : "sem commits");
        BranchPanel.IsVisible = true;
        BranchName.Text = branch;
        GitBranchButton.Content = branch;
        GitPullButton.Content = status.Behind > 0 ? $"Receber ↓{status.Behind}" : "Receber";
        GitPushButton.Content = status.Ahead > 0 ? $"Enviar ↑{status.Ahead}" : "Enviar";

        foreach (var change in status.Changes)
        {
            var full = FullPathOf(snapshot, change.Path);
            _gitMarks[full] = (LetterOf(change.Kind), change.Kind);
            for (var folder = Path.GetDirectoryName(full); folder is not null && folder.Length > snapshot.TopLevel.Length; folder = Path.GetDirectoryName(folder))
                _gitChangedFolders.Add(folder);
        }

        GitCount.Text = status.Changes.Count.ToString();
        GitCount.IsVisible = status.Changes.Count > 0;
        GitChangesTab.Content = status.Changes.Count > 0 ? $"Mudanças {status.Changes.Count}" : "Mudanças";
        RefreshGitRows();
        RefreshTree();
        _ = UpdateGitMarksAsync();
    }

    private static string FullPathOf(GitSnapshot snapshot, string relative) =>
        Path.GetFullPath(Path.Combine(snapshot.TopLevel, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string LetterOf(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added or GitChangeKind.Untracked => "N",
        GitChangeKind.Deleted => "E",
        GitChangeKind.Renamed => "R",
        _ => "!",
    };

    private static IBrush BrushOf(GitChangeKind kind)
    {
        var palette = Palette.Current;
        return kind switch
        {
            GitChangeKind.Added or GitChangeKind.Untracked => palette.String,
            GitChangeKind.Deleted or GitChangeKind.Conflict => palette.Error,
            _ => palette.Function,
        };
    }

    private void RefreshGitRows()
    {
        if (GitState is not { } snapshot)
        {
            _gitRows.Clear();
            return;
        }

        // Conflitos primeiro, porque impedem o resto.
        _gitRows.ReplaceAll(snapshot.Status.Changes
            .OrderBy(c => c.Kind != GitChangeKind.Conflict).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(change => new GitRow(Path.GetFileName(change.Path), FolderText(change),
                LetterOf(change.Kind), BrushOf(change.Kind), !_gitExcluded.Contains(change.Path), change, FileIcons.For(Path.GetFileName(change.Path))))
            .ToList());
        // O Git só deixa de chamar de conflito depois do commit. Para a pessoa, sem marcas já está resolvido,
        // e o aviso vermelho ao lado de "faça o commit" confundia.
        GitConflictNotice.IsVisible = snapshot.Status.Changes.Any(c => c.Kind == GitChangeKind.Conflict && HasConflictMarkers(FullPathOf(snapshot, c.Path)));
        GitNothing.IsVisible = _gitRows.Count == 0;
    }

    // A pasta, ou de onde veio o arquivo renomeado: só o nome antigo, se ficou na mesma pasta.
    private static string FolderText(GitChange change)
    {
        var folder = Path.GetDirectoryName(change.Path)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        if (change is not { Kind: GitChangeKind.Renamed, OriginalPath: { } original })
            return folder;
        var originalFolder = Path.GetDirectoryName(original)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        return originalFolder == folder ? $"era {Path.GetFileName(original)}" : $"era {original}";
    }

    // Painel

    private void OnOpenGit(object? sender, RoutedEventArgs e) => OpenGit();
    private void OnCloseGit(object? sender, RoutedEventArgs e) => CloseGit();

    public void OpenGit()
    {
        if (RootFolder is null)
            return;

        SetSidebarVisible(true);
        if (!IsWidePanelOpen)
        {
            _settings.SidebarWidth = Sidebar.Width;
            Sidebar.Width = Math.Max(_searchWidth, SearchMinWidth);
        }
        TreeView.IsVisible = false;
        SearchView.IsVisible = false;
        GitView.IsVisible = true;
        ShowGitTab(history: false);
        _ = RefreshGitAsync();
        _ = FetchInBackgroundAsync();
        Dispatcher.UIThread.Post(() => GitMessage.Focus());
    }

    public void CloseGit()
    {
        if (!IsGitOpen)
            return;
        Sidebar.Width = _settings.SidebarWidth;
        GitView.IsVisible = false;
        TreeView.IsVisible = true;
        FocusActiveDocument();
    }

    // Só para atualizar as contagens: sem janelas de login, e falhar aqui não importa.
    private async Task FetchInBackgroundAsync()
    {
        if (GitState is not { Remotes.Count: > 0 } || DateTime.UtcNow - _lastFetchUtc < FetchInterval)
            return;
        _lastFetchUtc = DateTime.UtcNow;
        if ((await Git().FetchQuietlyAsync()).Ok)
            await RefreshGitAsync();
    }

    private void OnGitRowToggle(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is GitRow row)
            ToggleGitChange(row.Change.Path);
    }

    public void ToggleGitChange(string path)
    {
        if (!_gitExcluded.Remove(path))
            _gitExcluded.Add(path);
        RefreshGitRows();
    }

    private void OnGitRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GitRow row)
            _ = OpenDiffAsync(row.Change);
    }

    private void OnGitRowOpen(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GitRow row && GitState is { } snapshot && row.Change.Kind != GitChangeKind.Deleted)
            OpenFile(FullPathOf(snapshot, row.Change.Path));
    }

    private void OnGitRowDiscard(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is GitRow row)
            _ = DiscardChangeAsync(row.Change);
    }

    private void SetGitMessage(string text) => GitStatusLine.Text = text;

    // Commit

    private void OnCommitClick(object? sender, RoutedEventArgs e) => _ = CommitAsync();

    private void OnGitMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = CommitAsync();
        }
    }

    public void SetCommitMessage(string message) => GitMessage.Text = message;

    public async Task<bool> CommitAsync()
    {
        if (GitState is null)
            return false;

        var message = (GitMessage.Text ?? "").Trim();
        if (message.Length == 0)
        {
            SetGitMessage("Escreva uma mensagem para o commit.");
            return false;
        }

        // O commit grava o que está no disco, então o que está só na tela precisa ir para lá antes.
        // E a lista vem depois: um arquivo que só mudou na tela ainda não aparecia nela e ficaria de fora.
        var dirty = _documents.Where(d => d.IsDirty && !d.IsUntitled).ToList();
        foreach (var document in dirty)
        {
            if (!await SaveAsync(document))
                return false;
        }
        if (dirty.Count > 0)
            await RefreshGitAsync();
        if (GitState is not { } snapshot)
            return false;

        var selected = snapshot.Status.Changes.Where(change => !_gitExcluded.Contains(change.Path)).ToList();
        if (selected.Count == 0)
        {
            SetGitMessage("Marque ao menos um arquivo.");
            return false;
        }
        if (snapshot.IsMerging && selected.Count != snapshot.Status.Changes.Count)
        {
            SetGitMessage("Durante um merge, o commit precisa levar todos os arquivos.");
            return false;
        }

        foreach (var change in selected.Where(c => c.Kind == GitChangeKind.Conflict))
        {
            if (HasConflictMarkers(FullPathOf(snapshot, change.Path)))
            {
                SetGitMessage($"{Path.GetFileName(change.Path)} ainda tem marcas de conflito (<<<<<<<). Resolva antes do commit.");
                return false;
            }
        }

        if (!await EnsureGitIdentityAsync())
            return false;

        SetGitMessage("Fazendo commit…");
        var result = await Git().CommitAsync(message, selected, snapshot.IsMerging);
        if (!result.Ok)
        {
            SetGitMessage(result.Message);
            await RefreshGitAsync();
            return false;
        }

        GitMessage.Text = "";
        _gitExcluded.Clear();
        await RefreshGitAsync();
        SetGitMessage("Commit feito.");
        return true;
    }

    private static bool HasConflictMarkers(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Contains("<<<<<<<", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> EnsureGitIdentityAsync()
    {
        var git = Git();
        if (await git.ConfigAsync("user.name") is null)
        {
            var name = await AskTextAsync("Como assinar seus commits?",
                "Este nome aparece no histórico e, se você publicar o projeto, fica público.", "Continuar",
                value => string.IsNullOrWhiteSpace(value) ? "Digite um nome." : null);
            if (name is null)
                return false;
            await git.SetGlobalConfigAsync("user.name", name.Trim());
        }
        if (await git.ConfigAsync("user.email") is null)
        {
            var email = await AskTextAsync("Qual e-mail usar nos commits?",
                "Ele também fica público se você publicar. No GitHub dá para usar um endereço noreply, em Settings, Emails.", "Salvar",
                value => value.Contains('@') ? null : "Digite um e-mail.");
            if (email is null)
                return false;
            await git.SetGlobalConfigAsync("user.email", email.Trim());
        }
        return true;
    }

    // Descartar

    public async Task DiscardChangeAsync(GitChange change)
    {
        if (GitState is not { } snapshot)
            return;

        var name = Path.GetFileName(change.Path);
        var isNew = change.Kind is GitChangeKind.Untracked or GitChangeKind.Added;
        var body = isNew
            ? "O arquivo ainda não está em nenhum commit e vai para a Lixeira do Windows."
            : change is { Kind: GitChangeKind.Renamed, OriginalPath: { } original }
                ? $"{Path.GetFileName(original)} volta como estava no último commit, e {name} vai para a Lixeira do Windows."
                : "O arquivo volta à versão do último commit. Isso não pode ser desfeito.";
        var choice = await AskAsync($"Descartar as mudanças em {name}?", body, "Descartar", null);
        if (choice != DialogChoice.Primary)
            return;

        var result = await Git().DiscardAsync(change);
        if (!result.Ok)
        {
            SetGitMessage(result.Message);
            return;
        }

        if (isNew || change.Kind == GitChangeKind.Renamed)
        {
            try
            {
                MoveToTrash(FullPathOf(snapshot, change.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                SetGitMessage("Não foi possível mover o arquivo para a Lixeira.");
            }
        }

        _gitExcluded.Remove(change.Path);
        await RefreshGitAsync();
        await CheckOpenDocumentsAsync();
    }

    // Comparação lado a lado

    public async Task OpenDiffAsync(GitChange change, int conflict = 0)
    {
        if (GitState is not { } snapshot)
            return;

        var full = FullPathOf(snapshot, change.Path);
        var left = "";
        if (change.Kind is not (GitChangeKind.Untracked or GitChangeKind.Added))
        {
            var bytes = await Git().ShowHeadAsync(change.OriginalPath ?? change.Path);
            if (bytes is not null)
            {
                if (TextFile.Decode(bytes) is not { } decoded)
                {
                    SetGitMessage("Este arquivo é binário e não pode ser comparado.");
                    return;
                }
                left = decoded.Text;
            }
        }

        string right;
        // A aba de imagem tem documento vazio: compará-lo mostraria o arquivo como se estivesse sem nada.
        if (_documents.Find(d => string.Equals(d.FullPath, full, StringComparison.OrdinalIgnoreCase)) is { IsImage: false } open)
        {
            right = open.Document.Text;
        }
        else if (!File.Exists(full))
        {
            right = "";
        }
        else if (ProjectSearch.LoadOrNull(full) is { } loaded)
        {
            right = loaded.Text;
        }
        else
        {
            SetGitMessage("Este arquivo é grande demais ou binário e não pode ser comparado.");
            return;
        }

        // Em conflito, comparar com o último commit mostraria as marcas como texto novo. Os dois lados lado a lado,
        // com um botão para cada escolha, dizem o que realmente importa.
        if (change.Kind == GitChangeKind.Conflict && GitConflict.Sides(right) is { } sides)
        {
            var conflictRows = await AlignForDiffAsync(sides.Mine, sides.Received);
            _conflict = new ConflictView(change, Path.GetRelativePath(snapshot.TopLevel, full).Replace(Path.DirectorySeparatorChar, '/'), sides, conflictRows);
            _conflictIndex = Math.Clamp(conflict, 0, sides.Count - 1);
            ShowDiff(full, conflictRows, "Meu, deste computador", "Recebido", conflict: true);
            ShowConflictFocus();
            return;
        }

        var rows = await AlignForDiffAsync(left, right);
        ShowDiff(full, rows, "Último commit", "Agora");
    }

    // O conflito na tela: os lados montados e as linhas alinhadas, para as setas só moverem o contorno.
    private sealed record ConflictView(GitChange Change, string Name, ConflictSides Sides, List<DiffRow> Rows);

    private ConflictView? _conflict;
    private int _conflictIndex;
    private (int First, int Last)? _conflictFocus;

    public bool IsConflictBarVisible => ConflictBar.IsVisible;
    public int ConflictIndex => _conflictIndex;

    private void OnConflictMine(object? sender, RoutedEventArgs e) => _ = ResolveConflictAsync(ConflictChoice.Mine);
    private void OnConflictReceived(object? sender, RoutedEventArgs e) => _ = ResolveConflictAsync(ConflictChoice.Received);
    private void OnConflictBoth(object? sender, RoutedEventArgs e) => _ = ResolveConflictAsync(ConflictChoice.Both);
    private void OnConflictPrevious(object? sender, RoutedEventArgs e) => MoveConflict(-1);
    private void OnConflictNext(object? sender, RoutedEventArgs e) => MoveConflict(1);

    public void MoveConflict(int direction)
    {
        if (_conflict is not { Sides.Count: > 1 } view)
            return;
        _conflictIndex = (_conflictIndex + direction + view.Sides.Count) % view.Sides.Count;
        ShowConflictFocus();
    }

    // Com um conflito só, o título diz isso e as setas somem. Com vários, o escolhido ganha contorno e vem à vista.
    private void ShowConflictFocus()
    {
        if (_conflict is not { } view)
            return;
        var count = view.Sides.Count;
        DiffTitle.Text = count == 1 ? $"{view.Name} · 1 conflito" : $"{view.Name} · conflito {_conflictIndex + 1} de {count}";
        ConflictNavigation.IsVisible = count > 1;
        _conflictFocus = count > 1 ? FocusRows(view.Rows, view.Sides.Blocks[_conflictIndex]) : null;
        DiffLeft.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        DiffRight.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        if (_conflictFocus is { } focus)
            Dispatcher.UIThread.Post(() => DiffRight.ScrollToLine(Math.Max(1, focus.First - 2)), DispatcherPriority.Loaded);
    }

    // As linhas da comparação que pertencem ao conflito, de um lado ou do outro.
    private static (int First, int Last)? FocusRows(List<DiffRow> rows, ConflictBlock block)
    {
        int first = -1, last = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var mine = row.LeftNumber is { } left && left - 1 >= block.MineStart && left - 1 < block.MineStart + block.MineCount;
            var received = row.RightNumber is { } right && right - 1 >= block.ReceivedStart && right - 1 < block.ReceivedStart + block.ReceivedCount;
            if (!mine && !received)
                continue;
            if (first < 0)
                first = i;
            last = i;
        }
        return first < 0 ? null : (first, last);
    }

    // Troca as marcas do conflito escolhido pelo lado que fica, na aba do arquivo, num passo só de desfazer,
    // e salva. Se o arquivo ainda tem outros, a comparação volta para o próximo. O commit continua com a pessoa.
    public async Task ResolveConflictAsync(ConflictChoice choice)
    {
        if (DiffPath is not { } path || !File.Exists(path))
            return;
        var view = _conflict;
        var index = view is null ? (int?)null : _conflictIndex;
        CloseDiff();
        OpenFile(path);
        if (ActiveDocument is not { IsImage: false } document || !string.Equals(document.FullPath, path, StringComparison.OrdinalIgnoreCase))
            return;
        if (GitConflict.Resolve(document.Document.Text, choice, index) is not { } resolved)
        {
            SetGitMessage("As marcas de conflito deste arquivo mudaram. Edite à mão ou clique nele de novo.");
            return;
        }

        document.Document.Replace(0, document.Document.TextLength, resolved);
        if (!await SaveAsync(document))
            return;
        await RefreshGitAsync();
        var kept = choice switch
        {
            ConflictChoice.Mine => "ficou a sua versão",
            ConflictChoice.Received => "ficou a versão recebida",
            _ => "ficaram as duas versões",
        };

        var remaining = GitConflict.Sides(resolved)?.Count ?? 0;
        if (remaining > 0 && view is not null)
        {
            await OpenDiffAsync(view.Change, Math.Min(index ?? 0, remaining - 1));
            SetGitMessage($"{Path.GetFileName(path)}: {kept}. {(remaining == 1 ? "Falta 1 conflito" : $"Faltam {remaining} conflitos")} neste arquivo.");
            return;
        }
        SetGitMessage($"{Path.GetFileName(path)}: {kept}. Confira e faça o commit. Ctrl Z desfaz.");
    }

    private static Task<List<DiffRow>> AlignForDiffAsync(string left, string right) => Task.Run(() =>
    {
        var a = LineDiff.SplitLines(left);
        var b = LineDiff.SplitLines(right);
        return LineDiff.Align(a, b, LineDiff.Compare(a, b));
    });

    private void ShowDiff(string path, List<DiffRow> rows, string leftLabel, string rightLabel, string? title = null, bool conflict = false)
    {
        ConflictBar.IsVisible = conflict;
        if (!conflict)
        {
            _conflict = null;
            _conflictFocus = null;
        }
        DiffLeftLabel.Text = leftLabel;
        DiffRightLabel.Text = rightLabel;
        // Um arquivo apagado depois daquele commit não tem mais o que abrir.
        DiffOpenButton.IsVisible = File.Exists(path);

        _diffLeftKinds.Clear();
        _diffRightKinds.Clear();
        _diffLeftKinds.AddRange(rows.Select(row => row.LeftKind));
        _diffRightKinds.AddRange(rows.Select(row => row.RightKind));
        _diffLeftNumbers.Numbers = rows.Select(row => row.LeftNumber).ToList();
        _diffRightNumbers.Numbers = rows.Select(row => row.RightNumber).ToList();

        var leftDocument = new TextDocument(string.Join('\n', rows.Select(row => row.LeftText)));
        var rightDocument = new TextDocument(string.Join('\n', rows.Select(row => row.RightText)));
        _diffLeftColors.Highlighter?.Detach();
        _diffRightColors.Highlighter?.Detach();
        _diffLeftColors.Highlighter = SyntaxHighlighter.ForDocument(path, leftDocument);
        _diffRightColors.Highlighter = SyntaxHighlighter.ForDocument(path, rightDocument);
        DiffLeft.Document = leftDocument;
        DiffRight.Document = rightDocument;
        DiffLeft.FontSize = CodeEditor.FontSize;
        DiffRight.FontSize = CodeEditor.FontSize;

        DiffTitle.Text = title ?? (GitState is { } snapshot ? Path.GetRelativePath(snapshot.TopLevel, path).Replace(Path.DirectorySeparatorChar, '/') : Path.GetFileName(path));
        DiffPath = path;
        DiffHost.IsVisible = true;
        ShowDocumentSurface();
        EmptyState.IsVisible = false;

        var firstChange = rows.FindIndex(row => row.LeftKind != DiffRowKind.Same || row.RightKind != DiffRowKind.Same);
        Dispatcher.UIThread.Post(() =>
        {
            DiffRight.ScrollToVerticalOffset(0);
            if (firstChange > 0)
                DiffRight.ScrollToLine(firstChange + 1);
        }, DispatcherPriority.Loaded);
    }

    private string? DiffPath { get; set; }

    private void OnCloseDiff(object? sender, RoutedEventArgs e) => CloseDiff();

    private void OnDiffOpenFile(object? sender, RoutedEventArgs e)
    {
        var path = DiffPath;
        CloseDiff();
        if (path is not null && File.Exists(path))
            OpenFile(path);
    }

    public void CloseDiff()
    {
        if (!IsDiffOpen)
            return;
        DiffHost.IsVisible = false;
        ShowDocumentSurface();
        UpdateEmptyState();
        FocusActiveDocument();
    }

    private void SyncDiffScroll(AvaloniaEdit.TextEditor from, AvaloniaEdit.TextEditor to)
    {
        if (_syncingDiffScroll)
            return;
        _syncingDiffScroll = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        _syncingDiffScroll = false;
    }

    // Marcas na margem do editor

    private void ScheduleGitMarks()
    {
        if (GitState is not null && _gitMarksDelay is { IsEnabled: false })
            _gitMarksDelay.Start();
    }

    public async Task UpdateGitMarksAsync()
    {
        var request = ++_gitMarksRequest;
        var result = await ComputeGitMarksAsync();
        if (request != _gitMarksRequest)
            return;
        _gutter.SetLineMarks(result?.Marks);
        SetChangeBlocks(result?.Blocks, result?.BaseText);
    }

    private async Task<(IReadOnlyDictionary<int, GitLineKind> Marks, List<ChangeBlock> Blocks, string BaseText)?> ComputeGitMarksAsync()
    {
        if (GitState is not { Status.HeadOid: not null } snapshot || ActiveDocument is not { IsUntitled: false, IsImage: false } document)
            return null;
        if (document.Document.TextLength > MaxMarkedChars || !FileOperations.IsInside(document.FullPath, snapshot.TopLevel))
            return null;

        // Renomeado depois do último commit: compara com o nome antigo, senão a margem ficaria vazia até o commit.
        var relative = Path.GetRelativePath(snapshot.TopLevel, document.FullPath).Replace(Path.DirectorySeparatorChar, '/');
        var source = snapshot.Status.Changes.FirstOrDefault(change => change.Kind == GitChangeKind.Renamed && change.Path == relative)?.OriginalPath ?? relative;
        var key = document.FullPath + "\n" + source;
        if (!_gitBaseTexts.TryGetValue(key, out var baseText))
        {
            var bytes = await Git().ShowHeadAsync(source);
            baseText = bytes is null ? null : TextFile.Decode(bytes)?.Text;
            _gitBaseTexts[key] = baseText;
        }

        // Arquivo novo é inteiro novo: a árvore já mostra isso, sem pintar a margem toda.
        if (baseText is null)
            return null;

        var current = document.Document.Text;
        return await Task.Run(() =>
        {
            var a = LineDiff.SplitLines(baseText);
            var b = LineDiff.SplitLines(current);
            var ops = LineDiff.Compare(a, b);
            return ((IReadOnlyDictionary<int, GitLineKind>)LineDiff.LineMarks(ops, b.Length), ChangeBlocks.FromOps(a, b, ops), baseText);
        });
    }

    // Enviar, receber e repositório novo

    private void OnPushClick(object? sender, RoutedEventArgs e) => _ = PushAsync();
    private void OnPullClick(object? sender, RoutedEventArgs e) => _ = PullAsync();
    private void OnGitInitClick(object? sender, RoutedEventArgs e) => _ = InitRepositoryAsync();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeRemoteName();

    // Vai no texto de um comando do cmd, e o Git aceita nomes com & e %.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex SafeBranchName();

    private bool CanStartGitTask()
    {
        if (!IsTaskRunning)
            return true;
        SetGitMessage("Pare o programa em execução antes.");
        return false;
    }

    public async Task PushAsync()
    {
        if (GitState is not { } snapshot || !CanStartGitTask())
            return;

        // Sem isso, a pessoa colaria o endereço para só depois ver o Git falhar.
        if (snapshot.Status.HeadOid is null)
        {
            SetGitMessage("Faça um commit antes de enviar.");
            return;
        }

        var remote = snapshot.Remotes.FirstOrDefault();
        if (remote is null)
        {
            var url = await AskTextAsync("Para onde enviar?", "Cole o endereço do repositório, como https://github.com/voce/projeto.git", "Conectar",
                value => string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('-') ? "Digite um endereço válido." : null);
            if (url is null)
                return;
            var added = await Git().AddRemoteAsync(url.Trim());
            if (!added.Ok)
            {
                SetGitMessage(added.Message);
                return;
            }
            remote = "origin";
        }

        string command;
        if (snapshot.Status.Upstream is not null)
        {
            command = "git push";
        }
        else if (SafeRemoteName().IsMatch(remote))
        {
            command = $"git push -u {remote} HEAD";
        }
        else
        {
            SetGitMessage("O nome deste remoto não é seguro para enviar por aqui.");
            return;
        }

        StartTask(command, FinishPush);
    }

    public async Task PullAsync()
    {
        if (GitState is not { } snapshot || !CanStartGitTask())
            return;

        var command = "git pull --no-rebase --no-edit";
        var success = "Atualizado.";
        if (snapshot.Status.Upstream is null)
        {
            SetGitMessage("Consultando o repositório remoto…");
            if (await FirstPullCommandAsync(snapshot) is not { } first)
            {
                SetGitMessage("Este branch ainda não está ligado ao remoto. Envie primeiro.");
                return;
            }
            // Juntar as duas histórias cria um commit, e ele precisa de um autor.
            if (!await EnsureGitIdentityAsync())
                return;
            SetGitMessage("");
            command = first;
            success = "Recebido. Agora clique em Enviar.";
        }

        // Receber mexe nos arquivos do disco, então o que está só na tela vai para lá antes.
        foreach (var document in _documents.Where(d => d.IsDirty && !d.IsUntitled).ToList())
        {
            if (!await SaveAsync(document))
                return;
        }

        StartTask(command, code => FinishGitTask(code, success));
    }

    // Um branch sem ligação só recebe o branch de mesmo nome. Assim, o repositório criado no GitHub
    // com README se junta ao projeto, e um branch novo nunca recebe o main sem a pessoa pedir.
    private async Task<string?> FirstPullCommandAsync(GitSnapshot snapshot)
    {
        if (snapshot.Remotes.FirstOrDefault() is not { } remote || snapshot.Status.Branch is not { } branch)
            return null;
        if (!SafeRemoteName().IsMatch(remote) || !SafeBranchName().IsMatch(branch))
            return null;
        if (await Git().RemoteBranchOidAsync(remote, branch) is null)
            return null;
        return $"git pull --no-rebase --no-edit --allow-unrelated-histories {remote} {branch}";
    }

    private async void FinishGitTask(int code, string success)
    {
        await RefreshGitAsync();
        await CheckOpenDocumentsAsync();
        SetGitMessage(GitTaskMessage(code, success));
        if (code != 0)
            await OfferNewRemoteIfMissingAsync();
    }

    private async void FinishPush(int code)
    {
        await RefreshGitAsync();
        await CheckOpenDocumentsAsync();
        if (code != 0 && await OfferNewRemoteIfMissingAsync())
            return;
        SetGitMessage(code == 0
            ? GitTaskMessage(code, "Enviado.")
            : await PushRefusalMessageAsync() ?? GitTaskMessage(code, "Enviado."));
    }

    // Repositório apagado ou renomeado no GitHub: sem isto, Enviar e Receber davam "not found" para sempre,
    // e não havia onde trocar o endereço. Pergunta o novo na hora, e a barra de comando também troca.
    private async Task<bool> OfferNewRemoteIfMissingAsync()
    {
        if (GitState is not { Remotes.Count: > 0 } snapshot || !SafeRemoteName().IsMatch(snapshot.Remotes[0])
            || !await Git().IsRemoteMissingAsync(snapshot.Remotes[0]))
            return false;
        var url = await Git().RemoteUrlAsync(snapshot.Remotes[0]) ?? snapshot.Remotes[0];
        SetGitMessage($"O repositório {url} não foi encontrado. Para trocar o endereço, use \"Trocar o endereço do remoto\" na barra de comando.");
        await ChangeRemoteUrlAsync($"{url} não foi encontrado. Ele pode ter sido apagado ou renomeado, ou esta conta não tem acesso. Cole o endereço certo:");
        return true;
    }

    public async Task<bool> ChangeRemoteUrlAsync(string? reason = null)
    {
        if (GitState is not { Remotes.Count: > 0 } snapshot)
            return false;
        var remote = snapshot.Remotes[0];
        if (!SafeRemoteName().IsMatch(remote))
        {
            SetGitMessage("O nome deste remoto não é seguro para trocar por aqui.");
            return false;
        }

        var current = await Git().RemoteUrlAsync(remote) ?? "";
        var url = await AskTextAsync("Trocar o endereço do remoto", reason ?? "Cole o endereço novo, como https://github.com/voce/projeto.git", "Trocar",
            value => string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('-') ? "Digite um endereço válido." : null, current);
        if (url is null || url.Trim() == current)
            return false;

        var result = await Git().SetRemoteUrlAsync(remote, url.Trim());
        await RefreshGitAsync();
        SetGitMessage(result.Ok ? "Endereço trocado. Agora clique em Enviar." : result.Message);
        return result.Ok;
    }

    // Só explica quando o motivo é mesmo esse: falta de permissão não se resolve com Receber.
    private async Task<string?> PushRefusalMessageAsync()
    {
        if (GitState is not { } snapshot)
            return null;

        if (snapshot.Status.Upstream is not null)
        {
            if (!(await Git().FetchQuietlyAsync()).Ok)
                return null;
            await RefreshGitAsync();
            return GitState is { Status.Behind: > 0 }
                ? "O repositório remoto tem commits novos. Clique em Receber e depois em Enviar."
                : null;
        }

        if (snapshot.Remotes.FirstOrDefault() is not { } remote || snapshot.Status.Branch is not { } branch
            || await Git().RemoteBranchOidAsync(remote, branch) is not { } oid || await Git().HeadContainsAsync(oid))
            return null;
        return "O repositório remoto já tem commits que este projeto não tem, como um README criado junto. Clique em Receber e depois em Enviar.";
    }

    // Com conflito, o aviso em vermelho do painel já explica o que fazer: repetir só atrapalha.
    private string GitTaskMessage(int code, string success)
    {
        if (GitConflictNotice.IsVisible)
            return "";
        return code == 0 ? success : "O Git não conseguiu concluir. Veja a saída no terminal.";
    }

    public string GitTaskMessageForTest(int code, string success) => GitTaskMessage(code, success);
    public bool IsGitConflictNoticeVisible => GitConflictNotice.IsVisible;

    public async Task InitRepositoryAsync()
    {
        if (RootFolder is null)
            return;
        var result = await new GitClient(RootFolder, GitEnvironment).InitAsync();
        await RefreshGitAsync();
        if (!result.Ok)
            GitEmptyMessage.Text = result.Message;
    }

    // Trocados nos testes, que nunca instalam nada de verdade.
    public Func<bool> IsGitInstalled { get; set; } = () => LanguageServerCatalog.FindExecutable("git") is not null;
    public string? GitInstallCommand { get; set; }
    public string GitEmptyTitleText => GitEmptyTitle.Text ?? "";
    public string GitEmptyMessageText => GitEmptyMessage.Text ?? "";
    public bool IsGitInitOffered => GitInitButton.IsVisible;
    public bool IsGitInstallOffered => GitInstallButton.IsVisible;

    // Um Windows novo não vem com Git: sem isto, o botão de iniciar não teria como funcionar.
    private void UpdateGitEmptyPanel()
    {
        var installed = IsGitInstalled();
        GitEmptyTitle.Text = installed ? "Esta pasta não usa Git" : "O Git não está instalado";
        GitEmptyMessage.Text = installed
            ? "O Git guarda o histórico do projeto e permite publicar no GitHub."
            : "O Git guarda o histórico do projeto e permite publicar no GitHub. A Sirius instala para você. Numa conta de administrador, o Windows pede uma confirmação.";
        GitInitButton.IsVisible = installed;
        GitInstallButton.IsVisible = !installed;
    }

    private void OnGitInstallClick(object? sender, RoutedEventArgs e) => _ = OfferGitInstallAsync();

    public async Task OfferGitInstallAsync()
    {
        var command = GitInstallCommand ?? ToolPaths.GitInstallCommand;
        // O instalador do Git pede o nível mais alto que a conta tem: administrador confirma, e conta comum instala só para si.
        var choice = await AskAsync("Instalar o Git?",
            $"Se a sua conta for de administrador, o Windows vai pedir uma confirmação para o instalador do Git. Pode aceitar.\n\nO comando vai rodar no terminal:\n\n{command}",
            "Instalar", null);
        if (choice != DialogChoice.Primary)
            return;

        StartTask(command, code =>
        {
            _ = RefreshGitAsync();
            // O status do fim da tarefa é o último a aparecer, por isso vem depois dele.
            ShowStatus(IsGitInstalled() ? "Git instalado."
                : code == 0 ? "Instalado, mas a Sirius não encontrou o Git. Reabra a Sirius."
                : "O Git não foi instalado. Veja a saída no terminal.");
        });
    }

    // Branches

    private void OnBranchClick(object? sender, RoutedEventArgs e) => _ = OpenBranchPickerAsync();

    public async Task OpenBranchPickerAsync()
    {
        if (GitState is not { } snapshot)
            return;

        var branches = await Git().BranchesAsync();
        var items = new List<(string Name, string Hint, Action Run)>
        {
            ("Criar branch", "", () => _ = CreateBranchAsync()),
        };
        foreach (var branch in branches)
        {
            var current = branch == snapshot.Status.Branch;
            items.Add((branch, current ? "atual" : "", () => { if (!current) _ = SwitchBranchAsync(branch); }));
        }

        OpenPalette();
        _paletteOverride = items;
        RefreshPalette();
    }

    private async Task<bool> SaveBeforeBranchChangeAsync()
    {
        foreach (var document in _documents.Where(d => d.IsDirty && !d.IsUntitled).ToList())
        {
            if (!await SaveAsync(document))
                return false;
        }
        return true;
    }

    public async Task SwitchBranchAsync(string branch)
    {
        if (!await SaveBeforeBranchChangeAsync())
            return;

        var result = await Git().SwitchAsync(branch);
        if (!result.Ok)
            ShowStatus(result.Message);
        await RefreshGitAsync();
        await CheckOpenDocumentsAsync();
        RefreshTree();
        ReindexProject();
    }

    public async Task CreateBranchAsync()
    {
        var name = await AskTextAsync("Criar branch", "O branch nasce a partir do ponto atual, e você passa a trabalhar nele.", "Criar",
            value => string.IsNullOrWhiteSpace(value) ? "Digite um nome." : null);
        if (name is null)
            return;

        name = name.Trim();
        if (!await Git().IsValidBranchNameAsync(name))
        {
            ShowStatus("Este nome não serve para um branch. Evite espaços e símbolos.");
            return;
        }

        var result = await Git().CreateBranchAsync(name);
        if (!result.Ok)
            ShowStatus(result.Message);
        await RefreshGitAsync();
    }
}
