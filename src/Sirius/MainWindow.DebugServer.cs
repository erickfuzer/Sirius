using System.Net.Sockets;
using Sirius.Core;
using Sirius.Lsp;

namespace Sirius;

// Depurar páginas servidas por outro programa, como o Apache. O Executar liga o Xdebug só durante a execução;
// um servidor já de pé lê o php.ini ao subir, então o Xdebug precisa estar lá, e a Sirius fica ouvindo na
// porta padrão do Xdebug. A mesma parada, as mesmas variáveis e os mesmos botões do Executar.
public partial class MainWindow
{
    // Trocados nos testes e nas sondas: a porta, o php.ini e o Xdebug nunca são os de verdade.
    public int ServerDebugPort { get; set; } = PhpIniXdebug.Port;
    public Func<Task<string?>> FindPhpIni { get; set; } = FindRealPhpIniAsync;
    // "" quando o PHP já carrega o Xdebug por conta própria; null quando não deu, com o motivo na cápsula.
    public Func<Task<string?>> ServerXdebugFile { get; set; } = null!;

    private bool _listeningToServer;
    public bool IsListeningToServer => _listeningToServer && _debug is not null;

    private static async Task<string?> FindRealPhpIniAsync() =>
        LanguageServerCatalog.FindExecutable("php") is { } php ? await XdebugSetup.LoadedIniAsync(php) : null;

    private async Task<string?> RealServerXdebugFileAsync()
    {
        if (LanguageServerCatalog.FindExecutable("php") is not { } php || await XdebugSetup.ProbeAsync(php) is not { } probe)
        {
            ShowStatus("A Sirius não encontrou o PHP deste computador.");
            return null;
        }
        if (probe.XdebugLoaded)
            return "";
        if (XdebugCatalog.Find(probe.Build) is not { } release)
        {
            ShowStatus($"Ainda não há Xdebug conhecido para o PHP {probe.Build.Version} deste computador.");
            return null;
        }
        ShowStatus("Baixando o Xdebug...");
        var file = XdebugSetup.InstalledFile(release, XdebugSetup.Folder)
            ?? await XdebugSetup.DownloadAsync(release, XdebugSetup.Folder, FetchXdebug);
        if (file is null)
            ShowStatus("Não foi possível baixar o Xdebug. Confira a internet.");
        else
            ClearStatus();
        return file;
    }

    public async Task ToggleServerDebugAsync()
    {
        if (IsListeningToServer)
        {
            EndDebug();
            ShowStatus("A Sirius parou de ouvir o servidor.");
            return;
        }
        await StartServerDebugAsync();
    }

    public async Task StartServerDebugAsync()
    {
        if (await FindPhpIni() is not { } ini || !File.Exists(ini))
        {
            ShowStatus("Este PHP não tem php.ini. Crie um a partir do php.ini-development da pasta do PHP.");
            return;
        }

        string text;
        try
        {
            text = PhpIniXdebug.Read(ini);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Não foi possível ler o php.ini.");
            return;
        }

        var turnedOn = false;
        if (!PhpIniXdebug.HasBlock(text))
        {
            var choice = await AskAsync("Ligar o Xdebug no servidor?",
                $"Para parar em páginas abertas por outro programa, como o Apache, o Xdebug precisa estar ligado no php.ini:\n{ini}\n\n" +
                "A Sirius guarda uma cópia antes e desfaz por \"Desligar o Xdebug do php.ini\" na barra de comando. Depois de ligar, reinicie o Apache.",
                "Ligar", null);
            if (choice != DialogChoice.Primary)
                return;
            if (await ServerXdebugFile() is not { } extension)
                return;
            try
            {
                PhpIniXdebug.Write(ini, PhpIniXdebug.Add(text, extension.Length == 0 ? null : extension));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShowStatus("Sem permissão para mudar o php.ini. Abra a Sirius como administrador ou mude o arquivo à mão.");
                return;
            }
            turnedOn = true;
        }

        EndDebug();
        var server = CreateDebugServer(ServerDebugPort);
        try
        {
            server.Start();
        }
        catch (SocketException)
        {
            server.Dispose();
            ShowStatus($"Outro programa já ouve a porta {ServerDebugPort} do Xdebug, como outro editor depurando. Feche-o e tente de novo.");
            return;
        }

        AttachDebug(server);
        _listeningToServer = true;
        TerminalInfo.Text = "Aguardando uma página do servidor";
        ShowVariables();
        ShowStatus(turnedOn
            ? "Xdebug ligado no php.ini. Reinicie o Apache e abra a página no navegador."
            : "Ouvindo o servidor. Abra a página no navegador.");
    }

    // Tira só o bloco que a Sirius pôs. A cópia de antes continua ao lado, com o nome .antes-da-sirius.
    public async Task DisableServerXdebugAsync()
    {
        if (IsListeningToServer)
            EndDebug();
        if (await FindPhpIni() is not { } ini || !File.Exists(ini))
        {
            ShowStatus("Este PHP não tem php.ini, então não há o que desligar.");
            return;
        }
        try
        {
            var text = PhpIniXdebug.Read(ini);
            if (!PhpIniXdebug.HasBlock(text))
            {
                ShowStatus("O php.ini não tem o Xdebug que a Sirius ligou.");
                return;
            }
            PhpIniXdebug.Write(ini, PhpIniXdebug.Remove(text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Sem permissão para mudar o php.ini.");
            return;
        }
        ShowStatus("Xdebug desligado do php.ini. Reinicie o Apache.");
    }
}
