using Sirius.Core;

namespace Sirius.Tests;

public class LineDiffTests
{
    private static string[] L(params string[] lines) => lines;

    // Reconstroi os dois lados a partir da comparação: se bater, ela não perdeu nada.
    private static void AssertRebuilds(string[] a, string[] b)
    {
        var ops = LineDiff.Compare(a, b);
        var left = new List<string>();
        var right = new List<string>();
        var nextA = 0;
        var nextB = 0;
        foreach (var op in ops)
        {
            Assert.Equal(nextA, op.AStart);
            Assert.Equal(nextB, op.BStart);
            left.AddRange(a.Skip(op.AStart).Take(op.ACount));
            right.AddRange(b.Skip(op.BStart).Take(op.BCount));
            if (op.Kind == DiffKind.Equal)
                Assert.Equal(a.Skip(op.AStart).Take(op.ACount), b.Skip(op.BStart).Take(op.BCount));
            nextA += op.ACount;
            nextB += op.BCount;
        }
        Assert.Equal(a, left);
        Assert.Equal(b, right);
    }

    [Fact]
    public void Textos_iguais_nao_tem_mudancas()
    {
        var op = Assert.Single(LineDiff.Compare(L("a", "b"), L("a", "b")));
        Assert.Equal(DiffKind.Equal, op.Kind);
    }

    [Fact]
    public void Reconhece_insercao_remocao_e_troca()
    {
        Assert.Contains(LineDiff.Compare(L("a", "c"), L("a", "b", "c")), op => op is { Kind: DiffKind.Insert, BStart: 1, BCount: 1 });
        Assert.Contains(LineDiff.Compare(L("a", "b", "c"), L("a", "c")), op => op is { Kind: DiffKind.Delete, AStart: 1, ACount: 1 });
        Assert.Contains(LineDiff.Compare(L("a", "b", "c"), L("a", "x", "c")), op => op is { Kind: DiffKind.Replace, AStart: 1, BStart: 1 });
    }

    [Fact]
    public void Lados_vazios_funcionam()
    {
        AssertRebuilds([], L("a"));
        AssertRebuilds(L("a"), []);
        AssertRebuilds([], []);
    }

    [Fact]
    public void Mudancas_espalhadas_mantem_o_que_e_igual()
    {
        var ops = LineDiff.Compare(L("a", "b", "c", "d", "e", "f"), L("a", "x", "c", "d", "y", "f", "z"));
        Assert.Equal(4, ops.Where(op => op.Kind == DiffKind.Equal).Sum(op => op.ACount));
        AssertRebuilds(L("a", "b", "c", "d", "e", "f"), L("a", "x", "c", "d", "y", "f", "z"));
    }

    [Fact]
    public void Comparacoes_aleatorias_sempre_reconstroem_os_dois_lados()
    {
        var random = new Random(1234);
        for (var round = 0; round < 500; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 6).ToString()).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 6).ToString()).ToArray();
            AssertRebuilds(a, b);
        }
    }

    [Fact]
    public void Textos_enormes_e_totalmente_diferentes_nao_travam()
    {
        var a = Enumerable.Range(0, 200_000).Select(i => "a" + i).ToArray();
        var b = Enumerable.Range(0, 200_000).Select(i => "b" + i).ToArray();
        AssertRebuilds(a, b);
    }

    [Theory]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb\r\n", new[] { "a", "b", "" })]
    [InlineData("a\rb", new[] { "a", "b" })]
    [InlineData("", new[] { "" })]
    public void Separa_linhas_em_qualquer_formato(string text, string[] expected)
    {
        Assert.Equal(expected, LineDiff.SplitLines(text));
    }

    [Fact]
    public void Marcas_apontam_as_linhas_atuais()
    {
        var marks = LineDiff.LineMarks(LineDiff.Compare(L("a", "b", "c", "d"), L("a", "X", "c", "novo", "d")), 5);
        Assert.Equal(GitLineKind.Modified, marks[2]);
        Assert.Equal(GitLineKind.Added, marks[4]);
        Assert.Equal(2, marks.Count);
    }

    [Fact]
    public void Linha_apagada_marca_onde_ela_estava()
    {
        var marks = LineDiff.LineMarks(LineDiff.Compare(L("a", "b", "c"), L("a", "c")), 2);
        Assert.Equal(GitLineKind.DeletedAbove, Assert.Single(marks).Value);
        Assert.Equal(2, marks.Keys.Single());
    }

    [Fact]
    public void Alinhamento_preenche_o_lado_mais_curto()
    {
        var a = L("a", "b", "c");
        var b = L("a", "X", "Y", "c");
        var rows = LineDiff.Align(a, b, LineDiff.Compare(a, b));

        Assert.Equal(4, rows.Count);
        Assert.Equal(DiffRowKind.Same, rows[0].LeftKind);
        Assert.Equal((2, "b", 2, "X"), (rows[1].LeftNumber, rows[1].LeftText, rows[1].RightNumber, rows[1].RightText));
        Assert.Null(rows[2].LeftNumber);
        Assert.Equal(DiffRowKind.Filler, rows[2].LeftKind);
        Assert.Equal(DiffRowKind.Changed, rows[2].RightKind);
        Assert.Equal((3, 4), (rows[3].LeftNumber, rows[3].RightNumber));
    }
}

