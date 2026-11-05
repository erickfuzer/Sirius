using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius.Tests;

public class GitHistoryParserTests
{
    [Fact]
    public void Le_varios_commits_com_mensagem_de_varias_linhas()
    {
        var output = "aaa1111111\0bbb2222222 ccc3333333\0Ana Lúcia\01700000000\0Título\0Título\n\nCorpo com\nduas linhas\n\0\n"
                   + "bbb2222222\0\0Beto\01600000000\0Primeiro\0Primeiro\n\0\n";

        var commits = GitHistory.ParseLog(output);

        Assert.Equal(2, commits.Count);
        Assert.Equal("aaa1111111", commits[0].Oid);
        Assert.Equal("aaa1111", commits[0].ShortOid);
        Assert.Equal("bbb2222222", commits[0].FirstParent);
        Assert.Equal("Ana Lúcia", commits[0].Author);
        Assert.Equal("Título", commits[0].Subject);
        Assert.Equal("Título\n\nCorpo com\nduas linhas", commits[0].Message);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), commits[0].Date);
        Assert.Equal("bbb2222222", commits[1].Oid);
        Assert.Null(commits[1].FirstParent);
    }

    [Fact]
    public void Saida_vazia_nao_tem_commits()
    {
        Assert.Empty(GitHistory.ParseLog(""));
        Assert.Empty(GitHistory.ParseChanges(""));
    }

    [Fact]
    public void Assunto_enorme_e_cortado()
    {
        var subject = new string('x', 5000);

        var commit = Assert.Single(GitHistory.ParseLog($"aaa\0\0Ana\01\0{subject}\0{subject}\0\n"));

        Assert.True(commit.Subject.Length < 400);
        Assert.True(commit.Message.Length < 2100);
    }

    [Fact]
    public void Le_cada_tipo_de_mudanca_e_nomes_estranhos()
    {
        var changes = GitHistory.ParseChanges("M\0src/a b.txt\0A\0ação.php\0D\0velho.txt\0R100\0antes.txt\0depois.txt\0T\0link\0C75\0x.txt\0y.txt\0");

        Assert.Equal(
        [
            new GitChange("src/a b.txt", null, GitChangeKind.Modified, false),
            new GitChange("ação.php", null, GitChangeKind.Added, false),
            new GitChange("velho.txt", null, GitChangeKind.Deleted, false),
            new GitChange("depois.txt", "antes.txt", GitChangeKind.Renamed, false),
            new GitChange("link", null, GitChangeKind.Modified, false),
            new GitChange("y.txt", null, GitChangeKind.Added, false),
        ], changes);
    }

    [Theory]
    [InlineData(-30, "agora")]
    [InlineData(30, "agora")]
    [InlineData(300, "há 5 min")]
    [InlineData(7200, "há 2 h")]
    public void Data_recente_em_palavras(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(expected, GitHistory.RelativeDate(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void Dias_contam_pelo_calendario_de_quem_esta_vendo()
    {
        var now = new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.FromHours(-3));

        // Só duas horas atrás, mas já foi ontem. O commit veio de outro fuso.
        Assert.Equal("ontem", GitHistory.RelativeDate(new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero), now));
        Assert.Equal("há 3 dias", GitHistory.RelativeDate(now.AddDays(-3), now));
        Assert.Equal("há 30 dias", GitHistory.RelativeDate(now.AddDays(-30), now));
        Assert.Equal("05/10/2026", GitHistory.RelativeDate(now.AddDays(-31), now));
    }

    [Fact]
    public void Commit_no_futuro_mostra_a_data()
    {
        var now = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal("07/11/2026", GitHistory.RelativeDate(now.AddDays(2), now));
    }
}

// Rodam o git de verdade em repositórios temporários.
public class GitHistoryClientTests
{
    private static async Task Commit(GitClient git, string message)
    {
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", message])).Ok);
    }

