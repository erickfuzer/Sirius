using Avalonia;
using Sirius.Core;

namespace Sirius;

class Program
{
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);

    // O canal desta Sirius com as que forem abertas depois. Null fora do programa de verdade, como nos testes.
    public static InstanceServer? Instance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ToolPaths.EnsureOnPath();

        // "Abrir com Sirius" num arquivo, com outra Sirius aberta: o arquivo vai para ela, e esta nem abre janela.
        // Uma pasta continua abrindo uma janela nova, para não trocar o projeto de quem está trabalhando.
        var instance = new InstanceServer(InstanceServer.DefaultName);
        if (!instance.TryStart() && args is [var start] && File.Exists(start)
            && InstanceClient.TrySend(InstanceServer.DefaultName, [Path.GetFullPath(start)], ForwardTimeout))
            return;
        Instance = instance;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
