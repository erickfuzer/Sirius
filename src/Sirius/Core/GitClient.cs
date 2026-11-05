using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Sirius.Core;

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked, Conflict }

// Path é relativo à raiz do repositório e usa a barra normal, como o Git escreve.
public sealed record GitChange(string Path, string? OriginalPath, GitChangeKind Kind, bool DeletedInWorktree);

public sealed record GitStatus(
    string? Branch, string? HeadOid, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitChange> Changes);

public sealed record GitResult(int ExitCode, string Output, string Error, byte[] OutputBytes)
{
    public bool Ok => ExitCode == 0;

    // A primeira linha útil do erro, que é o que a pessoa precisa ler.
    public string Message =>
        Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "O Git não conseguiu concluir.";
}

public sealed record GitSnapshot(string TopLevel, bool IsMerging, GitStatus Status, IReadOnlyList<string> Remotes);

// Lê a saída de "git status --porcelain=v2 --branch -z".
public static class GitStatusParser
{
    public static GitStatus Parse(string output)
    {
        string? branch = null;
        string? oid = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var changes = new List<GitChange>();

        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2)
                continue;

            switch (record[0])
            {
                case '#':
                    var parts = record.Split(' ');
                    if (parts.Length < 3)
                        break;
                    switch (parts[1])
                    {
                        case "branch.oid":
                            oid = parts[2] == "(initial)" ? null : parts[2];
                            break;
                        case "branch.head":
                            branch = parts[2] == "(detached)" ? null : parts[2];
                            break;
                        case "branch.upstream":
                            upstream = parts[2];
                            break;
                        case "branch.ab" when parts.Length >= 4:
                            _ = int.TryParse(parts[2].TrimStart('+'), out ahead);
                            _ = int.TryParse(parts[3].TrimStart('-'), out behind);
                            break;
                    }
                    break;
                case '1':
                    var ordinary = record.Split(' ', 9);
                    if (ordinary.Length == 9)
                        changes.Add(new GitChange(ordinary[8], null, KindOf(ordinary[1]), ordinary[1][1] == 'D'));
                    break;
                case '2':
                    var renamed = record.Split(' ', 10);
                    if (renamed.Length == 10 && i + 1 < records.Length)
                    {
                        changes.Add(new GitChange(renamed[9], records[i + 1], GitChangeKind.Renamed, renamed[1][1] == 'D'));
                        i++;
                    }
                    break;
                case 'u':
                    var conflict = record.Split(' ', 11);
                    if (conflict.Length == 11)
                        changes.Add(new GitChange(conflict[10], null, GitChangeKind.Conflict, false));
                    break;
                case '?':
                    changes.Add(new GitChange(record[2..], null, GitChangeKind.Untracked, false));
                    break;
            }
        }
        return new GitStatus(branch, oid, upstream, ahead, behind, changes);
    }

    private static GitChangeKind KindOf(string xy)
    {
        if (xy.Contains('D'))
            return GitChangeKind.Deleted;
        return xy.Contains('A') ? GitChangeKind.Added : GitChangeKind.Modified;
    }
}

