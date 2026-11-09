using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class DiskMovesTests
{
    private static MainWindow Open(TempFolder temp, TempFolder settingsFolder)
    {
        var window = new MainWindow(new AppSettings(), Path.Combine(settingsFolder.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

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

    // Sem janela

    [Fact]
    public void Renomeacao_de_pasta_leva_o_que_esta_dentro()
    {
        var renames = new List<(string, string)> { (@"C:\p\css", @"C:\p\estilos") };
        Assert.Equal(@"C:\p\estilos\a.css", DiskMoves.Apply(@"C:\p\css\a.css", renames));
        Assert.Equal(@"C:\p\estilos", DiskMoves.Apply(@"C:\p\css", renames));
        // Só a pasta com esse nome inteiro, não outra que começa igual.
        Assert.Equal(@"C:\p\css2\a.css", DiskMoves.Apply(@"C:\p\css2\a.css", renames));
    }

    [Fact]
    public void Renomeacoes_seguidas_valem_na_ordem()
    {
        var renames = new List<(string, string)> { (@"C:\p\a.txt", @"C:\p\b.txt"), (@"C:\p\b.txt", @"C:\p\c.txt") };
        Assert.Equal(@"C:\p\c.txt", DiskMoves.Apply(@"C:\p\a.txt", renames));
    }

    [Fact]
    public void Segue_o_arquivo_renomeado()
    {
        using var temp = new TempFolder();
        var old = Path.Combine(temp.Path, "a.txt");
        var renamed = temp.File("b.txt", "x");
        Assert.Equal(renamed, DiskMoves.Follow(old, File.GetLastWriteTimeUtc(renamed), [(old, renamed)], []));
    }

    [Fact]
    public void Salvar_por_temporario_nao_e_renomeacao()
    {
        using var temp = new TempFolder();
        // Outro programa renomeia o original para a cópia de segurança e põe o novo no lugar.
        var path = temp.File("a.txt", "novo");
        var backup = temp.File("a.txt.bak", "velho");
        var tmp = Path.Combine(temp.Path, "a.tmp");
        Assert.Null(DiskMoves.Follow(path, DateTime.UtcNow, [(path, backup), (tmp, path)], [tmp]));
    }

    [Fact]
    public void Segue_o_arquivo_movido_pela_data_da_gravacao()
    {
        using var temp = new TempFolder();
        var old = Path.Combine(temp.Path, "a.txt");
        var moved = temp.File(@"sub\a.txt", "x");
        var write = File.GetLastWriteTimeUtc(moved);
        Assert.Equal(moved, DiskMoves.Follow(old, write, [], [moved]));
        // Mesmo nome, mas gravado em outra hora: é outro arquivo.
        Assert.Null(DiskMoves.Follow(old, write.AddMinutes(-3), [], [moved]));
    }

    [Fact]
    public void Segue_o_arquivo_dentro_da_pasta_movida()
    {
        using var temp = new TempFolder();
        var old = Path.Combine(temp.Path, "css", "a.css");
        var moved = temp.File(@"site\css\a.css", "x");
        Assert.Equal(moved, DiskMoves.Follow(old, File.GetLastWriteTimeUtc(moved), [], [Path.Combine(temp.Path, "site", "css")]));
    }

    [Fact]
    public void Duas_copias_iguais_nao_decidem_para_onde_foi()
    {
        using var temp = new TempFolder();
        var old = Path.Combine(temp.Path, "a.txt");
        var first = temp.File(@"um\a.txt", "x");
        var second = temp.File(@"dois\a.txt", "x");
        var write = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(first, write);
        File.SetLastWriteTimeUtc(second, write);
        Assert.Null(DiskMoves.Follow(old, write, [], [first, second]));
    }

    // Na janela

    [AvaloniaFact]
    public async Task Aba_segue_o_arquivo_renomeado_por_fora()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("index.php", "<?php echo 1;");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(document.Document.TextLength, " // meu");

        var renamed = Path.Combine(temp.Path, "inicio.php");
        File.Move(path, renamed);
        await Until(() => document.FullPath == renamed);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(document, window.ActiveDocument);
        Assert.Equal("inicio.php", Assert.Single(window.TabRows).Name);
        Assert.Contains("inicio.php", window.StatusText);
        Assert.True(document.IsDirty);
        Assert.False(document.MissingReported);

        // Salvar grava no nome novo e não recria o antigo.
        Assert.True(await window.SaveAsync(document));
        Assert.Equal("<?php echo 1; // meu", File.ReadAllText(renamed));
        Assert.False(File.Exists(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pasta_renomeada_por_fora_leva_as_abas_e_continua_aberta()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File(@"css\estilo.css", "body {}");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        await Until(() => window.TreeRows.Any(r => r.Name == "estilo.css"));

        Directory.Move(Path.Combine(temp.Path, "css"), Path.Combine(temp.Path, "estilos"));
        var moved = Path.Combine(temp.Path, "estilos", "estilo.css");
        await Until(() => document.FullPath == moved);
        await Until(() => window.TreeRows.Any(r => r.Name == "estilos"));

        // A pasta aberta na árvore continua aberta com o nome novo.
        Assert.Contains(window.TreeRows, r => r.Name == "estilo.css");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Aba_segue_o_arquivo_movido_para_outra_pasta()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("logo.txt", "x");
        Directory.CreateDirectory(Path.Combine(temp.Path, "img"));
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        var moved = Path.Combine(temp.Path, "img", "logo.txt");
        File.Move(path, moved);
        await Until(() => document.FullPath == moved);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("em img", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Troca_so_de_maiusculas_tambem_segue()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("readme.md", "# Oi");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        var renamed = Path.Combine(temp.Path, "README.md");
        File.Move(path, renamed);
        await Until(() => document.Name == "README.md");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Outro_programa_salvando_por_temporario_atualiza_sem_trocar_a_aba()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "velho");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        var tmp = temp.File("a.txt.tmp", "novo");
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddMinutes(5));
        File.Move(path, path + ".bak");
        File.Move(tmp, path);
        await Until(() => document.Document.Text == "novo");

        Assert.Equal(path, document.FullPath);
        window.Close();
    }
}
