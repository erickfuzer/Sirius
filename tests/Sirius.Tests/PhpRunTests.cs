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
    public void Sem_index_executa_o_arquivo_php_aberto()
    {
        using var temp = new TempFolder();
        var script = temp.File("aula.php", "<?php\n");
        Assert.Equal($"php \"{script}\"", RunDetector.Detect(temp.Path, script));
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
