using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

// Listas com milhares de linhas. Sem virtualização, uma pasta como node_modules aberta na árvore travava
// a janela por 2 segundos, e o painel de Git com 550 mudanças por mais de 1. O modelo próprio das listas
// precisa repassar o ItemsPanel ao ItemsPresenter, senão o Avalonia usa um StackPanel sem aviso.
public class ListasGrandesTests
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

    private static Panel PanelOf(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ItemsControl>(name)!;
        list.UpdateLayout();
        return list.ItemsPanelRoot!;
    }

    [AvaloniaFact]
    public async Task Arvore_git_e_busca_so_desenham_as_linhas_a_vista()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        temp.File("base.txt", "base");
        var git = await GitClientTests.Repository(temp);
        await git.RunAsync(["add", "-A"]);
        Assert.True((await git.RunAsync(["commit", "-m", "base"])).Ok);
        for (var i = 0; i < 400; i++)
            temp.File(Path.Combine("muitos", $"item{i:D3}.txt"), "novo");

        var window = new MainWindow(new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path) { Width = 1200, Height = 800 };
        window.Language.IsInstalled = _ => false;
        window.GitEnvironment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = Path.Combine(config.Path, "gitconfig"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A árvore com a pasta de 400 arquivos aberta.
        window.OpenFile(Path.Combine(temp.Path, "muitos", "item000.txt"));
        var tree = PanelOf(window, "TreeList");
        Assert.IsType<VirtualizingStackPanel>(tree);
        Assert.InRange(tree.Children.Count, 1, 100);

        // O painel de Git com 400 mudanças.
        window.OpenGit();
        await window.RefreshGitAsync();
        await Until(() => window.GitRows.Count == 400);
        var changes = PanelOf(window, "GitList");
        Assert.IsType<VirtualizingStackPanel>(changes);
        Assert.InRange(changes.Children.Count, 1, 100);

        window.ShowGitTab(history: true);
        await Until(() => window.HistoryRows.Count > 0);
        Assert.IsType<VirtualizingStackPanel>(PanelOf(window, "GitHistoryList"));
        window.CloseGit();

        window.OpenSearch();
        Assert.IsType<VirtualizingStackPanel>(PanelOf(window, "SearchList"));
        window.Close();
    }

    [Fact]
    public void Lista_sem_mudanca_nao_avisa_nada()
    {
        var list = new RowList<string>();
        list.ReplaceAll(["a", "b", "c"]);
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        list.ReplaceAll(["a", "b", "c"]);

        Assert.Empty(events);
    }

    [Fact]
    public void Poucas_linhas_mudadas_trocam_so_elas()
    {
        var list = new RowList<string>();
        list.ReplaceAll(Enumerable.Range(0, 500).Select(i => $"linha {i}"));
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.CollectionChanged += (_, e) => events.Add(e);

        // Como marcar uma caixinha: uma linha só.
        list.ReplaceAll(Enumerable.Range(0, 500).Select(i => i == 7 ? "marcada" : $"linha {i}"));

        var change = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
        Assert.Equal(7, change.NewStartingIndex);
        Assert.Equal("marcada", list[7]);
    }

    [Fact]
    public void Lista_nova_entra_com_um_aviso_so()
    {
        var list = new RowList<string>();
        list.ReplaceAll(["a"]);
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        list.ReplaceAll(Enumerable.Range(0, 550).Select(i => $"mudança {i}"));

        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal(550, list.Count);
        Assert.Equal("mudança 549", list[^1]);
    }
}
