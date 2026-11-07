using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class SingleInstanceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Um nome por teste: os testes rodam em paralelo e não podem falar com a Sirius de verdade.
    private static string Name() => $"Sirius-teste-{Guid.NewGuid():N}";

    private static async Task<IReadOnlyList<string>> Next(TaskCompletionSource<IReadOnlyList<string>> received) =>
        await received.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_sirius_aberta_recebe_os_caminhos()
    {
        var name = Name();
        using var server = new InstanceServer(name);
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        server.OnReceived(paths => received.TrySetResult(paths));
        Assert.True(server.TryStart());

        Assert.True(InstanceClient.TrySend(name, [@"C:\projeto\a.php", @"C:\projeto\pasta com espaço\ação.css"], Wait));

        Assert.Equal([@"C:\projeto\a.php", @"C:\projeto\pasta com espaço\ação.css"], await Next(received));
    }

    [Fact]
    public void So_uma_sirius_ouve_o_canal()
    {
        var name = Name();
        using var first = new InstanceServer(name);
        using var second = new InstanceServer(name);

        Assert.True(first.TryStart());
        Assert.False(second.TryStart());
        Assert.True(first.IsServing);
        Assert.False(second.IsServing);
    }

    [Fact]
    public async Task O_que_chega_antes_da_janela_fica_guardado()
    {
        var name = Name();
        using var server = new InstanceServer(name);
        Assert.True(server.TryStart());

        Assert.True(InstanceClient.TrySend(name, [@"C:\a.txt"], Wait));
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        server.OnReceived(paths => received.TrySetResult(paths));

        Assert.Equal([@"C:\a.txt"], await Next(received));
    }

    [Fact]
    public async Task Caminho_incompleto_e_descartado()
    {
        var name = Name();
        using var server = new InstanceServer(name);
        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        server.OnReceived(paths => received.TrySetResult(paths));
        server.TryStart();

        Assert.True(InstanceClient.TrySend(name, [@"pasta\a.txt", @"..\b.txt", @"C:\c.txt"], Wait));

        Assert.Equal([@"C:\c.txt"], await Next(received));
    }

    [Fact]
    public void Sem_sirius_aberta_quem_chamou_abre_a_propria_janela()
    {
        var started = DateTime.UtcNow;
        Assert.False(InstanceClient.TrySend(Name(), [@"C:\a.txt"], TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Quando_a_primeira_fecha_outra_assume()
    {
        var name = Name();
        var first = new InstanceServer(name);
        using var second = new InstanceServer(name);
        Assert.True(first.TryStart());
        Assert.False(second.TryStart());

        first.Dispose();
        var limit = DateTime.UtcNow + Wait;
        while (!second.TryStart())
        {
            Assert.True(DateTime.UtcNow < limit, "A segunda não assumiu o canal.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        second.OnReceived(paths => received.TrySetResult(paths));
        Assert.True(InstanceClient.TrySend(name, [@"C:\a.txt"], Wait));
        Assert.Equal([@"C:\a.txt"], await Next(received));
    }

    [Fact]
    public async Task Varias_ao_mesmo_tempo_chegam_todas()
    {
        var name = Name();
        using var server = new InstanceServer(name);
        var all = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = new TaskCompletionSource();
        server.OnReceived(paths =>
        {
            foreach (var path in paths)
                all.Add(path);
            if (all.Count == 8)
                done.TrySetResult();
        });
        server.TryStart();

        var sends = Enumerable.Range(0, 8).Select(i => Task.Run(() => InstanceClient.TrySend(name, [$@"C:\arquivo{i}.txt"], Wait))).ToList();
        Assert.All(await Task.WhenAll(sends), Assert.True);
        await done.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(8, all.Distinct().Count());
    }

    [AvaloniaFact]
    public void A_janela_abre_em_aba_so_arquivos_que_existem()
    {
        using var temp = new TempFolder();
        var project = temp.File("index.php", "<?php");
        var outside = new TempFolder();
        var file = outside.File("nota.txt", "oi");
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        _ = project;

        window.ReceiveForwarded([Path.Combine(outside.Path, "sumiu.txt"), outside.Path, file]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("nota.txt", window.ActiveDocument?.Name);
        Assert.Single(window.Documents);
        Assert.Equal(temp.Path, window.RootFolder);
        window.Close();
        outside.Dispose();
    }
}
