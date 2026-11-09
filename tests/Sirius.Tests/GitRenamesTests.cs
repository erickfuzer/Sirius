using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class GitRenamesTests
{
    private static (string, byte[]) File(string path, string text) => (path, Encoding.UTF8.GetBytes(text));

    // Sem Git

    [Fact]
    public void Junta_conteudo_igual_mesmo_com_outra_quebra_de_linha()
    {
        var pairs = GitRenames.Pair([File("index.php", "<?php\necho 1;\n")], [File("inicio.php", "<?php\r\necho 1;\r\n")]);
        Assert.Equal([("index.php", "inicio.php")], pairs);
    }

    [Fact]
    public void Junta_o_renomeado_e_editado()
    {
        var old = "a\nb\nc\nd\ne\nf\n";
        var pairs = GitRenames.Pair([File("css/velho.css", old)], [File("css/novo.css", old.Replace("c\n", "C\n"))]);
        Assert.Equal([("css/velho.css", "css/novo.css")], pairs);
    }

    [Fact]
    public void Conteudo_diferente_continua_apagado_e_novo()
    {
        Assert.Empty(GitRenames.Pair([File("a.txt", "um\ndois\ntres\n")], [File("b.txt", "quatro\ncinco\nseis\n")]));
        // Parecido, mas de outro tipo: o Git até juntaria, aqui não se compara para não custar caro.
        Assert.Empty(GitRenames.Pair([File("a.css", "a\nb\nc\nd\n")], [File("a.js", "a\nb\nc\nX\n")]));
    }

    [Fact]
    public void Arquivo_vazio_nao_vira_renomeacao()
    {
        Assert.Empty(GitRenames.Pair([File("a.txt", "")], [File("b.txt", "")]));
    }

    [Fact]
    public void Com_duas_copias_iguais_prefere_o_mesmo_nome()
    {
        var pairs = GitRenames.Pair([File("a/logo.txt", "x\n")], [File("b/outro.txt", "x\n"), File("c/logo.txt", "x\n")]);
        Assert.Equal([("a/logo.txt", "c/logo.txt")], pairs);
    }

    [Fact]
    public void Le_a_saida_do_cat_file()
    {
        var output = Encoding.ASCII.GetBytes("aaa blob 3\nabc\nbbb missing\nccc blob 0\n\n");
        var contents = GitClient.ParseBatch(output);
        Assert.Equal("abc", Encoding.ASCII.GetString(contents["aaa"]));
        Assert.Empty(contents["ccc"]);
        Assert.False(contents.ContainsKey("bbb"));
    }

    // Com Git de verdade

    private static async Task<GitClient> Committed(TempFolder temp)
    {
        var git = await GitClientTests.Repository(temp);
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", "base"])).Ok);
        return git;
    }

    [Fact]
    public async Task Renomeado_no_explorer_aparece_como_renomeado_e_o_commit_leva_os_dois()
    {
        using var temp = new TempFolder();
        temp.File("index.php", "<?php\necho 'oi';\n");
        temp.File("outro.txt", "fica");
        var git = await Committed(temp);
        System.IO.File.Move(Path.Combine(temp.Path, "index.php"), Path.Combine(temp.Path, "inicio.php"));

        var change = Assert.Single((await git.SnapshotAsync())!.Status.Changes);
        Assert.Equal(GitChangeKind.Renamed, change.Kind);
        Assert.Equal("inicio.php", change.Path);
        Assert.Equal("index.php", change.OriginalPath);
        Assert.True(change.Unstaged);

        Assert.True((await git.CommitAsync("renomeia", [change], isMerging: false)).Ok);
        Assert.Empty((await git.SnapshotAsync())!.Status.Changes);
        var log = await git.RunAsync(["log", "-1", "-M", "--name-status", "--format="]);
        Assert.StartsWith("R100", log.Output.Trim());
    }

    [Fact]
    public async Task Movido_para_outra_pasta_e_editado_tambem_junta()
    {
        using var temp = new TempFolder();
        temp.File("estilo.css", "body {}\nh1 {}\nh2 {}\np {}\n");
        var git = await Committed(temp);
        System.IO.File.Delete(Path.Combine(temp.Path, "estilo.css"));
        temp.File(@"css\estilo.css", "body {}\nh1 { color: red; }\nh2 {}\np {}\n");

        var change = Assert.Single((await git.SnapshotAsync())!.Status.Changes);
        Assert.Equal(("css/estilo.css", "estilo.css"), (change.Path, change.OriginalPath));
    }

    [Fact]
    public async Task Apagado_e_novo_sem_relacao_ficam_separados()
    {
        using var temp = new TempFolder();
        temp.File("a.txt", "um\ndois\n");
        var git = await Committed(temp);
        System.IO.File.Delete(Path.Combine(temp.Path, "a.txt"));
        temp.File("b.txt", "tres\nquatro\n");

        var kinds = (await git.SnapshotAsync())!.Status.Changes.Select(change => change.Kind).Order().ToList();
        Assert.Equal([GitChangeKind.Deleted, GitChangeKind.Untracked], kinds);
    }

    // Na janela

    private static async Task Until(Func<bool> condition, int seconds = 30)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<MainWindow> Open(TempFolder temp, TempFolder config)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.GitEnvironment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(config.Path, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.RefreshGitAsync();
        return window;
    }

    [AvaloniaFact]
    public async Task Painel_mostra_o_nome_antigo_e_a_margem_compara_com_ele()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("index.php", "<?php\n$a = 1;\n$b = 2;\n");
        await Committed(temp);
        var window = await Open(temp, config);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        var renamed = Path.Combine(temp.Path, "inicio.php");
        System.IO.File.Move(path, renamed);
        await Until(() => document.FullPath == renamed);
        await window.RefreshGitAsync();
        var row = Assert.Single(window.GitRows);
        Assert.Equal(("inicio.php", "R", "era index.php"), (row.Name, row.Letter, row.Folder));

        // A faixa da margem continua: compara com index.php do último commit.
        document.Document.Text = "<?php\n$a = 10;\n$b = 2;\n";
        await Until(() => window.ChangeBlockList.Count == 1);
        Assert.True(window.ShowChangeAt(2));
        Assert.Equal("$a = 1;", window.ChangePeekOldText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Descartar_o_renomeado_traz_o_nome_antigo_e_manda_o_novo_para_a_lixeira()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "x\n");
        await Committed(temp);
        var renamed = Path.Combine(temp.Path, "b.txt");
        System.IO.File.Move(path, renamed);
        var window = await Open(temp, config);
        var trashed = new List<string>();
        window.MoveToTrash = item =>
        {
            trashed.Add(item);
            System.IO.File.Delete(item);
        };

        var discarding = window.DiscardChangeAsync(Assert.Single(window.GitRows).Change);
        Assert.Contains("a.txt volta", window.DialogMessageText);
        window.AnswerDialog(primary: true);
        await discarding;

        Assert.Equal([renamed], trashed);
        Assert.Equal("x\n", System.IO.File.ReadAllText(path));
        Assert.Empty(window.GitRows);
        window.Close();
    }
}