// Roda o programa git de verdade, em vez de reimplementar o formato do repositório.
public sealed class GitClient(string folder, IReadOnlyDictionary<string, string>? environment = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(5);

    // Abrir uma pasta já consulta o status, então a consulta não pode executar
    // programas definidos na configuração de um repositório desconhecido.
    private static readonly string[] CommonArguments =
        ["--no-pager", "--literal-pathspecs", "-c", "core.quotepath=false", "-c", "core.fsmonitor=false"];

    public async Task<GitResult> RunAsync(IEnumerable<string> arguments, string? input = null, TimeSpan? timeout = null, bool quiet = false)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in CommonArguments.Concat(arguments))
            info.ArgumentList.Add(argument);

        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_EDITOR"] = "true";
        if (quiet)
            info.Environment["GCM_INTERACTIVE"] = "never";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                info.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new Win32Exception();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or DirectoryNotFoundException)
        {
            return new GitResult(-1, "", "O programa git não foi encontrado.", []);
        }

        using (process)
        {
            using var cancel = new CancellationTokenSource(timeout ?? DefaultTimeout);
            try
            {
                if (input is not null)
                    await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), cancel.Token);
                process.StandardInput.Close();

                using var bytes = new MemoryStream();
                var readOutput = process.StandardOutput.BaseStream.CopyToAsync(bytes, cancel.Token);
                var readError = process.StandardError.ReadToEndAsync(cancel.Token);
                await Task.WhenAll(readOutput, readError);
                await process.WaitForExitAsync(cancel.Token);

                var data = bytes.ToArray();
                return new GitResult(process.ExitCode, Encoding.UTF8.GetString(data), await readError, data);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killError) when (killError is InvalidOperationException or Win32Exception)
                {
                    // O processo terminou enquanto tentávamos encerrá-lo.
                }
                return new GitResult(-1, "", "O Git demorou demais para responder.", []);
            }
        }
    }

    // Null quando a pasta não é um repositório ou o git não está disponível.
    public async Task<GitSnapshot?> SnapshotAsync()
    {
        var location = await RunAsync(["rev-parse", "--show-toplevel", "--absolute-git-dir"]);
        if (!location.Ok)
            return null;

        var lines = location.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return null;

        var status = await RunAsync(["--no-optional-locks", "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"]);
        if (!status.Ok)
            return null;

        var remotes = await RunAsync(["remote"]);
        return new GitSnapshot(
            Path.GetFullPath(lines[0]),
            File.Exists(Path.Combine(lines[1], "MERGE_HEAD")),
            GitStatusParser.Parse(status.Output),
            remotes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // O conteúdo do arquivo no último commit. Null quando ele ainda não existe lá.
    public Task<byte[]?> ShowHeadAsync(string path) => ShowAtAsync("HEAD", path);

    // O conteúdo do arquivo em um commit. Null quando ele não existe lá.
    public async Task<byte[]?> ShowAtAsync(string revision, string path)
    {
        var result = await RunAsync(["show", "--no-textconv", revision + ":" + path]);
        return result.Ok ? result.OutputBytes : null;
    }

    // Null se o Git falhar. Vai do mais novo ao mais antigo, a partir do commit atual.
    // Sem assinatura: mostrá-la executaria o programa que a configuração do repositório indicar.
    public async Task<List<GitCommit>?> LogAsync(int skip, int count)
    {
        var result = await RunAsync(["log", "--no-show-signature", "--no-notes", "--no-color", "--encoding=UTF-8",
            "--format=tformat:" + GitHistory.LogFormat, $"--skip={skip}", $"--max-count={count}", "HEAD", "--"]);
        return result.Ok ? GitHistory.ParseLog(result.Output) : null;
    }

    // Null se o Git falhar. O primeiro commit do repositório não tem com o que comparar, e aí vale o "--root".
    public async Task<List<GitChange>?> CommitChangesAsync(GitCommit commit)
    {
        string[] revisions = commit.FirstParent is { } parent ? [parent, commit.Oid] : ["--root", commit.Oid];
        var result = await RunAsync(["diff-tree", "--no-ext-diff", "--no-commit-id", "-r", "-z", "-M", "--name-status", .. revisions]);
        return result.Ok ? GitHistory.ParseChanges(result.Output) : null;
    }

    public async Task<GitResult> CommitAsync(string message, IReadOnlyList<GitChange> changes, bool isMerging)
    {
        // Um apagamento que já está preparado não existe mais para o "git add" encontrar.
        var toAdd = changes
            .Where(change => change.Kind != GitChangeKind.Deleted || change.DeletedInWorktree)
            .Select(change => change.Path)
            .ToList();
        if (toAdd.Count > 0)
        {
            var added = await RunAsync(["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], Nul(toAdd));
            if (!added.Ok)
                return added;
        }

        // Durante um merge o commit precisa levar tudo, e não só os arquivos escolhidos.
        if (isMerging)
            return await RunAsync(["commit", "-m", message], null, CommitTimeout);

        var paths = changes.SelectMany(change => change.OriginalPath is null ? [change.Path] : new[] { change.Path, change.OriginalPath });
        return await RunAsync(["commit", "-m", message, "--pathspec-from-file=-", "--pathspec-file-nul"], Nul(paths), CommitTimeout);
    }

    public async Task<GitResult> DiscardAsync(GitChange change)
    {
        // O que o Git não conhece no último commit só precisa sair da área de preparação.
        if (change.Kind is GitChangeKind.Added or GitChangeKind.Renamed)
        {
            var removed = await RunAsync(["rm", "--cached", "-q", "-f", "--ignore-unmatch", "--", change.Path]);
            if (!removed.Ok)
                return removed;
        }
        var restore = change.Kind switch
        {
            GitChangeKind.Renamed => change.OriginalPath,
            GitChangeKind.Modified or GitChangeKind.Deleted or GitChangeKind.Conflict => change.Path,
            _ => null,
        };
        return restore is null
            ? new GitResult(0, "", "", [])
            : await RunAsync(["restore", "--source=HEAD", "--staged", "--worktree", "--", restore]);
    }

    public async Task<List<string>> BranchesAsync()
    {
        var result = await RunAsync(["for-each-ref", "--format=%(refname:short)", "refs/heads"]);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public async Task<bool> IsValidBranchNameAsync(string name) =>
        (await RunAsync(["check-ref-format", "--branch", name])).Ok;

    public Task<GitResult> SwitchAsync(string branch) => RunAsync(["switch", "--", branch]);

    public Task<GitResult> CreateBranchAsync(string branch) => RunAsync(["switch", "-c", branch]);

    public Task<GitResult> InitAsync() => RunAsync(["init", "-b", "main"]);

    public Task<GitResult> AddRemoteAsync(string url) => RunAsync(["remote", "add", "origin", "--", url]);

    public Task<GitResult> FetchQuietlyAsync() =>
        RunAsync(["fetch", "--quiet", "--no-tags", "--no-write-fetch-head"], null, TimeSpan.FromSeconds(30), quiet: true);

    public async Task<string?> ConfigAsync(string key)
    {
        var result = await RunAsync(["config", "--get", key]);
        var value = result.Output.Trim();
        return result.Ok && value.Length > 0 ? value : null;
    }

    public Task<GitResult> SetGlobalConfigAsync(string key, string value) =>
        RunAsync(["config", "--global", key, value]);

    private static string Nul(IEnumerable<string> paths) => string.Concat(paths.Select(path => path + "\0"));
}