public class GitStatusParserTests
{
    [Fact]
    public void Le_o_branch_e_a_distancia_do_remoto()
    {
        var status = GitStatusParser.Parse("# branch.oid abc123\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +2 -3\0");
        Assert.Equal(("main", "abc123", "origin/main", 2, 3), (status.Branch, status.HeadOid, status.Upstream, status.Ahead, status.Behind));
    }

    [Fact]
    public void Repositorio_sem_commits_e_branch_solto()
    {
        Assert.Null(GitStatusParser.Parse("# branch.oid (initial)\0# branch.head main\0").HeadOid);
        Assert.Null(GitStatusParser.Parse("# branch.oid abc\0# branch.head (detached)\0").Branch);
    }

    [Theory]
    [InlineData(".M", GitChangeKind.Modified, false)]
    [InlineData("M.", GitChangeKind.Modified, false)]
    [InlineData("A.", GitChangeKind.Added, false)]
    [InlineData(".D", GitChangeKind.Deleted, true)]
    [InlineData("D.", GitChangeKind.Deleted, false)]
    public void Le_os_tipos_de_mudanca(string xy, GitChangeKind kind, bool deletedInWorktree)
    {
        var change = Assert.Single(GitStatusParser.Parse($"1 {xy} N... 100644 100644 100644 aaa bbb pasta/com espaço.txt\0").Changes);
        Assert.Equal("pasta/com espaço.txt", change.Path);
        Assert.Equal((kind, deletedInWorktree), (change.Kind, change.DeletedInWorktree));
    }

    [Fact]
    public void Le_renomeado_novo_e_conflito()
    {
        var changes = GitStatusParser.Parse(
            "2 R. N... 100644 100644 100644 aaa bbb R100 novo nome.txt\0velho nome.txt\0" +
            "u UU N... 100644 100644 100644 100644 a b c em conflito.txt\0" +
            "? ação.txt\0").Changes;

        Assert.Equal(3, changes.Count);
        Assert.Equal(("novo nome.txt", "velho nome.txt", GitChangeKind.Renamed), (changes[0].Path, changes[0].OriginalPath, changes[0].Kind));
        Assert.Equal(("em conflito.txt", GitChangeKind.Conflict), (changes[1].Path, changes[1].Kind));
        Assert.Equal(("ação.txt", GitChangeKind.Untracked), (changes[2].Path, changes[2].Kind));
    }
}

// Rodam o git de verdade em repositórios temporários.
public class GitClientTests
{
    public static async Task<GitClient> Repository(TempFolder temp)
    {
        var git = new GitClient(temp.Path);
        Assert.True((await git.InitAsync()).Ok);
        await git.RunAsync(["config", "user.name", "Teste"]);
        await git.RunAsync(["config", "user.email", "teste@example.com"]);
        await git.RunAsync(["config", "commit.gpgsign", "false"]);
        await git.RunAsync(["config", "core.autocrlf", "false"]);
        return git;
    }

    private static async Task CommitAll(GitClient git, string message = "base")
    {
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", message])).Ok);
    }

    private static async Task<GitStatus> Status(GitClient git) => (await git.SnapshotAsync())!.Status;

    [Fact]
    public async Task Pasta_sem_repositorio_devolve_nulo()
    {
        using var temp = new TempFolder();
        Assert.Null(await new GitClient(temp.Path).SnapshotAsync());
    }

    [Fact]
    public async Task Mostra_mudancas_e_o_conteudo_do_ultimo_commit()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        var path = temp.File("a.txt", "olá");
        await CommitAll(git);

        File.WriteAllText(path, "mudou");
        temp.File("novo com espaço.txt", "x");

