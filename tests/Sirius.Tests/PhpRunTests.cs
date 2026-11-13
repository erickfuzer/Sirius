using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class PhpRunTests
{
    // Detecção

    [Fact]
    public void Index_na_raiz_sobe_o_servidor_do_php()
    {
        using var temp = new TempFolder();
        temp.File("index.php", "<?php\n");
        Assert.Equal("php -S localhost:8000", RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Pasta_public_vira_a_raiz_do_site()
    {
        using var temp = new TempFolder();
        temp.File(Path.Combine("public", "index.php"), "<?php\n");
        temp.File("index.php", "<?php\n");
        Assert.Equal("php -S localhost:8000 -t public", RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Laravel_usa_o_artisan_mesmo_com_scripts_de_node()
    {
        using var temp = new TempFolder();
        temp.File("artisan", "#!/usr/bin/env php\n");
        temp.File(Path.Combine("public", "index.php"), "<?php\n");
        temp.File("package.json", "{\"scripts\":{\"dev\":\"vite\"}}");
        Assert.Equal("php artisan serve", RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Projeto_dotnet_com_php_de_exemplo_continua_dotnet()
    {
        using var temp = new TempFolder();
        temp.File("App.csproj", "<OutputType>Exe</OutputType>");
        temp.File("index.php", "<?php\n");
        Assert.StartsWith("dotnet run", RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Sem_index_sobe_o_servidor_para_o_arquivo_aberto()
    {
        using var temp = new TempFolder();
        var script = temp.File("aula.php", "<?php\n");
        Assert.Equal("php -S localhost:8000", RunDetector.Detect(temp.Path, script));
    }

    [Fact]
    public void Arquivo_php_fora_da_pasta_roda_no_terminal()
    {
        using var temp = new TempFolder();
        using var other = new TempFolder();
        var script = other.File("solto.php", "<?php\n");
        Assert.Equal($"php \"{script}\"", RunDetector.Detect(temp.Path, script));
    }

    [Fact]
    public void Projeto_node_nao_vira_php_por_causa_do_arquivo_aberto()
    {
        using var temp = new TempFolder();
        temp.File("package.json", "{\"scripts\":{\"dev\":\"vite\"}}");
        var script = temp.File("teste.php", "<?php\n");
        Assert.Equal("npm run dev", RunDetector.Detect(temp.Path, script));
    }

    // Página a abrir

    [Fact]
    public void Abre_o_arquivo_em_uso_quando_nao_ha_pagina_inicial()
    {
        using var temp = new TempFolder();
        var file = temp.File("aula.php", "<?php\n");
        Assert.Equal("http://localhost:8000/aula.php", RunDetector.SitePageUrl("php -S localhost:8000", temp.Path, file));
    }

    [Fact]
    public void Endereco_codifica_acentos_espacos_e_subpastas()
    {
        using var temp = new TempFolder();
        var file = temp.File(Path.Combine("aula 1", "exercício #2.php"), "<?php\n");
        Assert.Equal("http://localhost:8000/aula%201/exerc%C3%ADcio%20%232.php", RunDetector.SitePageUrl("php -S localhost:8000", temp.Path, file));
    }

    [Fact]
    public void Com_pagina_inicial_abre_sempre_o_site()
    {
        using var temp = new TempFolder();
        temp.File("index.php", "<?php\n");
        var file = temp.File(Path.Combine("src", "Catalogo.php"), "<?php\n");
        Assert.Equal("http://localhost:8000", RunDetector.SitePageUrl("php -S localhost:8000", temp.Path, file));
    }

    [Fact]
    public void Pasta_escolhida_com_t_e_artisan_abrem_o_site()
    {
        using var temp = new TempFolder();
        var file = temp.File("aula.php", "<?php\n");
        Assert.Equal("http://localhost:8000", RunDetector.SitePageUrl("php -S localhost:8000 -t public", temp.Path, file));
        Assert.Equal("http://127.0.0.1:8000", RunDetector.SitePageUrl("php artisan serve", temp.Path, file));
    }

    [Fact]
    public void Arquivo_fora_da_pasta_ou_que_nao_e_php_abre_o_site()
    {
        using var temp = new TempFolder();
        using var other = new TempFolder();
        var outside = other.File("solto.php", "<?php\n");
        var style = temp.File("estilo.css", "");
        Assert.Equal("http://localhost:8000", RunDetector.SitePageUrl("php -S localhost:8000", temp.Path, outside));
        Assert.Equal("http://localhost:8000", RunDetector.SitePageUrl("php -S localhost:8000", temp.Path, style));
        Assert.Equal("http://localhost:8000", RunDetector.SitePageUrl("php -S localhost:8000", temp.Path, null));
    }

    [Fact]
    public void Comando_que_nao_e_servidor_nao_tem_pagina()
    {
        using var temp = new TempFolder();
        var file = temp.File("aula.php", "<?php\n");
        Assert.Null(RunDetector.SitePageUrl("php aula.php", temp.Path, file));
    }

    [Fact]
    public void Pasta_sem_php_nao_vira_projeto_php()
    {
        using var temp = new TempFolder();
        temp.File(Path.Combine("public", "index.html"), "<html>");
        Assert.Null(RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Php_do_xampp_entra_nas_pastas_conhecidas() =>
        Assert.Contains(@"C:\xampp\php", ToolPaths.Folders);

    // Endereço do site

    [Theory]
    [InlineData("php -S localhost:8000", "http://localhost:8000")]
    [InlineData("php -S localhost:8000 -t public", "http://localhost:8000")]
    [InlineData("php -S 127.0.0.1:9090", "http://127.0.0.1:9090")]
    [InlineData("php -S 0.0.0.0:8080 -t web", "http://localhost:8080")]
    [InlineData("\"C:\\xampp\\php\\php.exe\" -S localhost:8001", "http://localhost:8001")]
    [InlineData("php artisan serve", "http://127.0.0.1:8000")]
    [InlineData("php artisan serve --port=9000", "http://127.0.0.1:9000")]
    public void Reconhece_o_endereco_do_servidor(string command, string expected) =>
        Assert.Equal(expected, RunDetector.SiteUrl(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("npm run dev")]
    [InlineData("php aula.php")]
    [InlineData("php -S")]
    [InlineData("php -S localhost")]
    [InlineData("php -S http://evil/&calc:80")]
    public void Outros_comandos_nao_tem_site(string? command) => Assert.Null(RunDetector.SiteUrl(command));

    // Na janela

    private const string FakeServer = "ping -n 60 127.0.0.1 >nul & rem php -S localhost:8123";

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Botao_de_abrir_site_acompanha_o_servidor()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var settings = new AppSettings();
        settings.RunCommands[temp.Path] = FakeServer;
        var opened = new List<string>();
        var window = new MainWindow(settings, Path.Combine(settingsFolder.Path, "settings.json"), temp.Path)
        {
            OpenUrl = url =>
            {
                opened.Add(url);
                return true;
            },
        };
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsOpenSiteVisible);

        await window.RunProjectAsync();
        await Until(() => window.IsOpenSiteVisible);
        Assert.Contains("Abrir site no navegador", window.PaletteCommandNames("site"));

        // Com a recarga ligada, de fábrica, o site abre pela Sirius, que repassa ao servidor do Executar.
        window.OpenSite();
        Assert.Equal([window.BrowserServer!.Origin + "/"], opened);
        Assert.Equal("http://localhost:8123", window.BrowserServer.Upstream);

        await window.ChangeSettingAsync("recarga:nao");
        opened.Clear();
        window.OpenSite();
        Assert.Equal(["http://localhost:8123"], opened);

        window.StopTask();
        await Until(() => !window.IsOpenSiteVisible);
        Assert.Null(window.RunningSiteUrl);

        window.OpenSite();
        Assert.Single(opened);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Abrir_site_acompanha_a_aba_em_uso()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var primeira = temp.File("aula1.php", "<?php\n");
        var segunda = temp.File("aula2.php", "<?php\n");
        var estilo = temp.File("estilo.css", "");
        var settings = new AppSettings();
        settings.RunCommands[temp.Path] = FakeServer;
        var window = new MainWindow(settings, Path.Combine(settingsFolder.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(primeira);

        await window.RunProjectAsync();
        await Until(() => window.IsOpenSiteVisible);
        Assert.Equal("http://localhost:8123/aula1.php", window.RunningSiteUrl);

        window.OpenFile(segunda);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("http://localhost:8123/aula2.php", window.RunningSiteUrl);

        // Numa aba de CSS, continua no arquivo de quando o servidor subiu.
        window.OpenFile(estilo);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("http://localhost:8123/aula1.php", window.RunningSiteUrl);

        window.StopTask();
        await Until(() => !window.IsOpenSiteVisible);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // Sem PHP no computador

    [Theory]
    [InlineData("php -S localhost:8000", true)]
    [InlineData("php artisan serve", true)]
    [InlineData("php \"C:\\a b\\aula.php\"", true)]
    [InlineData("PHP.EXE -v", true)]
    [InlineData("\"php\" -v", true)]
    [InlineData("php", true)]
    [InlineData("  php -v", true)]
    [InlineData("\"C:\\xampp\\php\\php.exe\" -S localhost:8000", false)]
    [InlineData("npm run dev", false)]
    [InlineData("phpunit", false)]
    [InlineData("composer run php", false)]
    public void Reconhece_comando_que_precisa_do_php(string command, bool expected) =>
        Assert.Equal(expected, RunDetector.NeedsPhp(command));

    [Fact]
    public void Procura_o_php_nas_duas_pastas_do_winget()
    {
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
        Assert.Contains(Path.Combine(winget, "Links"), ToolPaths.Folders);
    }

    [Fact]
    public void So_pula_o_visual_cpp_quando_ele_ja_existe()
    {
        Assert.Contains("--id PHP.PHP.8.4 --exact", ToolPaths.PhpInstallCommand(true));
        Assert.Contains("--skip-dependencies", ToolPaths.PhpInstallCommand(true));
        Assert.Contains("--id PHP.PHP.8.4 --exact", ToolPaths.PhpInstallCommand(false));
        Assert.DoesNotContain("--skip-dependencies", ToolPaths.PhpInstallCommand(false));
    }

    [Fact]
    public void Programa_que_nao_existe_nao_conta_como_php_funcionando() =>
        Assert.False(ToolPaths.PhpRuns(@"C:\pasta-que-nao-existe\php.exe"));

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Oferta_avisa_de_administrador_so_quando_vai_pedir(bool hasVisualCpp, bool warns)
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var window = OpenPhpProject(temp, settingsFolder, () => false);
        window.PhpInstallCommand = null;
        window.HasVisualCpp = hasVisualCpp;

        var running = window.RunProjectAsync();
        await Until(() => window.IsDialogOpen);

        Assert.DoesNotContain("XAMPP", window.PhpOfferText);
        Assert.Equal(warns, window.PhpOfferText.Contains("administrador"));
        Assert.Equal(hasVisualCpp, window.PhpOfferText.Contains("--skip-dependencies"));
        window.AnswerDialog(primary: false);
        await running;
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Php_instalado_que_nao_abre_explica_o_motivo()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var window = OpenPhpProject(temp, settingsFolder, () => false);

        var running = window.RunProjectAsync();
        await Until(() => window.IsDialogOpen);
        window.IsPhpInstalled = () => true;
        window.DoesPhpRun = () => false;
        window.AnswerDialog(primary: true);
        await running;

        await Until(() => window.StatusText.Contains("Visual C++"));
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static MainWindow OpenPhpProject(TempFolder temp, TempFolder settingsFolder, Func<bool> installed)
    {
        temp.File("index.php", "<?php\n");
        var window = new MainWindow(new AppSettings(), Path.Combine(settingsFolder.Path, "settings.json"), temp.Path)
        {
            IsPhpInstalled = installed,
            PhpInstallCommand = "echo instalando",
        };
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task Sem_php_executar_oferece_instalar_e_cancelar_nao_roda_nada()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var window = OpenPhpProject(temp, settingsFolder, () => false);

        var running = window.RunProjectAsync();
        await Until(() => window.IsDialogOpen);
        Assert.False(window.IsTaskRunning);

        window.AnswerDialog(primary: false);
        await running;

        Assert.False(window.IsDialogOpen);
        Assert.False(window.IsTaskRunning);
        Assert.False(window.IsTerminalOpen);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Aceitar_instala_e_avisa_para_clicar_em_executar()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var installed = false;
        var window = OpenPhpProject(temp, settingsFolder, () => installed);
        window.DoesPhpRun = () => true;

        var running = window.RunProjectAsync();
        await Until(() => window.IsDialogOpen);
        installed = true;
        window.AnswerDialog(primary: true);
        await running;

        await Until(() => window.StatusText.Contains("PHP instalado"));
        Assert.False(window.IsOpenSiteVisible);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Instalou_mas_nao_achou_o_php_pede_para_reabrir()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var window = OpenPhpProject(temp, settingsFolder, () => false);

        var running = window.RunProjectAsync();
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await running;

        await Until(() => window.StatusText.Contains("Reabra"));
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Instalacao_que_falha_nao_diz_que_deu_certo()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var window = OpenPhpProject(temp, settingsFolder, () => true);
        window.IsPhpInstalled = () => false;
        window.PhpInstallCommand = "exit /b 3";

        var running = window.RunProjectAsync();
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await running;

        await Until(() => window.StatusText.Contains("código 3"));
        Assert.DoesNotContain("instalado", window.StatusText, StringComparison.OrdinalIgnoreCase);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Com_php_executa_direto_sem_perguntar()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var settings = new AppSettings();
        settings.RunCommands[temp.Path] = FakeServer;
        var window = new MainWindow(settings, Path.Combine(settingsFolder.Path, "settings.json"), temp.Path)
        {
            IsPhpInstalled = () => throw new InvalidOperationException("Não devia consultar: este comando não começa com php."),
        };
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.RunProjectAsync();
        await Until(() => window.IsTaskRunning);

        Assert.False(window.IsDialogOpen);
        window.StopTask();
        await Until(() => !window.IsTaskRunning);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Comando_que_nao_e_servidor_nao_mostra_o_botao()
    {
        using var temp = new TempFolder();
        using var settingsFolder = new TempFolder();
        var settings = new AppSettings();
        settings.RunCommands[temp.Path] = "ping -n 60 127.0.0.1 >nul";
        var window = new MainWindow(settings, Path.Combine(settingsFolder.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.RunProjectAsync();
        await Until(() => window.IsTaskRunning);
        Assert.False(window.IsOpenSiteVisible);

        window.StopTask();
        await Until(() => !window.IsTaskRunning);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
