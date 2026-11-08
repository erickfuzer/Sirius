using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

// A janela de verdade trabalhando com repositórios Git reais.
public class GitWindowTests
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

    private static async Task<MainWindow> Open(TempFolder temp, TempFolder config, bool isolateIdentity = false)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        // Nenhum teste pode tocar na configuração global do Git da máquina.
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

    private static async Task<GitClient> CommittedRepository(TempFolder temp)
    {
        var git = await GitClientTests.Repository(temp);
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", "base", "--allow-empty"])).Ok);
        return git;
    }

    private static void Answer(MainWindow window, string text)
    {
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Arquivo_mudado_so_na_tela_entra_no_commit_sem_estar_na_lista()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "base");
        var git = await CommittedRepository(temp);
        var window = await Open(temp, config);
        Assert.Empty(window.GitRows);
        window.OpenFile(path);
        window.ActiveDocument!.Document.Text = "da tela";
        window.SetCommitMessage("só na tela");

        Assert.True(await window.CommitAsync());

        Assert.Equal("da tela", (await git.RunAsync(["show", "HEAD:a.txt"])).Output);
        Assert.Empty(window.GitRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_desmarcado_e_mudado_na_tela_continua_fora_do_commit()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var a = temp.File("a.txt", "a");
        var b = temp.File("b.txt", "b");
        var git = await CommittedRepository(temp);
        File.WriteAllText(a, "a2");
        File.WriteAllText(b, "b2");
        var window = await Open(temp, config);
        window.ToggleGitChange("b.txt");
        window.OpenFile(b);
        window.ActiveDocument!.Document.Text = "b da tela";
        window.SetCommitMessage("só o a");

        Assert.True(await window.CommitAsync());

        Assert.Equal("a2", (await git.RunAsync(["show", "HEAD:a.txt"])).Output);
        Assert.Equal("b", (await git.RunAsync(["show", "HEAD:b.txt"])).Output);
        Assert.Equal("b da tela", File.ReadAllText(b));
        window.Close();
    }

    // Como o repositório criado no site com README: um commit que o projeto não conhece.
    private static async Task SeedRemote(TempFolder bare, TempFolder seed, string readme = "# Do site\n")
    {
        Assert.True((await new GitClient(bare.Path).RunAsync(["init", "--bare", "-b", "main"])).Ok);
        Assert.True((await new GitClient(Path.GetDirectoryName(seed.Path)!).RunAsync(["clone", bare.Path, seed.Path])).Ok);
        var git = new GitClient(seed.Path);
        await git.RunAsync(["switch", "-c", "main"]);
        await git.RunAsync(["config", "user.name", "Site"]);
        await git.RunAsync(["config", "user.email", "site@example.com"]);
        File.WriteAllText(Path.Combine(seed.Path, "README.md"), readme);
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", "Initial commit"])).Ok);
        Assert.True((await git.RunAsync(["push", "origin", "main"])).Ok);
    }

    [AvaloniaFact]
    public async Task Remoto_criado_com_readme_recusa_explica_e_junta_ao_receber()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        using var bare = new TempFolder();
        using var seed = new TempFolder();
        await SeedRemote(bare, seed);
        var index = temp.File("index.php", "<?php\n");
        var git = await CommittedRepository(temp);
        Assert.True((await git.AddRemoteAsync(bare.Path)).Ok);
        var window = await Open(temp, config);

        await window.PushAsync();
        await Until(() => window.GitStatusMessage.Contains("Clique em Receber"), 60);

        await window.PullAsync();
        await Until(() => window.GitStatusMessage.Contains("Agora clique em Enviar"), 60);
        Assert.True(File.Exists(Path.Combine(temp.Path, "README.md")));
        Assert.True(File.Exists(index));

        await window.PushAsync();
        await Until(() => window.GitStatusMessage == "Enviado.", 60);
        Assert.Equal("origin/main", window.GitState!.Status.Upstream);
        Assert.Contains("index.php", (await new GitClient(bare.Path).RunAsync(["ls-tree", "--name-only", "main"])).Output);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Receber_em_branch_novo_nao_traz_o_main()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        using var bare = new TempFolder();
        using var seed = new TempFolder();
        await SeedRemote(bare, seed);
        var git = await CommittedRepository(temp);
        await git.AddRemoteAsync(bare.Path);
        await git.RunAsync(["switch", "-c", "ideia"]);
        var window = await Open(temp, config);

        await window.PullAsync();

        Assert.False(window.IsTaskRunning);
        Assert.Contains("Envie primeiro", window.GitStatusMessage);
        Assert.False(File.Exists(Path.Combine(temp.Path, "README.md")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nome_de_branch_com_simbolo_do_cmd_nao_vai_para_o_comando()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        using var bare = new TempFolder();
        using var seed = new TempFolder();
        await SeedRemote(bare, seed);
        var git = await CommittedRepository(temp);
        await git.AddRemoteAsync(bare.Path);
        Assert.True((await git.RunAsync(["switch", "-c", "a&calc"])).Ok);
        // O remoto também tem esse nome: só o formato pode barrar.
        Assert.True((await git.RunAsync(["push", "origin", "HEAD"])).Ok);
        var window = await Open(temp, config);

        await window.PullAsync();

        Assert.False(window.IsTaskRunning);
        Assert.Contains("Envie primeiro", window.GitStatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Envio_recusado_por_outro_motivo_nao_manda_receber()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        using var bare = new TempFolder();
        using var seed = new TempFolder();
        await SeedRemote(bare, seed);
        var git = await GitClientTests.Repository(temp);
        await git.AddRemoteAsync(bare.Path);
        Assert.True((await git.RunAsync(["fetch", "origin"])).Ok);
        Assert.True((await git.RunAsync(["reset", "--hard", "origin/main"])).Ok);
        temp.File("meu.txt", "x");
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", "meu"])).Ok);
        // Como quem pode ler, mas não pode enviar: o projeto já contém tudo o que há lá.
        File.WriteAllText(Path.Combine(bare.Path, "hooks", "pre-receive"), "#!/bin/sh\nexit 1\n");
        var window = await Open(temp, config);

        await window.PushAsync();
        await Until(() => window.GitStatusMessage.Contains("não conseguiu concluir"), 60);
        Assert.DoesNotContain("Receber", window.GitStatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enviar_sem_nenhum_commit_pede_um_antes_do_endereco()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await GitClientTests.Repository(temp);
        var window = await Open(temp, config);

        await window.PushAsync();

        Assert.False(window.IsDialogOpen);
        Assert.Equal("Faça um commit antes de enviar.", window.GitStatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sem_git_no_computador_oferece_instalar_em_vez_de_iniciar()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = await Open(temp, config);
        Assert.True(window.IsGitInitOffered);
        Assert.False(window.IsGitInstallOffered);

        var installed = false;
        window.IsGitInstalled = () => installed;
        window.GitInstallCommand = "exit 0";
        await window.RefreshGitAsync();

        Assert.Equal("O Git não está instalado", window.GitEmptyTitleText);
        Assert.False(window.IsGitInitOffered);
        Assert.True(window.IsGitInstallOffered);
        // O instalador do Git pede confirmação numa conta de administrador, como no notebook do dono: o texto avisa.
        Assert.Contains("administrador, o Windows pede uma confirmação", window.GitEmptyMessageText);
        Assert.DoesNotContain("sem pedir administrador", window.GitEmptyMessageText);

        var offer = window.OfferGitInstallAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsDialogOpen);
        Assert.Contains("exit 0", window.PhpOfferText);
        Assert.Contains("o Windows vai pedir uma confirmação", window.PhpOfferText);
        window.AnswerDialog(primary: true);
        await offer;

        installed = true;
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (window.StatusText != "Git instalado." || !window.IsGitInitOffered)
        {
            Assert.True(DateTime.UtcNow < limit, $"O painel não voltou: {window.StatusText}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(window.IsGitInstallOffered);
        window.Close();
    }

    [Fact]
    public void Git_instala_por_usuario_e_a_pasta_dele_entra_no_path()
    {
        Assert.Contains("Git.Git", ToolPaths.GitInstallCommand);
        Assert.Contains("--scope user", ToolPaths.GitInstallCommand);
        Assert.Contains(ToolPaths.Folders, folder => folder.EndsWith(Path.Combine("Programs", "Git", "cmd"), StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task Pasta_sem_git_oferece_iniciar_repositorio()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = await Open(temp, config);

        Assert.Null(window.GitState);

        await window.InitRepositoryAsync();

        Assert.NotNull(window.GitState);
        Assert.Equal("main", window.GitState!.Status.Branch);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mostra_as_mudancas_no_painel_e_na_arvore()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File(Path.Combine("src", "a.txt"), "1");
        await CommittedRepository(temp);
        File.WriteAllText(path, "2");
        var created = temp.File("novo.txt", "x");

        var window = await Open(temp, config);

        Assert.Equal(2, window.GitRows.Count);
        Assert.Equal("M", window.TreeMarkFor(path));
        Assert.Equal("N", window.TreeMarkFor(created));
        Assert.Contains(window.TreeRows, r => r is { Name: "src", GitLetter: "•" });
        Assert.Contains(window.TreeRows, r => r is { Name: "novo.txt", GitLetter: "N" });
        window.Close();
    }

    [AvaloniaFact]
    public async Task Atalho_abre_o_painel_e_esc_volta_para_a_arvore()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var window = await Open(temp, config);
        var width = window.CurrentSidebarWidth;

        window.KeyPressQwerty(PhysicalKey.G, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsGitOpen);
        Assert.True(window.CurrentSidebarWidth >= 300);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsGitOpen);
        Assert.Equal(width, window.CurrentSidebarWidth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Trocar_entre_busca_e_git_devolve_a_largura_original()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var window = await Open(temp, config);
        var width = window.CurrentSidebarWidth;

        window.OpenGit();
        window.OpenSearch();
        Assert.False(window.IsGitOpen);
        Assert.True(window.IsSearchOpen);

        window.OpenGit();
        Assert.False(window.IsSearchOpen);

        window.CloseGit();
        Assert.Equal(width, window.CurrentSidebarWidth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_sem_mensagem_avisa_e_nao_grava()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "1");
        await CommittedRepository(temp);
        File.WriteAllText(path, "2");
        var window = await Open(temp, config);

        Assert.False(await window.CommitAsync());

        Assert.Equal("Escreva uma mensagem para o commit.", window.GitStatusMessage);
        Assert.Single(window.GitRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_grava_o_que_esta_marcado_e_limpa_a_mensagem()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var a = temp.File("a.txt", "1");
        var b = temp.File("b.txt", "1");
        var git = await CommittedRepository(temp);
        File.WriteAllText(a, "2");
        File.WriteAllText(b, "2");
        var window = await Open(temp, config);

        window.ToggleGitChange("b.txt");
        Assert.False(window.GitRows.Single(r => r.Name == "b.txt").IsIncluded);
        window.SetCommitMessage("muda o a");

        Assert.True(await window.CommitAsync());

        Assert.Equal("", window.GitMessageText);
        Assert.Equal("b.txt", Assert.Single(window.GitRows).Name);
        Assert.Equal("muda o a", (await git.RunAsync(["log", "-1", "--format=%s"])).Output.Trim());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_salva_antes_o_que_esta_so_na_tela()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "1");
        var git = await CommittedRepository(temp);
        File.WriteAllText(path, "no disco");
        var window = await Open(temp, config);
        window.OpenFile(path);
        window.ActiveDocument!.Document.Text = "da tela";
        window.SetCommitMessage("commit");

        Assert.True(await window.CommitAsync());

        Assert.False(window.ActiveDocument!.IsDirty);
        Assert.Equal("da tela", (await git.RunAsync(["show", "HEAD:a.txt"])).Output);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pergunta_nova_cancela_a_anterior_em_vez_de_travar()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var a = temp.File("a.txt", "1");
        var b = temp.File("b.txt", "2");
        await CommittedRepository(temp);
        var window = await Open(temp, config);

        var first = window.DeleteAsync(a);
        Assert.True(window.IsDialogOpen);
        var second = window.DeleteAsync(b);

        await first;
        Assert.True(File.Exists(a));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await second;
        Assert.True(File.Exists(b));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sem_identidade_pergunta_nome_e_email_uma_vez()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var git = new GitClient(temp.Path);
        Assert.True((await git.InitAsync()).Ok);
        await git.RunAsync(["config", "commit.gpgsign", "false"]);
        temp.File("a.txt", "1");
        var window = await Open(temp, config);
        window.SetCommitMessage("primeiro");

        var committing = window.CommitAsync();
        await Until(() => window.IsDialogOpen);
        Answer(window, "Pessoa Teste");
        await Until(() => window.IsDialogOpen);
        Answer(window, "pessoa@example.com");

        Assert.True(await committing);
        var environment = window.GitEnvironment;
        var isolated = new GitClient(temp.Path, environment);
        Assert.Equal("Pessoa Teste", await isolated.ConfigAsync("user.name"));
        Assert.Contains("Pessoa Teste", (await isolated.RunAsync(["log", "-1", "--format=%an"])).Output);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Conflito_com_marcas_nao_deixa_fazer_commit()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "base");
        var git = await CommittedRepository(temp);
        await git.CreateBranchAsync("outro");
        File.WriteAllText(path, "do outro");
        await git.RunAsync(["commit", "-am", "outro"]);
        await git.SwitchAsync("main");
        File.WriteAllText(path, "da main");
        await git.RunAsync(["commit", "-am", "main"]);
        await git.RunAsync(["merge", "outro"]);
        var window = await Open(temp, config);

        Assert.True(window.GitState!.IsMerging);
        Assert.Equal("!", window.GitRows.Single().Letter);
        // O aviso do painel basta: a linha de estado não repete o mesmo texto.
        Assert.True(window.IsGitConflictNoticeVisible);
        Assert.Equal("", window.GitTaskMessageForTest(1, "Atualizado."));
        window.SetCommitMessage("merge");

        Assert.False(await window.CommitAsync());
        Assert.Contains("marcas de conflito", window.GitStatusMessage);

        File.WriteAllText(path, "resolvido");
        await window.RefreshGitAsync();
        Assert.True(await window.CommitAsync());
        Assert.False(window.GitState!.IsMerging);
        window.Close();
    }

    private static async Task<(MainWindow Window, string Path)> Conflicted(TempFolder temp, TempFolder config)
    {
        var path = temp.File("README.md", "base\n");
        var git = await CommittedRepository(temp);
        await git.CreateBranchAsync("outro");
        File.WriteAllText(path, "# test\n");
        await git.RunAsync(["commit", "-am", "outro"]);
        await git.SwitchAsync("main");
        File.WriteAllText(path, "Olá\nteste\n");
        await git.RunAsync(["commit", "-am", "main"]);
        await git.RunAsync(["merge", "outro"]);
        return (await Open(temp, config), path);
    }

    [AvaloniaFact]
    public async Task Conflito_mostra_os_dois_lados_com_os_botoes()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var (window, _) = await Conflicted(temp, config);

        await window.OpenDiffAsync(window.GitRows.Single().Change);

        Assert.True(window.IsConflictBarVisible);
        Assert.Equal("Olá\nteste\n", window.DiffLeftText);
        // Uma linha vazia de enchimento alinha com as duas do outro lado.
        Assert.Equal("# test", window.DiffRightText.TrimEnd());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ConflictChoice.Mine, "Olá\nteste\n")]
    [InlineData(ConflictChoice.Received, "# test\n")]
    [InlineData(ConflictChoice.Both, "Olá\nteste\n# test\n")]
    public async Task Escolher_um_lado_salva_e_deixa_fazer_o_commit(ConflictChoice choice, string expected)
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var (window, path) = await Conflicted(temp, config);
        await window.OpenDiffAsync(window.GitRows.Single().Change);

        await window.ResolveConflictAsync(choice);

        Assert.False(window.IsDiffOpen);
        Assert.Equal(expected, File.ReadAllText(path));
        Assert.Equal(path, window.ActiveDocument!.FullPath);
        Assert.Contains("faça o commit", window.GitStatusMessage);
        Assert.False(window.IsGitConflictNoticeVisible);
        window.SetCommitMessage("merge");
        Assert.True(await window.CommitAsync());
        Assert.False(window.GitState!.IsMerging);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Resolver_se_desfaz_com_um_ctrl_z()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var (window, _) = await Conflicted(temp, config);
        await window.OpenDiffAsync(window.GitRows.Single().Change);

        await window.ResolveConflictAsync(ConflictChoice.Received);
        window.ActiveDocument!.Document.UndoStack.Undo();

        Assert.Contains("<<<<<<< HEAD", window.ActiveDocument.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_comum_nao_mostra_os_botoes_de_conflito()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um");
        await CommittedRepository(temp);
        File.WriteAllText(path, "dois");
        var window = await Open(temp, config);

        await window.OpenDiffAsync(window.GitRows.Single().Change);

        Assert.True(window.IsDiffOpen);
        Assert.False(window.IsConflictBarVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Comparacao_mostra_o_antes_e_o_agora()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um\ndois\ntres");
        await CommittedRepository(temp);
        File.WriteAllText(path, "um\nDOIS\nmais\ntres");
        var window = await Open(temp, config);

        await window.OpenDiffAsync(window.GitRows.Single().Change);

        Assert.True(window.IsDiffOpen);
        Assert.Equal("um\ndois\n\ntres", window.DiffLeftText);
        Assert.Equal("um\nDOIS\nmais\ntres", window.DiffRightText);
        Assert.Equal([DiffRowKind.Same, DiffRowKind.Changed, DiffRowKind.Filler, DiffRowKind.Same], window.DiffLeftKinds);
        Assert.Equal([DiffRowKind.Same, DiffRowKind.Changed, DiffRowKind.Changed, DiffRowKind.Same], window.DiffRightKinds);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsDiffOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Comparacao_usa_o_texto_ainda_nao_salvo()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um");
        await CommittedRepository(temp);
        File.WriteAllText(path, "disco");
        var window = await Open(temp, config);
        window.OpenFile(path);
        window.ActiveDocument!.Document.Text = "na tela";

        await window.OpenDiffAsync(window.GitRows.Single().Change);

        Assert.Equal("na tela", window.DiffRightText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Comparacao_de_arquivo_novo_e_apagado()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var deleted = temp.File("apagado.txt", "antigo");
        await CommittedRepository(temp);
        File.Delete(deleted);
        temp.File("novo.txt", "recente");
        var window = await Open(temp, config);

        await window.OpenDiffAsync(window.GitRows.Single(r => r.Name == "novo.txt").Change);
        Assert.Equal("", window.DiffLeftText);
        Assert.Equal("recente", window.DiffRightText);

        await window.OpenDiffAsync(window.GitRows.Single(r => r.Name == "apagado.txt").Change);
        Assert.Equal("antigo", window.DiffLeftText);
        Assert.Equal("", window.DiffRightText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_binario_nao_abre_comparacao()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("foto.bin");
        File.WriteAllBytes(path, [1, 0, 2]);
        await CommittedRepository(temp);
        File.WriteAllBytes(path, [1, 0, 3]);
        var window = await Open(temp, config);

        await window.OpenDiffAsync(window.GitRows.Single().Change);

        Assert.False(window.IsDiffOpen);
        Assert.Contains("binário", window.GitStatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Abrir_um_arquivo_fecha_a_comparacao()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um");
        await CommittedRepository(temp);
        File.WriteAllText(path, "dois");
        var window = await Open(temp, config);
        await window.OpenDiffAsync(window.GitRows.Single().Change);

        window.OpenFile(path);

        Assert.False(window.IsDiffOpen);
        Assert.Equal("a.txt", window.ActiveDocument?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Margem_marca_as_linhas_alteradas_enquanto_digita_e_limpa_com_o_commit()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "um\ndois\ntres");
        await CommittedRepository(temp);
        var window = await Open(temp, config);
        window.OpenFile(path);
        await window.UpdateGitMarksAsync();
        Assert.Empty(window.GitLineMarks);

        var document = window.ActiveDocument!.Document;
        document.Insert(document.GetLineByNumber(2).Offset, "x");
        document.Insert(document.TextLength, "\nquatro");
        await Until(() => window.GitLineMarks.Count == 2);

        Assert.Equal(GitLineKind.Modified, window.GitLineMarks[2]);
        Assert.Equal(GitLineKind.Added, window.GitLineMarks[4]);

        await window.SaveAsync(window.ActiveDocument!);
        await window.RefreshGitAsync();
        window.SetCommitMessage("muda");
        Assert.True(await window.CommitAsync());
        await Until(() => window.GitLineMarks.Count == 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_novo_nao_pinta_a_margem_inteira()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var path = temp.File("novo.txt", "a\nb\nc");
        var window = await Open(temp, config);

        window.OpenFile(path);
        await window.UpdateGitMarksAsync();

        Assert.Empty(window.GitLineMarks);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Descartar_pergunta_e_cancelar_mantem_as_mudancas()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "original");
        await CommittedRepository(temp);
        File.WriteAllText(path, "mudou");
        var window = await Open(temp, config);

        var discarding = window.DiscardChangeAsync(window.GitRows.Single().Change);
        Assert.True(window.IsDialogOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await discarding;

        Assert.Equal("mudou", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Descartar_volta_o_arquivo_e_atualiza_a_aba_aberta()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "original");
        await CommittedRepository(temp);
        File.WriteAllText(path, "mudou");
        var window = await Open(temp, config);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        var discarding = window.DiscardChangeAsync(window.GitRows.Single().Change);
        window.AnswerDialog(primary: true);
        await discarding;

        Assert.Equal("original", File.ReadAllText(path));
        await Until(() => document.Document.Text == "original");
        Assert.Empty(window.GitRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Descartar_arquivo_novo_manda_para_a_lixeira()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var path = temp.File("novo.txt", "x");
        var window = await Open(temp, config);
        var trashed = new List<string>();
        window.MoveToTrash = item =>
        {
            trashed.Add(item);
            File.Delete(item);
        };

        var discarding = window.DiscardChangeAsync(window.GitRows.Single().Change);
        window.AnswerDialog(primary: true);
        await discarding;

        Assert.Equal([path], trashed);
        Assert.Empty(window.GitRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_feito_por_fora_atualiza_o_painel_sozinho()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "1");
        var git = await CommittedRepository(temp);
        File.WriteAllText(path, "2");
        var window = await Open(temp, config);
        Assert.Single(window.GitRows);

        await git.RunAsync(["commit", "-am", "por fora"]);

        await Until(() => window.GitRows.Count == 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Branches_trocam_pela_barra_de_comando_e_salvam_antes()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "1");
        var git = await CommittedRepository(temp);
        await git.RunAsync(["branch", "outro"]);
        var window = await Open(temp, config);
        window.OpenFile(path);
        window.ActiveDocument!.Document.Insert(0, "x");

        await window.OpenBranchPickerAsync();
        Assert.True(window.IsPaletteOpen);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("outro");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await Until(() => window.GitState?.Status.Branch == "outro");
        Assert.False(window.ActiveDocument!.IsDirty);
        Assert.Equal("x1", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Troca_de_branch_que_perderia_alteracoes_e_recusada()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var path = temp.File("a.txt", "base");
        var git = await CommittedRepository(temp);
        await git.CreateBranchAsync("outro");
        File.WriteAllText(path, "do outro");
        await git.RunAsync(["commit", "-am", "outro"]);
        await git.SwitchAsync("main");
        File.WriteAllText(path, "minha alteração");
        var window = await Open(temp, config);

        await window.SwitchBranchAsync("outro");

        Assert.Equal("main", window.GitState!.Status.Branch);
        Assert.Equal("minha alteração", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Criar_branch_valida_o_nome()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var window = await Open(temp, config);

        var invalid = window.CreateBranchAsync();
        Answer(window, "nome com espaço");
        await invalid;
        Assert.Equal("main", window.GitState!.Status.Branch);

        var valid = window.CreateBranchAsync();
        Answer(window, "recurso/novo");
        await valid;
        Assert.Equal("recurso/novo", window.GitState!.Status.Branch);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enviar_sem_remoto_pede_o_endereco_e_depois_recebe_novidades()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        using var bare = new TempFolder();
        using var other = new TempFolder();
        Assert.True((await new GitClient(bare.Path).RunAsync(["init", "--bare", "-b", "main"])).Ok);
        temp.File("a.txt", "1");
        await CommittedRepository(temp);
        var window = await Open(temp, config);

        var pushing = window.PushAsync();
        await Until(() => window.IsDialogOpen);
        Answer(window, bare.Path);
        await pushing;

        await Until(() => !window.IsTaskRunning, 60);
        await Until(() => window.GitState?.Status.Upstream == "origin/main", 60);
        Assert.Contains("origin", window.GitState!.Remotes);

        // Outra cópia publica uma novidade, e receber traz o arquivo para cá.
        Assert.True((await new GitClient(Path.GetDirectoryName(other.Path)!).RunAsync(["clone", bare.Path, other.Path])).Ok);
        var otherGit = new GitClient(other.Path);
        await otherGit.RunAsync(["config", "user.name", "Outra"]);
        await otherGit.RunAsync(["config", "user.email", "outra@example.com"]);
        File.WriteAllText(Path.Combine(other.Path, "novidade.txt"), "x");
        await otherGit.RunAsync(["add", "-A"]);
        await otherGit.RunAsync(["commit", "-m", "novidade"]);
        Assert.True((await otherGit.RunAsync(["push"])).Ok);

        await window.PullAsync();
        await Until(() => !window.IsTaskRunning, 60);
        await Until(() => File.Exists(Path.Combine(temp.Path, "novidade.txt")), 60);
        await Until(() => window.GitStatusMessage == "Atualizado.", 60);
        await Until(() => window.TreeRows.Any(r => r.Name == "novidade.txt"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Endereco_que_parece_opcao_e_recusado()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var window = await Open(temp, config);

        var pushing = window.PushAsync();
        await Until(() => window.IsDialogOpen);
        Answer(window, "--upload-pack=calc");

        Assert.True(window.IsDialogOpen);
        Assert.Empty(window.GitState!.Remotes);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await pushing;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Receber_sem_remoto_explica_o_que_fazer()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        await CommittedRepository(temp);
        var window = await Open(temp, config);

        await window.PullAsync();

        Assert.False(window.IsTaskRunning);
        Assert.Contains("Envie primeiro", window.GitStatusMessage);
        window.Close();
    }
}