    [Fact]
    public async Task Repositorio_sem_commits_nao_tem_historico()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);

        Assert.Null(await git.LogAsync(0, 10));
    }

    [Fact]
    public async Task Vem_do_mais_novo_ao_mais_antigo_e_pagina()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        foreach (var name in new[] { "um", "dois", "três" })
        {
            temp.File(name + ".txt", name);
            await Commit(git, "Commit " + name);
        }

        var all = await git.LogAsync(0, 10);
        var page = await git.LogAsync(1, 1);

        Assert.Equal(["Commit três", "Commit dois", "Commit um"], all!.Select(c => c.Subject));
        Assert.Equal("Teste", all![0].Author);
        Assert.Equal(all[1].Oid, all[0].FirstParent);
        Assert.Null(all[2].FirstParent);
        Assert.Equal("Commit dois", Assert.Single(page!).Subject);
    }

    [Fact]
    public async Task Configuracao_do_repositorio_nao_muda_a_leitura()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("a.txt", "a");
        await Commit(git, "Ação com acento");
        await git.RunAsync(["config", "log.showSignature", "true"]);
        await git.RunAsync(["config", "format.pretty", "oneline"]);
        await git.RunAsync(["config", "i18n.logOutputEncoding", "ISO-8859-1"]);
        await git.RunAsync(["config", "color.ui", "always"]);

        var commit = Assert.Single((await git.LogAsync(0, 10))!);

        Assert.Equal("Ação com acento", commit.Subject);
    }

    [Fact]
    public async Task Primeiro_commit_mostra_tudo_como_novo()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File(Path.Combine("pasta com espaço", "ação.txt"), "x");
        await Commit(git, "base");
        var commit = Assert.Single((await git.LogAsync(0, 1))!);

        var change = Assert.Single((await git.CommitChangesAsync(commit))!);

        Assert.Equal(new GitChange("pasta com espaço/ação.txt", null, GitChangeKind.Added, false), change);
    }

    [Fact]
    public async Task Mostra_modificado_novo_apagado_e_renomeado()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var changed = temp.File("muda.txt", "1\n");
        var removed = temp.File("sai.txt", "tchau\n");
        var renamed = temp.File("antes.txt", string.Concat(Enumerable.Repeat("conteúdo igual\n", 20)));
        await Commit(git, "base");

        File.WriteAllText(changed, "2\n");
        File.Delete(removed);
        File.Move(renamed, temp.File("depois.txt"));
        temp.File("novo.txt", "oi\n");
        await Commit(git, "tudo");
        var commit = (await git.LogAsync(0, 1))![0];

        var changes = (await git.CommitChangesAsync(commit))!.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();

        Assert.Equal(
        [
            new GitChange("depois.txt", "antes.txt", GitChangeKind.Renamed, false),
            new GitChange("muda.txt", null, GitChangeKind.Modified, false),
            new GitChange("novo.txt", null, GitChangeKind.Added, false),
            new GitChange("sai.txt", null, GitChangeKind.Deleted, false),
        ], changes);
    }

    [Fact]
    public async Task Merge_compara_com_o_branch_onde_entrou()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("base.txt", "b");
        await Commit(git, "base");
        await git.CreateBranchAsync("lado");
        temp.File("lado.txt", "l");
        await Commit(git, "no lado");
        await git.SwitchAsync("main");
        temp.File("main.txt", "m");
        await Commit(git, "na main");
        Assert.True((await git.RunAsync(["merge", "--no-edit", "--no-ff", "lado"])).Ok);
        var merge = (await git.LogAsync(0, 1))![0];

        var change = Assert.Single((await git.CommitChangesAsync(merge))!);

        Assert.Equal("lado.txt", change.Path);
    }

    [Fact]
    public async Task Le_o_arquivo_como_estava_naquele_commit()
    {
        using var temp = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var path = temp.File("a.txt", "velho");
        await Commit(git, "um");
        File.WriteAllText(path, "novo");
        await Commit(git, "dois");
        var commits = (await git.LogAsync(0, 2))!;

        Assert.Equal("velho", System.Text.Encoding.UTF8.GetString((await git.ShowAtAsync(commits[1].Oid, "a.txt"))!));
        Assert.Equal("novo", System.Text.Encoding.UTF8.GetString((await git.ShowAtAsync(commits[0].Oid, "a.txt"))!));
        Assert.Null(await git.ShowAtAsync(commits[0].Oid, "nao-existe.txt"));
    }
}

// A janela de verdade lendo o histórico de repositórios Git reais.
public class GitHistoryWindowTests
{
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

    private static async Task Commit(GitClient git, string message)
    {
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", message])).Ok);
    }

    private static async Task<List<HistoryCommitRow>> ShowHistory(MainWindow window)
    {
        window.OpenGitHistory();
        await Until(() => window.HistoryRows.OfType<HistoryCommitRow>().Any());
        return window.HistoryRows.OfType<HistoryCommitRow>().ToList();
    }

    private static async Task<List<HistoryFileRow>> Expand(MainWindow window, HistoryCommitRow row)
    {
        await window.ToggleCommitAsync(row.Commit);
        Dispatcher.UIThread.RunJobs();
        return window.HistoryRows.OfType<HistoryFileRow>().Where(file => file.Commit.Oid == row.Commit.Oid).ToList();
    }

