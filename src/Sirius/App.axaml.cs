using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Palette.Apply(Palette.Claro.Name);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var path = AppSettings.DefaultPath;
            var start = desktop.Args is { Length: > 0 } args ? args[0] : null;
            var window = new MainWindow(AppSettings.Load(path), path, start);
            desktop.MainWindow = window;
            window.StartUpdateChecks();
            ListenForOtherInstances(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static readonly TimeSpan TakeOverInterval = TimeSpan.FromSeconds(3);

    private static void ListenForOtherInstances(MainWindow window)
    {
        if (Program.Instance is not { } instance)
            return;
        instance.OnReceived(paths => Dispatcher.UIThread.Post(() => window.ReceiveForwarded(paths)));
        if (instance.IsServing)
            return;

        // Esta janela abriu com outra já aberta, como numa pasta. Quando a outra fechar, esta passa a receber os arquivos.
        var timer = new DispatcherTimer { Interval = TakeOverInterval };
        timer.Tick += (_, _) =>
        {
            if (instance.TryStart())
                timer.Stop();
        };
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
    }
}