        var changes = (await Status(git)).Changes;
        Assert.Contains(changes, c => c is { Path: "a.txt", Kind: GitChangeKind.Modified });
        Assert.Contains(changes, c => c is { Path: "novo com espaço.txt", Kind: GitChangeKind.Untracked });
        Assert.Equal("olá", TextFile.Decode((await git.ShowHeadAsync("a.txt"))!)!.Value.Text);
        Assert.Null(await git.ShowHeadAsync("novo com espaço.txt"));
    }

    [Fact]
    public async Task Commit_leva_so_os_arquivos_escolhidos()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        var a = temp.File("a.txt", "1");
        var b = temp.File("b.txt", "1");
        await CommitAll(git);
        File.WriteAllText(a, "2");
        File.WriteAllText(b, "2");

        var changes = (await Status(git)).Changes;
        var result = await git.CommitAsync("só o a", changes.Where(c => c.Path == "a.txt").ToList(), false);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("b.txt", Assert.Single((await Status(git)).Changes).Path);
    }

    [Fact]
    public async Task Commit_aceita_novo_apagado_preparado_e_renomeado()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        var deleted = temp.File("apagado.txt", "1");
        var stagedDeleted = temp.File("preparado.txt", "2");
        var old = temp.File("velho.txt", "conteudo igual para o git ver renomeado\n");
        await CommitAll(git);

        File.Delete(deleted);
        await git.RunAsync(["rm", "-q", "--", "preparado.txt"]);
        await git.RunAsync(["mv", "velho.txt", "novo nome.txt"]);
        temp.File("criado.txt", "x");

        var changes = (await Status(git)).Changes;
        Assert.Contains(changes, c => c.Kind == GitChangeKind.Renamed);
        var result = await git.CommitAsync("tudo", changes, false);

        Assert.True(result.Ok, result.Message);
        Assert.Empty((await Status(git)).Changes);
    }

    [Fact]
    public async Task Primeiro_commit_do_repositorio_funciona()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        temp.File("a.txt", "1");

        var result = await git.CommitAsync("início", (await Status(git)).Changes, false);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull((await Status(git)).HeadOid);
    }

    [Fact]
    public async Task Mensagem_com_acento_e_aspas_chega_inteira()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        temp.File("a.txt", "1");
        const string message = "Ação \"especial\" & outra; $linha";

        await git.CommitAsync(message, (await Status(git)).Changes, false);

        Assert.Equal(message, (await git.RunAsync(["log", "-1", "--format=%s"])).Output.Trim());
    }

    [Fact]
    public async Task Descartar_volta_ao_ultimo_commit()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        var modified = temp.File("m.txt", "original");
        var deleted = temp.File("d.txt", "original");
        var staged = temp.File("s.txt", "original");
        await CommitAll(git);
        File.WriteAllText(modified, "mudou");
        File.Delete(deleted);
        File.WriteAllText(staged, "mudou");
        await git.RunAsync(["add", "s.txt"]);

        foreach (var change in (await Status(git)).Changes)
            Assert.True((await git.DiscardAsync(change)).Ok);

        Assert.Equal("original", File.ReadAllText(modified));
        Assert.Equal("original", File.ReadAllText(deleted));
        Assert.Equal("original", File.ReadAllText(staged));
        Assert.Empty((await Status(git)).Changes);
    }

    [Fact]
    public async Task Descartar_novo_preparado_tira_do_git_e_mantem_o_arquivo()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        temp.File("base.txt", "1");
        await CommitAll(git);
        var added = temp.File("novo.txt", "x");
        await git.RunAsync(["add", "novo.txt"]);

        Assert.True((await git.DiscardAsync((await Status(git)).Changes.Single())).Ok);

        Assert.True(File.Exists(added));
        Assert.Equal(GitChangeKind.Untracked, (await Status(git)).Changes.Single().Kind);
    }

    [Fact]
    public async Task Branches_criam_trocam_e_validam_o_nome()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        temp.File("a.txt", "1");
        await CommitAll(git);

        Assert.True((await git.CreateBranchAsync("recurso")).Ok);
        Assert.Equal("recurso", (await Status(git)).Branch);
        Assert.Contains("main", await git.BranchesAsync());

        Assert.True((await git.SwitchAsync("main")).Ok);
        Assert.Equal("main", (await Status(git)).Branch);

        Assert.True(await git.IsValidBranchNameAsync("recurso/novo"));
        Assert.False(await git.IsValidBranchNameAsync("nome com espaço"));
        Assert.False(await git.IsValidBranchNameAsync("-perigoso"));
    }

    [Fact]
    public async Task Conta_commits_a_enviar_e_a_receber()
    {
        using var temp = new TempFolder();
        using var remote = new TempFolder();
        await new GitClient(remote.Path).RunAsync(["init", "--bare", "-b", "main"]);
        var git = await Repository(temp);
        temp.File("a.txt", "1");
        await CommitAll(git);
        Assert.True((await git.AddRemoteAsync(remote.Path)).Ok);
        Assert.True((await git.RunAsync(["push", "-u", "origin", "HEAD"])).Ok);

        var snapshot = await git.SnapshotAsync();
        Assert.Equal(["origin"], snapshot!.Remotes);
        Assert.Equal("origin/main", snapshot.Status.Upstream);

        temp.File("b.txt", "2");
        await CommitAll(git, "local");
        Assert.Equal(1, (await Status(git)).Ahead);
    }

    [Fact]
    public async Task Reconhece_merge_em_andamento_e_conflito()
    {
        using var temp = new TempFolder();
        var git = await Repository(temp);
        var path = temp.File("a.txt", "base");
        await CommitAll(git);
        await git.CreateBranchAsync("outro");
        File.WriteAllText(path, "do outro");
        await CommitAll(git, "outro");
        await git.SwitchAsync("main");
        File.WriteAllText(path, "da main");
        await CommitAll(git, "main");

        Assert.False((await git.RunAsync(["merge", "outro"])).Ok);

        var snapshot = await git.SnapshotAsync();
        Assert.True(snapshot!.IsMerging);
        Assert.Equal(GitChangeKind.Conflict, Assert.Single(snapshot.Status.Changes).Kind);

        File.WriteAllText(path, "resolvido");
        var result = await git.CommitAsync("merge", snapshot.Status.Changes, true);

        Assert.True(result.Ok, result.Message);
        Assert.False((await git.SnapshotAsync())!.IsMerging);
    }
}