    [AvaloniaFact]
    public async Task Aba_historico_mostra_os_commits_e_esconde_o_formulario()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("a.txt", "1");
        await Commit(git, "Primeiro");
        temp.File("b.txt", "2");
        await Commit(git, "Segundo");
        var window = await Open(temp, config);

        var rows = await ShowHistory(window);

        Assert.True(window.IsHistoryShown);
        Assert.False(window.IsCommitFormVisible);
        Assert.Equal(["Segundo", "Primeiro"], rows.Select(r => r.Commit.Subject));
        Assert.Contains("Teste", rows[0].Detail);
        Assert.Contains(rows[0].Commit.ShortOid, rows[0].Detail);
        Assert.Contains("agora", rows[0].Detail);

        window.ShowGitTab(history: false);

        Assert.False(window.IsHistoryShown);
        Assert.True(window.IsCommitFormVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicar_no_commit_abre_e_fecha_os_arquivos()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File(Path.Combine("src", "a.txt"), "1");
        await Commit(git, "base");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];

        var files = await Expand(window, row);

        var file = Assert.Single(files);
        Assert.Equal("a.txt", file.Name);
        Assert.Equal("src", file.Folder);
        Assert.Equal("N", file.Letter);

        Assert.Empty(await Expand(window, row));
        Assert.Single(window.HistoryRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compara_o_arquivo_com_a_versao_anterior_do_commit()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var path = temp.File("a.txt", "velho\n");
        await Commit(git, "um");
        File.WriteAllText(path, "meio\n");
        await Commit(git, "dois");
        // O que está no disco agora não pode entrar na comparação de um commit antigo.
        File.WriteAllText(path, "agora\n");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];
        var file = Assert.Single(await Expand(window, row));

        await window.OpenCommitDiffAsync(file.Commit, file.Change);

