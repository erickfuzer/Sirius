using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

// O bloco do Xdebug no php.ini, para páginas de outro servidor, como o Apache.
public class PhpIniXdebugTests
{
    [Theory]
    [InlineData("Configuration File (php.ini) Path: \nLoaded Configuration File:         C:\\xampp\\php\\php.ini\nScan for additional .ini files in: (none)\n", "C:\\xampp\\php\\php.ini")]
    [InlineData("Loaded Configuration File:         (none)\n", null)]
    [InlineData("qualquer coisa", null)]
    public void Le_o_php_ini_em_uso(string output, string? expected) =>
        Assert.Equal(expected, PhpIniXdebug.ParseLoadedIni(output));

    [Theory]
    [InlineData("[PHP]\nmemory_limit=128M\n")]
    [InlineData("[PHP]\r\nmemory_limit=128M\r\n")]
    [InlineData("[PHP]\nsem quebra no fim")]
    public void Ligar_e_desligar_volta_ao_original(string ini)
    {
        var on = PhpIniXdebug.Add(ini, @"C:\Sirius\php_xdebug.dll");
        Assert.True(PhpIniXdebug.HasBlock(on));
        Assert.Contains("zend_extension=\"C:/Sirius/php_xdebug.dll\"", on);
        Assert.Contains($"xdebug.client_port={PhpIniXdebug.Port}", on);
        var expected = ini.EndsWith('\n') ? ini : ini + "\n";
        Assert.Equal(expected, PhpIniXdebug.Remove(on));
    }

    [Fact]
    public void Acha_o_arquivo_do_xdebug_no_bloco()
    {
        Assert.Equal("C:/Sirius/x.dll", PhpIniXdebug.ExtensionIn(PhpIniXdebug.Add("[PHP]\n", @"C:\Sirius\x.dll")));
        Assert.Null(PhpIniXdebug.ExtensionIn(PhpIniXdebug.Add("[PHP]\n", null)));
        // Fora do bloco da Sirius não é dela.
        Assert.Null(PhpIniXdebug.ExtensionIn("zend_extension=\"C:/outro.dll\"\n"));
    }

    [Fact]
    public void Quebra_de_linha_segue_o_arquivo()
    {
        var on = PhpIniXdebug.Add("[PHP]\r\na=1\r\n", null);
        Assert.DoesNotContain("\n", on.Replace("\r\n", ""));
        Assert.DoesNotContain("zend_extension", on);
    }

    [Fact]
    public void Ligar_de_novo_troca_o_bloco()
    {
        var once = PhpIniXdebug.Add("[PHP]\n", "C:/a.dll");
        var twice = PhpIniXdebug.Add(once, "C:/b.dll");
        Assert.DoesNotContain("a.dll", twice);
        Assert.Single(twice.Split("Sirius: depuracao (nao edite").Skip(1));
    }

    [Fact]
    public void Gravar_guarda_a_copia_so_da_primeira_vez_e_preserva_os_bytes()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "php.ini");
        // Um comentário em Latin-1, como em php.ini antigos: o "é" é um byte só.
        var original = Encoding.Latin1.GetBytes("; café\n[PHP]\n");
        File.WriteAllBytes(path, original);

        PhpIniXdebug.Write(path, PhpIniXdebug.Add(PhpIniXdebug.Read(path), null));
        PhpIniXdebug.Write(path, PhpIniXdebug.Add(PhpIniXdebug.Read(path), "C:/x.dll"));

        Assert.Equal(original, File.ReadAllBytes(path + ".antes-da-sirius"));
        PhpIniXdebug.Write(path, PhpIniXdebug.Remove(PhpIniXdebug.Read(path)));
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}

