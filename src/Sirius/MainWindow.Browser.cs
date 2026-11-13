using Avalonia.Interactivity;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius;

// O botão Navegador: abre a página em uso no navegador e a recarrega quando um arquivo do projeto muda,
// como o Live Server do VS Code. A página vem do servidor do Executar, do Apache ou da própria pasta.
public partial class MainWindow
{
    private LiveServer? _browserServer;

    // Trocado nos testes, que não dependem de um Apache ligado.
    public Func<ApacheSite?> FindApache { get; set; } = ApacheSite.FindRunning;

    public LiveServer? BrowserServer => _browserServer;
    public bool IsBrowserButtonVisible => BrowserButton.IsVisible;

    public static bool IsWebPage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".php";

    private bool CanOpenInBrowser => ActiveDocument is { IsImage: false, IsUntitled: false } document && IsWebPage(document.FullPath);

    private void UpdateBrowserButton() => BrowserButton.IsVisible = CanOpenInBrowser && !IsDiffOpen;

    private void OnBrowserButton(object? sender, RoutedEventArgs e) => OpenInBrowser();

    // De onde vem a página e em que caminho. Nulo: uma página PHP sem servidor nenhum para rodá-la.
    public (string? Upstream, string? StaticRoot, string Path)? BrowserTarget(string file)
    {
        // Numa pasta com index.php, a página é sempre ela, como no Abrir site: um .php avulso costuma ser só uma parte.
        var page = RunDetector.IsPhp(file) && RootFolder is { } project && File.Exists(Path.Combine(project, "index.php"))
            && LiveServer.IsInside(project, file) ? project : file;

        if (IsTaskRunning && RunDetector.SiteUrl(_taskCommand) is { } site)
        {
            var served = RunDetector.ServedRoot(_taskCommand!, WorkingFolder());
            var path = RunDetector.IsPhp(file)
                ? new Uri(RunDetector.SitePageUrl(_taskCommand, WorkingFolder(), file) ?? site).PathAndQuery
                : UrlPath(served, file) ?? "/";
            return (site, null, path);
        }

        if (FindApache() is { } apache && apache.PathFor(page) is { } apachePath)
            return (apache.Origin, null, apachePath);

        if (RunDetector.IsPhp(file))
            return null;
        var root = RootFolder is { } folder && LiveServer.IsInside(folder, file) ? folder : Path.GetDirectoryName(file)!;
        return (null, root, UrlPath(root, file) ?? "/");
    }

    private static string? UrlPath(string root, string file)
    {
        if (!LiveServer.IsInside(root, file))
            return null;
        var relative = Path.GetRelativePath(root, file);
        return relative == "." ? "/" : "/" + string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
    }

    public void OpenInBrowser()
    {
        if (!CanOpenInBrowser)
        {
            ShowStatus("Abra um arquivo .html ou .php para ver no navegador.");
            return;
        }
        var document = ActiveDocument!;
        if (BrowserTarget(document.FullPath) is not { } target)
        {
            ShowStatus("Para ver uma página PHP, clique em Executar primeiro.");
            return;
        }

        bool opened;
        if (!_settings.LiveReload)
            opened = OpenUrl(target.Upstream is { } upstream ? upstream + target.Path : new Uri(document.FullPath).AbsoluteUri);
        else
            opened = OpenThroughBrowserServer(target.Upstream, target.StaticRoot, target.Path, RootFolder ?? Path.GetDirectoryName(document.FullPath));

        if (!opened)
            ShowStatus("Não foi possível abrir o navegador.");
        else if (document.IsDirty)
            ShowStatus("O navegador mostra o que está salvo. Salve com Ctrl S para ver as mudanças.");
        else if (_settings.LiveReload)
            ShowStatus("Aberto no navegador. A página recarrega quando você salvar.");
    }

    private bool OpenThroughBrowserServer(string? upstream, string? staticRoot, string path, string? watch)
    {
        if (_browserServer is null)
        {
            var server = new LiveServer();
            try
            {
                server.Start();
            }
            catch (InvalidOperationException)
            {
                server.Dispose();
                return upstream is not null && OpenUrl(upstream + path);
            }
            _browserServer = server;
        }
        _browserServer.Upstream = upstream;
        _browserServer.StaticRoot = staticRoot;
        _browserServer.Watch(watch);
        return OpenUrl(_browserServer.Origin + path);
    }

    // Um Executar novo: a página que mostrava "o servidor não respondeu" recarrega quando ele subir.
    private void BrowserServerMayHaveChanged()
    {
        if (_browserServer is not { } server || !IsTaskRunning || RunDetector.SiteUrl(_taskCommand) is not { } site
            || !string.Equals(server.Upstream, site, StringComparison.OrdinalIgnoreCase))
            return;
        DispatcherTimer.RunOnce(() => server.Notify(cssOnly: false), TimeSpan.FromSeconds(1.5));
    }

    private void StopBrowserServer()
    {
        _browserServer?.Dispose();
        _browserServer = null;
    }
}
