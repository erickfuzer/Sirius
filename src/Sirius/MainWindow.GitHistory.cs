using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius;

// O histórico de commits no painel de Git. Só lê: nada aqui muda o repositório.
public partial class MainWindow
{
    private const int HistoryPageSize = 100;
    private const int MaxFilesPerCommit = 1000;
    private const int MaxDiffBytes = 5 * 1024 * 1024;

    private readonly ObservableCollection<object> _historyRows = new();
    private readonly List<GitCommit> _historyCommits = new();
    private readonly HashSet<string> _historyExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GitChange>> _historyFiles = new(StringComparer.Ordinal);

    private bool _historyHasMore;
    private string? _historyHead;
    private int _historyRequest;
    private int _historyDiffRequest;
    private (string Oid, string Path)? _historySelected;

    public bool IsHistoryShown => GitView.IsVisible && GitHistoryList.IsVisible;
    public IReadOnlyList<object> HistoryRows => _historyRows;
    public string DiffTitleText => DiffTitle.Text ?? "";
    public string DiffLeftLabelText => DiffLeftLabel.Text ?? "";
    public string DiffRightLabelText => DiffRightLabel.Text ?? "";
    public bool IsDiffOpenButtonVisible => DiffOpenButton.IsVisible;
    public bool IsCommitFormVisible => GitCommitButton.IsVisible;
    public string GitChangesTabLabel => GitChangesTab.Content as string ?? "";

    private void InitializeGitHistory() => GitHistoryList.ItemsSource = _historyRows;

    public void OpenGitHistory()
    {
        if (RootFolder is null)
            return;
        OpenGit();
        ShowGitTab(history: true);
    }

    private void OnGitChangesTab(object? sender, RoutedEventArgs e)
    {
        ShowGitTab(history: false);
        GitMessage.Focus();
    }

    private void OnGitHistoryTab(object? sender, RoutedEventArgs e) => ShowGitTab(history: true);

    public void ShowGitTab(bool history)
    {
        GitChangesTab.Classes.Set("on", !history);
        GitHistoryTab.Classes.Set("on", history);
        GitMessageBox.IsVisible = !history;
        GitCommitButton.IsVisible = !history;
        GitNotices.IsVisible = !history;
        GitList.IsVisible = !history;
        GitHistoryList.IsVisible = history;
        GitHistoryEmpty.IsVisible = history && GitState is { Status.HeadOid: null };
        SetGitMessage("");

        if (history && (_historyHead is null || _historyHead != GitState?.Status.HeadOid))
            _ = LoadHistoryAsync();
    }

    public void ResetHistory()
    {
        _historyRequest++;
        _historyDiffRequest++;
        _historyHead = null;
        _historyHasMore = false;
        _historySelected = null;
        _historyCommits.Clear();
        _historyExpanded.Clear();
        _historyFiles.Clear();
        _historyRows.Clear();
    }

    // Recomeça do commit atual e mantém abertos os commits que continuam na lista.
    public async Task LoadHistoryAsync()
    {
        if (GitState is not { } snapshot)
            return;

        var request = ++_historyRequest;
        var head = snapshot.Status.HeadOid;
        GitHistoryEmpty.IsVisible = head is null;
        if (head is null)
        {
            ResetHistory();
            return;
        }

        var commits = await Git().LogAsync(0, HistoryPageSize + 1);
        if (request != _historyRequest)
            return;
        if (commits is null)
        {
            SetGitMessage("Não foi possível ler o histórico.");
            return;
        }

        _historyHead = head;
        _historyCommits.Clear();
        AddHistoryPage(commits);
        _historyExpanded.IntersectWith(_historyCommits.Select(commit => commit.Oid));
        RebuildHistoryRows();
    }

    public async Task LoadMoreHistoryAsync()
    {
        var request = ++_historyRequest;
        var commits = await Git().LogAsync(_historyCommits.Count, HistoryPageSize + 1);
        if (request != _historyRequest)
            return;
        if (commits is null)
        {
            SetGitMessage("Não foi possível ler o histórico.");
            return;
        }

        // O último item é só o botão: trocá-lo pelos novos commits deixa a rolagem onde está.
        if (_historyRows.Count > 0 && _historyRows[^1] is HistoryMoreRow)
            _historyRows.RemoveAt(_historyRows.Count - 1);

        var first = _historyCommits.Count;
        AddHistoryPage(commits);
        foreach (var commit in _historyCommits.Skip(first))
            _historyRows.Add(CommitRow(commit));
        if (_historyHasMore)
            _historyRows.Add(new HistoryMoreRow("Carregar mais commits"));
    }

    private void AddHistoryPage(List<GitCommit> commits)
    {
        _historyHasMore = commits.Count > HistoryPageSize;
        _historyCommits.AddRange(commits.Take(HistoryPageSize));
    }

    private HistoryCommitRow CommitRow(GitCommit commit)
    {
        var now = DateTimeOffset.Now;
        var date = commit.Date.ToOffset(now.Offset);
        var detail = $"{commit.Author} · {GitHistory.RelativeDate(commit.Date, now)} · {commit.ShortOid}";
        var tip = $"{commit.Message}\n\n{commit.Author} · {date:dd/MM/yyyy HH:mm} · {commit.ShortOid}";
        return new HistoryCommitRow(commit, detail, tip, _historyExpanded.Contains(commit.Oid) ? ChevronOpen : ChevronClosed);
    }