        Assert.True(window.IsDiffOpen);
        Assert.Contains("velho", window.DiffLeftText);
        Assert.Contains("meio", window.DiffRightText);
        Assert.DoesNotContain("agora", window.DiffRightText);
        Assert.Equal("Antes", window.DiffLeftLabelText);
        Assert.Contains(row.Commit.ShortOid, window.DiffRightLabelText);
        Assert.True(window.IsDiffOpenButtonVisible);
        var selected = Assert.Single(window.HistoryRows.OfType<HistoryFileRow>());
        Assert.Equal(Palette.Current.Tint, selected.Background);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Depois_da_comparacao_de_commit_a_de_mudancas_volta_aos_seus_titulos()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var path = temp.File("a.txt", "1\n");
        await Commit(git, "um");
        File.WriteAllText(path, "2\n");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];
        var file = Assert.Single(await Expand(window, row));
        await window.OpenCommitDiffAsync(file.Commit, file.Change);

        await window.OpenDiffAsync(window.GitRows[0].Change);

        Assert.Equal("Último commit", window.DiffLeftLabelText);
        Assert.Equal("Agora", window.DiffRightLabelText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_apagado_compara_sem_oferecer_abrir()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var path = temp.File("a.txt", "conteúdo\n");
        await Commit(git, "um");
        File.Delete(path);
        await Commit(git, "apaga");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];
        var file = Assert.Single(await Expand(window, row));
        Assert.Equal("E", file.Letter);

        await window.OpenCommitDiffAsync(file.Commit, file.Change);

        Assert.Contains("conteúdo", window.DiffLeftText);
        Assert.Equal("", window.DiffRightText.Trim());
        Assert.False(window.IsDiffOpenButtonVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_renomeado_le_o_nome_antigo_no_commit_anterior()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var path = temp.File("antes.txt", string.Concat(Enumerable.Repeat("mesma linha\n", 20)));
        await Commit(git, "um");
        File.Move(path, temp.File("depois.txt"));
        await Commit(git, "renomeia");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];
        var file = Assert.Single(await Expand(window, row));
        Assert.Equal("R", file.Letter);

        await window.OpenCommitDiffAsync(file.Commit, file.Change);

        Assert.Contains("mesma linha", window.DiffLeftText);
        Assert.Contains("mesma linha", window.DiffRightText);
        Assert.Equal("antes.txt → depois.txt", window.DiffTitleText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Binario_avisa_em_vez_de_comparar()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        File.WriteAllBytes(temp.File("foto.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 1, 2, 3]);
        await Commit(git, "foto");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];
        var file = Assert.Single(await Expand(window, row));

        await window.OpenCommitDiffAsync(file.Commit, file.Change);

        Assert.False(window.IsDiffOpen);
        Assert.Contains("binário", window.GitStatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_novo_aparece_e_quem_estava_aberto_continua()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("a.txt", "1");
        await Commit(git, "base");
        var window = await Open(temp, config);
        var row = (await ShowHistory(window))[0];
        await Expand(window, row);

        temp.File("b.txt", "2");
        await window.RefreshGitAsync();
        window.SetCommitMessage("Novo pela IDE");
        Assert.True(await window.CommitAsync());
        await Until(() => window.HistoryRows.OfType<HistoryCommitRow>().Count() == 2);

        Assert.Equal("Novo pela IDE", window.HistoryRows.OfType<HistoryCommitRow>().First().Commit.Subject);
        Assert.Single(window.HistoryRows.OfType<HistoryFileRow>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Abrir_a_aba_depois_de_um_commit_recarrega_a_lista()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("a.txt", "1");
        await Commit(git, "base");
        var window = await Open(temp, config);
        await ShowHistory(window);
        window.ShowGitTab(history: false);

        temp.File("b.txt", "2");
        await Commit(git, "Feito no terminal");
        await window.RefreshGitAsync();
        Assert.Single(window.HistoryRows);

        window.ShowGitTab(history: true);
        await Until(() => window.HistoryRows.Count == 2);

        Assert.Equal("Feito no terminal", ((HistoryCommitRow)window.HistoryRows[0]).Commit.Subject);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Muitos_commits_carregam_aos_poucos()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        // O fast-import cria 250 commits de uma vez: um por um demoraria segundos preciosos.
        var stream = new System.Text.StringBuilder();
        for (var i = 1; i <= 250; i++)
        {
            var message = $"Commit {i}";
            stream.Append($"commit refs/heads/main\ncommitter Teste <teste@example.com> {1700000000 + i} +0000\ndata {message.Length}\n{message}\n\n");
        }
        Assert.True((await git.RunAsync(["fast-import", "--quiet"], stream.ToString())).Ok);
        var window = await Open(temp, config);

        var rows = await ShowHistory(window);

        Assert.Equal(100, rows.Count);
        Assert.Equal("Commit 250", rows[0].Commit.Subject);
        Assert.IsType<HistoryMoreRow>(window.HistoryRows[^1]);

        await window.LoadMoreHistoryAsync();
        Assert.Equal(201, window.HistoryRows.Count);
        await window.LoadMoreHistoryAsync();

        var all = window.HistoryRows.OfType<HistoryCommitRow>().ToList();
        Assert.Equal(250, window.HistoryRows.Count);
        Assert.Equal(250, all.Select(r => r.Commit.Oid).Distinct().Count());
        Assert.Equal("Commit 1", all[^1].Commit.Subject);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Repositorio_sem_commits_avisa_em_vez_de_falhar()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await GitClientTests.Repository(temp);
        var window = await Open(temp, config);

        window.OpenGitHistory();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsHistoryShown);
        Assert.Empty(window.HistoryRows);
        Assert.Equal("", window.GitStatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Trocar_de_pasta_limpa_o_historico()
    {
        using var temp = new TempFolder();
        using var other = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("a.txt", "1");
        await Commit(git, "base");
        var window = await Open(temp, config);
        await ShowHistory(window);

        window.OpenFolder(other.Path);
        await window.RefreshGitAsync();

        Assert.False(window.IsHistoryShown);
        Assert.Empty(window.HistoryRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Abrir_o_git_comeca_nas_mudancas_e_conta_os_arquivos()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        var path = temp.File("a.txt", "1");
        await Commit(git, "base");
        File.WriteAllText(path, "2");
        var window = await Open(temp, config);
        await ShowHistory(window);
        window.CloseGit();

        window.OpenGit();

        Assert.False(window.IsHistoryShown);
        Assert.True(window.IsCommitFormVisible);
        Assert.Equal("Mudanças 1", window.GitChangesTabLabel);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Barra_de_comando_abre_o_historico_e_trocar_o_tema_mantem_a_lista()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = await GitClientTests.Repository(temp);
        temp.File("a.txt", "1");
        await Commit(git, "base");
        var window = await Open(temp, config);

        Assert.Contains("Histórico de commits", window.PaletteCommandNames("histórico"));

        var row = (await ShowHistory(window))[0];
        var before = Assert.Single(await Expand(window, row));
        window.ToggleTheme();
        Dispatcher.UIThread.RunJobs();

        var after = Assert.Single(window.HistoryRows.OfType<HistoryFileRow>());
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(2, window.HistoryRows.Count);
        window.Close();
    }
}