public class ServerDebugWindowTests
{
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static MainWindow Open(TempFolder temp, TempFolder config, string ini, AppSettings? settings = null, int? port = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.OpenUrl = _ => true;
        window.FindPhpIni = () => Task.FromResult<string?>(ini);
        window.ServerXdebugFile = () => Task.FromResult<string?>(@"C:\Sirius\php_xdebug.dll");
        window.ServerDebugPort = port ?? FreePort();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task Ligar_grava_o_php_ini_ouve_e_para_na_pagina()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("index.php", "<?php\n$a = 1;\n");
        var ini = temp.File("php.ini", "[PHP]\n");
        var window = Open(temp, config, ini);

        var starting = window.StartServerDebugAsync();
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await starting;

        Assert.True(PhpIniXdebug.HasBlock(File.ReadAllText(ini)));
        Assert.Equal("[PHP]\n", File.ReadAllText(ini + ".antes-da-sirius"));
        Assert.True(window.IsListeningToServer);
        Assert.True(window.IsListeningIndicatorVisible);
        Assert.Contains("Reinicie o Apache", window.StatusText);

        // Uma página do servidor conecta na porta do Xdebug, e a Sirius para nela como no Executar.
        using var page = new FakeXdebug(new FakeXdebug.Stop(php, 2, FakeXdebug.Int("$a", 1)));
        page.Connect(window.ServerDebugPort);
        await Until(() => window.IsDebugPaused);
        Assert.False(window.IsListeningIndicatorVisible);
        Assert.Equal(2, window.PausedLine);
        window.DebugContinue();
        await page.Finished;

        await window.ToggleServerDebugAsync();
        Assert.False(window.IsListeningToServer);
        Assert.False(window.IsListeningIndicatorVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Com_o_bloco_ja_no_php_ini_so_ouve()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var ini = temp.File("php.ini", PhpIniXdebug.Add("[PHP]\n", null));
        var window = Open(temp, config, ini);

        await window.StartServerDebugAsync();

        Assert.False(window.IsDialogOpen);
        Assert.True(window.IsListeningToServer);
        Assert.Contains("Ouvindo o servidor", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelar_nao_mexe_no_php_ini()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var ini = temp.File("php.ini", "[PHP]\n");
        var window = Open(temp, config, ini);

        var starting = window.StartServerDebugAsync();
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: false);
        await starting;

        Assert.Equal("[PHP]\n", File.ReadAllText(ini));
        Assert.False(File.Exists(ini + ".antes-da-sirius"));
        Assert.False(window.IsListeningToServer);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Desligar_tira_so_o_bloco()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var ini = temp.File("php.ini", "[PHP]\nmemory_limit=128M\n");
        PhpIniXdebug.Write(ini, PhpIniXdebug.Add(File.ReadAllText(ini), "C:/x.dll"));
        var window = Open(temp, config, ini);

        await window.DisableServerXdebugAsync();

        Assert.Equal("[PHP]\nmemory_limit=128M\n", File.ReadAllText(ini));
        Assert.Contains("Reinicie o Apache", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Porta_ocupada_explica()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var ini = temp.File("php.ini", PhpIniXdebug.Add("[PHP]\n", null));
        var window = Open(temp, config, ini);
        var other = new TcpListener(IPAddress.Loopback, window.ServerDebugPort);
        other.Start();
        try
        {
            await window.StartServerDebugAsync();
            Assert.False(window.IsListeningToServer);
            Assert.Contains("Outro programa já ouve a porta", window.StatusText);
        }
        finally
        {
            other.Stop();
            window.Close();
        }
    }

    // O dono ligou a escuta, clicou em Executar, e a página do Apache passou direto: o Executar desligava a escuta.
    [AvaloniaFact]
    public async Task Executar_usa_a_mesma_escuta_e_nao_desliga()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("index.php", "<?php\n$a = 1;\n");
        var ini = temp.File("php.ini", PhpIniXdebug.Add("[PHP]\n", null));
        var settings = new AppSettings();
        settings.RunCommands[temp.Path] = "php -v";
        var window = Open(temp, config, ini, settings);
        window.IsPhpInstalled = () => true;
        window.OpenFile(php);
        window.ToggleBreakpoint(2);
        window.PrepareXdebug = () => Task.FromResult(new DebugPreparation(false, config.Path));
        await window.StartServerDebugAsync();
        Assert.True(window.IsListeningToServer);

        await window.RunProjectAsync();
        Assert.Contains($"client_port={window.ServerDebugPort}", window.LastDebugCommand);
        await Until(() => !window.IsTaskRunning);

        // A tarefa acabou, e a escuta continua: uma página do servidor ainda para.
        Assert.True(window.IsListeningToServer);
        using var page = new FakeXdebug(new FakeXdebug.Stop(php, 2));
        page.Connect(window.ServerDebugPort);
        await Until(() => window.IsDebugPaused);
        window.DebugContinue();
        await page.Finished;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escuta_ligada_volta_ao_abrir_e_desligada_nao()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var ini = temp.File("php.ini", PhpIniXdebug.Add("[PHP]\n", null));
        var settings = new AppSettings();
        var port = ServerDebugWindowTests.FreePort();
        var window = Open(temp, config, ini, settings, port);
        await window.StartServerDebugAsync();
        Assert.True(settings.ListenToServer);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var reopened = Open(temp, config, ini, settings, port);
        await Until(() => reopened.IsListeningToServer);
        Assert.Contains("Ouvindo o servidor", reopened.StatusText);

        await reopened.ToggleServerDebugAsync();
        Assert.False(settings.ListenToServer);
        reopened.Close();
        Dispatcher.UIThread.RunJobs();

        var third = Open(temp, config, ini, settings, port);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        Assert.False(third.IsListeningToServer);
        third.Close();
    }

    [AvaloniaFact]
    public async Task Bloco_sem_o_arquivo_do_xdebug_baixa_de_novo()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        // Como hoje no computador do dono: o php.ini aponta para um Xdebug que sumiu.
        var ini = temp.File("php.ini", PhpIniXdebug.Add("[PHP]\n", Path.Combine(temp.Path, "sumiu.dll")));
        var novo = temp.File("novo.dll", "x");
        var window = Open(temp, config, ini);
        window.ServerXdebugFile = () => Task.FromResult<string?>(novo);

        await window.StartServerDebugAsync();

        Assert.Equal(novo.Replace('\\', '/'), PhpIniXdebug.ExtensionIn(File.ReadAllText(ini)));
        Assert.True(window.IsListeningToServer);
        Assert.Contains("Reinicie o Apache", window.StatusText);
        window.Close();
    }
}