    private IEnumerable<object> FileRows(GitCommit commit)
    {
        if (!_historyFiles.TryGetValue(commit.Oid, out var files))
            yield break;
        if (files.Count == 0)
        {
            // Um merge sem conflito ou um commit vazio não muda nada em relação ao anterior.
            yield return new HistoryNoteRow("Nenhum arquivo mudou neste commit.");
            yield break;
        }

        foreach (var change in files.Take(MaxFilesPerCommit))
        {
            var folder = Path.GetDirectoryName(change.Path)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
            yield return new HistoryFileRow(commit, change, Path.GetFileName(change.Path), folder, LetterOf(change.Kind), BrushOf(change.Kind),
                FileRowBackground(commit, change));
        }
        if (files.Count > MaxFilesPerCommit)
            yield return new HistoryNoteRow($"E mais {files.Count - MaxFilesPerCommit} arquivos.");
    }

    private IBrush FileRowBackground(GitCommit commit, GitChange change) =>
        _historySelected == (commit.Oid, change.Path) ? Palette.Current.Tint : Brushes.Transparent;

    private void RebuildHistoryRows()
    {
        _historyRows.Clear();
        foreach (var commit in _historyCommits)
        {
            _historyRows.Add(CommitRow(commit));
            if (_historyExpanded.Contains(commit.Oid))
            {
                foreach (var row in FileRows(commit))
                    _historyRows.Add(row);
            }
        }
        if (_historyHasMore)
            _historyRows.Add(new HistoryMoreRow("Carregar mais commits"));
    }

    private void OnHistoryCommitClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HistoryCommitRow row)
            _ = ToggleCommitAsync(row.Commit);
    }

    private void OnHistoryFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HistoryFileRow row)
            _ = OpenCommitDiffAsync(row.Commit, row.Change);
    }

    private void OnHistoryMoreClick(object? sender, RoutedEventArgs e) => _ = LoadMoreHistoryAsync();

    private async void OnHistoryCopyOid(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HistoryCommitRow row && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(row.Commit.Oid);
            SetGitMessage($"Código {row.Commit.ShortOid} copiado.");
        }
    }

    // Vários podem ficar abertos: fechar um acima do clique faria a lista pular debaixo do mouse.
    public async Task ToggleCommitAsync(GitCommit commit)
    {
        if (_historyExpanded.Remove(commit.Oid))
        {
            var open = IndexOfCommit(commit);
            if (open < 0)
                return;
            while (open + 1 < _historyRows.Count && _historyRows[open + 1] is HistoryFileRow or HistoryNoteRow)
                _historyRows.RemoveAt(open + 1);
            _historyRows[open] = CommitRow(commit);
            return;
        }

        if (!_historyFiles.ContainsKey(commit.Oid))
        {
            var files = await Git().CommitChangesAsync(commit);
            if (files is null)
            {
                SetGitMessage("Não foi possível ler os arquivos deste commit.");
                return;
            }
            _historyFiles[commit.Oid] = files;
        }

        // Enquanto o Git respondia, a lista pode ter sido recarregada ou o commit já ter sido aberto.
        var index = IndexOfCommit(commit);
        if (index < 0 || !_historyExpanded.Add(commit.Oid))
            return;

        _historyRows[index] = CommitRow(commit);
        foreach (var row in FileRows(commit))
            _historyRows.Insert(++index, row);
    }

    private int IndexOfCommit(GitCommit commit)
    {
        for (var i = 0; i < _historyRows.Count; i++)
        {
            if (_historyRows[i] is HistoryCommitRow row && row.Commit.Oid == commit.Oid)
                return i;
        }
        return -1;
    }

    public async Task OpenCommitDiffAsync(GitCommit commit, GitChange change)
    {
        if (GitState is not { } snapshot)
            return;

        var request = ++_historyDiffRequest;
        var git = Git();
        var left = change.Kind == GitChangeKind.Added || commit.FirstParent is not { } parent
            ? ""
            : await ReadForDiffAsync(git, parent, change.OriginalPath ?? change.Path);
        var right = change.Kind == GitChangeKind.Deleted
            ? ""
            : await ReadForDiffAsync(git, commit.Oid, change.Path);
        if (request != _historyDiffRequest)
            return;
        if (left is null || right is null)
        {
            SetGitMessage("Este arquivo é grande demais ou binário e não pode ser comparado.");
            return;
        }

        var rows = await AlignForDiffAsync(left, right);
        if (request != _historyDiffRequest)
            return;

        SetGitMessage("");
        SelectHistoryFile(commit, change);
        var title = change.OriginalPath is { } original ? $"{original} → {change.Path}" : change.Path;
        ShowDiff(FullPathOf(snapshot, change.Path), rows, "Antes", $"Neste commit ({commit.ShortOid})", title);
    }

    // Texto vazio para o que não existe naquele commit, e null para o que não dá para comparar.
    private static async Task<string?> ReadForDiffAsync(GitClient git, string revision, string path)
    {
        var bytes = await git.ShowAtAsync(revision, path);
        if (bytes is null)
            return "";
        return bytes.Length > MaxDiffBytes ? null : TextFile.Decode(bytes)?.Text;
    }

    private void SelectHistoryFile(GitCommit commit, GitChange change)
    {
        _historySelected = (commit.Oid, change.Path);
        for (var i = 0; i < _historyRows.Count; i++)
        {
            if (_historyRows[i] is HistoryFileRow row)
            {
                var background = FileRowBackground(row.Commit, row.Change);
                if (!Equals(row.Background, background))
                    _historyRows[i] = row with { Background = background };
            }
        }
    }
}
