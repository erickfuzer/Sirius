using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sirius.Core;

namespace Sirius.Tests;

// O destaque de um botão de ícone é o próprio botão: ícone fora do centro aparece torto dentro dele.
public class IconCenterTests
{
    private const double Tolerance = 0.5;

    private static string Describe(Button button) =>
        Avalonia.Automation.AutomationProperties.GetName(button) is { Length: > 0 } name ? name : button.Name ?? ToolTip.GetTip(button)?.ToString() ?? "botão sem nome";

    private static List<string> OffCenter(Control root)
    {
        var problems = new List<string>();
        foreach (var button in root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
        {
            // Só os botões cujo conteúdo é um ícone: o de uma linha da lista tem texto e outros botões dentro.
            var inner = button.GetVisualDescendants().ToList();
            if (inner.OfType<TextBlock>().Any(text => !string.IsNullOrEmpty(text.Text)) || inner.OfType<Button>().Any())
                continue;
            var paths = inner.OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.RenderedGeometry is not null).ToList();
            if (paths.Count == 0)
                continue;

            // O ícone pode ter mais de um traço, como o do tema: vale o conjunto.
            var points = paths.SelectMany(path =>
            {
                var box = path.RenderedGeometry!.Bounds;
                return new[] { path.TranslatePoint(box.TopLeft, button), path.TranslatePoint(box.BottomRight, button) };
            }).OfType<Point>().ToList();
            if (points.Count == 0)
                continue;

            var dx = (points.Min(p => p.X) + points.Max(p => p.X)) / 2 - button.Bounds.Width / 2;
            var dy = (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2 - button.Bounds.Height / 2;
            if (Math.Abs(dx) > Tolerance || Math.Abs(dy) > Tolerance)
                problems.Add($"{Describe(button)}: {dx:0.00}, {dy:0.00}");
        }
        return problems;
    }

    private static void AssertCentered(Control root)
    {
        var problems = OffCenter(root);
        Assert.True(problems.Count == 0, "Fora do centro:\n" + string.Join('\n', problems));
    }

    private static async Task<MainWindow> OpenWithEverything(TempFolder temp, TempFolder config)
    {
        var git = await GitClientTests.Repository(temp);
        var file = temp.File("a.txt", "1\n");
        temp.File(Path.Combine("pasta", "b.txt"), "x\n");
        await git.RunAsync(["add", "-A"]);
        await git.RunAsync(["commit", "-m", "base"]);
        File.WriteAllText(file, "2\n");

        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.GitEnvironment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(config.Path, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        window.Width = 1400;
        window.Height = 900;
        window.Show();
        await window.RefreshGitAsync();
        window.OpenFile(file);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task Icones_ficam_no_centro_dos_botoes_na_tela_principal()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = await OpenWithEverything(temp, config);

        AssertCentered(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Icones_ficam_no_centro_dos_botoes_no_painel_de_git()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = await OpenWithEverything(temp, config);
        window.OpenGit();
        Dispatcher.UIThread.RunJobs();
        await window.RefreshGitAsync();
        Dispatcher.UIThread.RunJobs();
        await window.OpenDiffAsync(window.GitRows[0].Change);
        Dispatcher.UIThread.RunJobs();

        AssertCentered(window);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Icones_ficam_no_centro_dos_botoes_na_busca()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = await OpenWithEverything(temp, config);
        window.OpenSearch();
        Dispatcher.UIThread.RunJobs();

        AssertCentered(window);
        window.Close();
    }
}
