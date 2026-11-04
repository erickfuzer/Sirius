using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            desktop.MainWindow = new MainWindow(AppSettings.Load(path), path, start);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
