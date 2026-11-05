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
using Sirius.Theming;

namespace Sirius;

// O Git dentro da IDE: painel de mudanças, commit, comparação, envio e branches.
public partial class MainWindow
{
    private const int MaxMarkedChars = 2_000_000;
    private static readonly TimeSpan FetchInterval = TimeSpan.FromSeconds(60);

    private readonly ObservableCollection<GitRow> _gitRows = new();
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
            RefreshTree();
            _gutter.SetLineMarks(null);
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
        _gitRows.Clear();
        if (GitState is not { } snapshot)
            return;

        // Conflitos primeiro, porque impedem o resto.
        foreach (var change in snapshot.Status.Changes.OrderBy(c => c.Kind != GitChangeKind.Conflict).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(change.Path)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
            _gitRows.Add(new GitRow(Path.GetFileName(change.Path), folder, LetterOf(change.Kind), BrushOf(change.Kind),
                !_gitExcluded.Contains(change.Path), change));
        }
        GitConflictNotice.IsVisible = snapshot.Status.Changes.Any(c => c.Kind == GitChangeKind.Conflict);
        GitNothing.IsVisible = _gitRows.Count == 0;
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
        if (GitState is not { } snapshot)
            return false;

        var message = (GitMessage.Text ?? "").Trim();
        if (message.Length == 0)
        {
            SetGitMessage("Escreva uma mensagem para o commit.");
            return false;
        }

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

        // O commit grava o que está no disco, então o que está só na tela precisa ir para lá antes.
        foreach (var document in _documents.Where(d => d.IsDirty && !d.IsUntitled).ToList())
        {
            if (!await SaveAsync(document))
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

    public async Task OpenDiffAsync(GitChange change)
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

        var rows = await AlignForDiffAsync(left, right);
        ShowDiff(full, rows, "Último commit", "Agora");
    }

    private static Task<List<DiffRow>> AlignForDiffAsync(string left, string right) => Task.Run(() =>
    {
        var a = LineDiff.SplitLines(left);
        var b = LineDiff.SplitLines(right);
        return LineDiff.Align(a, b, LineDiff.Compare(a, b));
    });

    private void ShowDiff(string path, List<DiffRow> rows, string leftLabel, string rightLabel, string? title = null)
    {
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
        var marks = await ComputeGitMarksAsync();
        if (request == _gitMarksRequest)
            _gutter.SetLineMarks(marks);
    }

    private async Task<IReadOnlyDictionary<int, GitLineKind>?> ComputeGitMarksAsync()
    {
        if (GitState is not { Status.HeadOid: not null } snapshot || ActiveDocument is not { IsUntitled: false, IsImage: false } document)
            return null;
        if (document.Document.TextLength > MaxMarkedChars || !FileOperations.IsInside(document.FullPath, snapshot.TopLevel))
            return null;

        var key = document.FullPath;
        if (!_gitBaseTexts.TryGetValue(key, out var baseText))
        {
            var relative = Path.GetRelativePath(snapshot.TopLevel, key).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = await Git().ShowHeadAsync(relative);
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
            return (IReadOnlyDictionary<int, GitLineKind>)LineDiff.LineMarks(LineDiff.Compare(a, b), b.Length);
        });
    }

    // Enviar, receber e repositório novo

    private void OnPushClick(object? sender, RoutedEventArgs e) => _ = PushAsync();
    private void OnPullClick(object? sender, RoutedEventArgs e) => _ = PullAsync();
    private void OnGitInitClick(object? sender, RoutedEventArgs e) => _ = InitRepositoryAsync();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeRemoteName();

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

        StartTask(command, code => FinishGitTask(code, "Enviado."));
    }

    public async Task PullAsync()
    {
        if (GitState is not { } snapshot || !CanStartGitTask())
            return;
        if (snapshot.Status.Upstream is null)
        {
            SetGitMessage("Este branch ainda não está ligado ao remoto. Envie primeiro.");
            return;
        }

        // Receber mexe nos arquivos do disco, então o que está só na tela vai para lá antes.
        foreach (var document in _documents.Where(d => d.IsDirty && !d.IsUntitled).ToList())
        {
            if (!await SaveAsync(document))
                return;
        }

        StartTask("git pull --no-rebase --no-edit", code => FinishGitTask(code, "Atualizado."));
    }

    private async void FinishGitTask(int code, string success)
    {
        await RefreshGitAsync();
        await CheckOpenDocumentsAsync();
        if (GitState is { } snapshot && snapshot.Status.Changes.Any(c => c.Kind == GitChangeKind.Conflict))
            SetGitMessage("Há conflitos. Edite os arquivos marcados e faça o commit.");
        else
            SetGitMessage(code == 0 ? success : "O Git não conseguiu concluir. Veja a saída no terminal.");
    }

    public async Task InitRepositoryAsync()
    {
        if (RootFolder is null)
            return;
        var result = await new GitClient(RootFolder, GitEnvironment).InitAsync();
        await RefreshGitAsync();
        if (!result.Ok)
            GitEmptyMessage.Text = result.Message;
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
