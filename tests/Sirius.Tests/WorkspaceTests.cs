using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class WorkspaceTests
{
    private static string SettingsPath(TempFolder settings) => Path.Combine(settings.Path, "settings.json");

    private static MainWindow Open(TempFolder temp, TempFolder settingsFolder, AppSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), SettingsPath(settingsFolder), temp.Path);
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

    // Grava com uma data diferente, para o teste não depender da precisão do relógio do disco.
    private static void WriteExternally(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
    }

    // Árvore

    [AvaloniaFact]
    public async Task Arquivo_criado_por_fora_aparece_na_arvore()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings);

        temp.File("novo.txt", "x");
        await Until(() => window.TreeRows.Any(r => r.Name == "novo.txt"));

        File.Delete(Path.Combine(temp.Path, "novo.txt"));
        await Until(() => window.TreeRows.All(r => r.Name != "novo.txt"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_criado_por_fora_entra_na_barra_de_comando()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings);
        await window.ProjectIndexed;

        temp.File("recente.cs", "x");
        await Until(() => window.TreeRows.Any(r => r.Name == "recente.cs"));
        await window.ProjectIndexed;

        window.OpenPalette();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("recente");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("recente.cs", window.ActiveDocument?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compilacao_em_pasta_ignorada_nao_mexe_na_arvore()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        temp.File("a.cs", "x");
        var window = Open(temp, settings);
        var before = window.TreeRows.ToList();

        temp.File(Path.Combine("bin", "saida.dll"), "x");
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, window.TreeRows);
        window.Close();
    }

    // Arquivos alterados por fora

    [AvaloniaFact]
    public async Task Arquivo_sem_alteracoes_atualiza_sozinho_e_mantem_o_cursor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "um\ndois\ntres");
        var window = Open(temp, settings);
        window.OpenFile(path);
        window.GoToLocation(path, 3, 1);
        var document = window.ActiveDocument!;

        WriteExternally(path, "um\ndois\ntres\nquatro");
        await Until(() => document.Document.Text.EndsWith("quatro"));

        Assert.False(document.IsDirty);
        Assert.False(window.IsDialogOpen);
        Assert.Equal(3, window.CaretLine);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_com_alteracoes_pergunta_e_manter_nao_perde_nada()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "original");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "minha ");

        WriteExternally(path, "de fora");
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("minha original", document.Document.Text);
        Assert.True(document.IsDirty);

        // Não insiste na mesma versão, mas salvar ainda confere.
        await window.CheckOpenDocumentsAsync();
        Assert.False(window.IsDialogOpen);
        var saving = window.SaveAsync(document);
        Assert.True(window.IsDialogOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(await saving);
        Assert.Equal("de fora", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_com_alteracoes_pode_usar_a_versao_do_disco()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "original");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "minha ");

        WriteExternally(path, "de fora");
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await Until(() => document.Document.Text == "de fora");

        Assert.False(document.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Salvar_na_sirius_nao_dispara_o_aviso()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "x");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "y");

        await window.SaveAsync(document);
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsDialogOpen);
        Assert.Equal("yx", document.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Arquivo_apagado_por_fora_continua_aberto_e_salvar_recria()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "precioso");
        var window = Open(temp, settings);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        File.Delete(path);
        await Until(() => document.MissingReported);

        Assert.Same(document, window.ActiveDocument);
        Assert.Equal("precioso", document.Document.Text);

        Assert.True(await window.SaveAsync(document));
        Assert.Equal("precioso", File.ReadAllText(path));
        window.Close();
    }

    // Reabrir as abas

    [AvaloniaFact]
    public void Reabre_as_abas_a_ativa_e_o_cursor()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var a = temp.File("a.txt", "1\n2\n3\n4");
        var b = temp.File("b.txt", "1\n2");

        var first = Open(temp, settings);
        first.OpenFile(b);
        first.OpenFile(a);
        first.GoToLocation(a, 3, 1);
        first.Close();
        Dispatcher.UIThread.RunJobs();

        var second = Open(temp, settings, AppSettings.Load(SettingsPath(settings)));

        Assert.Equal(["b.txt", "a.txt"], second.Documents.Select(d => d.Name));
        Assert.Equal("a.txt", second.ActiveDocument?.Name);
        Assert.Equal(3, second.CaretLine);
        second.Close();
    }

    [AvaloniaFact]
    public void Arquivo_que_sumiu_nao_impede_a_reabertura()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var a = temp.File("a.txt", "x");
        var b = temp.File("b.txt", "y");

        var first = Open(temp, settings);
        first.OpenFile(a);
        first.OpenFile(b);
        first.Close();
        Dispatcher.UIThread.RunJobs();
        File.Delete(b);

        var second = Open(temp, settings, AppSettings.Load(SettingsPath(settings)));

        Assert.Equal("a.txt", Assert.Single(second.Documents).Name);
        Assert.Equal("a.txt", second.ActiveDocument?.Name);
        second.Close();
    }

    [AvaloniaFact]
    public void Fechar_a_aba_antes_de_sair_nao_reabre_ela()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var a = temp.File("a.txt", "x");

        var first = Open(temp, settings);
        first.OpenFile(a);
        first.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        first.Close();
        Dispatcher.UIThread.RunJobs();

        var second = Open(temp, settings, AppSettings.Load(SettingsPath(settings)));

        Assert.Empty(second.Documents);
        second.Close();
    }

    [AvaloniaFact]
    public void Sair_descartando_alteracoes_ainda_reabre_a_aba()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var a = temp.File("a.txt", "original");

        var first = Open(temp, settings);
        first.OpenFile(a);
        first.ActiveDocument!.Document.Insert(0, "rascunho ");
        first.Close();
        Dispatcher.UIThread.RunJobs();
        first.AnswerDialog(primary: false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(first.IsVisible);

        var second = Open(temp, settings, AppSettings.Load(SettingsPath(settings)));

        Assert.Equal("original", Assert.Single(second.Documents).Document.Text);
        second.Close();
    }

    [AvaloniaFact]
    public void Cada_pasta_tem_as_suas_abas()
    {
        using var one = new TempFolder();
        using var two = new TempFolder();
        using var settings = new TempFolder();
        var a = one.File("a.txt", "x");

        var first = Open(one, settings);
        first.OpenFile(a);
        first.Close();
        Dispatcher.UIThread.RunJobs();

        var second = Open(two, settings, AppSettings.Load(SettingsPath(settings)));

        Assert.Empty(second.Documents);
        second.Close();
    }

    // Aba sem nome

    [AvaloniaFact]
    public void Aba_nova_vazia_fecha_sem_perguntar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings);

        window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Sem título 1", window.ActiveDocument?.Name);
        Assert.True(window.ActiveDocument!.IsUntitled);

        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsDialogOpen);
        Assert.Empty(window.Documents);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Salvar_aba_nova_pede_o_nome_e_passa_a_ser_arquivo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var target = Path.Combine(temp.Path, "feito.cs");
        var window = Open(temp, settings);
        window.PickSavePath = _ => Task.FromResult<string?>(target);
        window.NewUntitled();
        var document = window.ActiveDocument!;
        document.Document.Text = "class A { }";

        Assert.True(await window.SaveAsync(document));

        Assert.False(document.IsUntitled);
        Assert.False(document.IsDirty);
        Assert.Equal(target, document.FullPath);
        Assert.Equal("feito.cs", document.Name);
        Assert.Equal("class A { }", File.ReadAllText(target));
        Assert.NotNull(document.Highlighter);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelar_o_salvar_como_mantem_a_aba_sem_gravar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var window = Open(temp, settings);
        window.PickSavePath = _ => Task.FromResult<string?>(null);
        window.NewUntitled();
        var document = window.ActiveDocument!;
        document.Document.Text = "x";

        Assert.False(await window.SaveAsync(document));

        Assert.True(document.IsUntitled);
        Assert.True(document.IsDirty);
        Assert.Empty(Directory.GetFiles(temp.Path));
        window.Close();
    }

    [AvaloniaFact]
    public void Aba_sem_nome_nao_volta_na_proxima_sessao()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var first = Open(temp, settings);
        first.NewUntitled();
        first.Close();
        Dispatcher.UIThread.RunJobs();

        var second = Open(temp, settings, AppSettings.Load(SettingsPath(settings)));

        Assert.Empty(second.Documents);
        second.Close();
    }

    [AvaloniaFact]
    public async Task Salvar_como_nao_aceita_arquivo_aberto_em_outra_aba()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var existing = temp.File("a.txt", "importante");
        var window = Open(temp, settings);
        window.OpenFile(existing);
        window.PickSavePath = _ => Task.FromResult<string?>(existing);
        window.NewUntitled();
        var document = window.ActiveDocument!;
        document.Document.Text = "sobrescreveria";

        Assert.False(await window.SaveAsync(document));

        Assert.Equal("importante", File.ReadAllText(existing));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Salvar_tudo_grava_todas_as_abas_alteradas()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var a = temp.File("a.txt", "1");
        var b = temp.File("b.txt", "2");
        var window = Open(temp, settings);
        window.OpenFile(a);
        window.ActiveDocument!.Document.Insert(0, "a");
        window.OpenFile(b);
        window.ActiveDocument!.Document.Insert(0, "b");

        Assert.True(await window.SaveAllAsync());

        Assert.Equal("a1", File.ReadAllText(a));
        Assert.Equal("b2", File.ReadAllText(b));
        window.Close();
    }

    // Substituir no projeto

    private static void TypeSearch(MainWindow window, string text)
    {
        window.OpenSearch();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Substituir_pergunta_e_cancelar_nao_muda_nada()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt", "foo");
        var window = Open(temp, settings);
        TypeSearch(window, "foo");

        var replacing = window.ReplaceAllAsync("bar");
        await Until(() => window.IsDialogOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await replacing;

        Assert.Equal("foo", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Substituir_grava_os_fechados_e_deixa_os_abertos_sem_salvar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var open = temp.File("aberto.txt", "foo");
        var closed = temp.File("fechado.txt", "foo foo");
        var window = Open(temp, settings);
        window.OpenFile(open);
        var document = window.ActiveDocument!;
        TypeSearch(window, "foo");

        var replacing = window.ReplaceAllAsync("bar");
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await replacing;

        Assert.Equal("bar bar", File.ReadAllText(closed));
        Assert.Equal("bar", document.Document.Text);
        Assert.True(document.IsDirty);
        Assert.Equal("foo", File.ReadAllText(open));
        Assert.Equal("Substituídas 3 ocorrências em 2 arquivos.", window.SearchStatusText);

        document.Document.UndoStack.Undo();
        Assert.Equal("foo", document.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Substituir_mantem_a_codificacao_do_arquivo()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        var path = temp.File("a.txt");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes("coração foo"));
        var window = Open(temp, settings);
        TypeSearch(window, "foo");

        var replacing = window.ReplaceAllAsync("bar");
        await Until(() => window.IsDialogOpen);
        window.AnswerDialog(primary: true);
        await replacing;

        Assert.Equal(Encoding.Latin1.GetBytes("coração bar"), File.ReadAllBytes(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Substituir_sem_ocorrencias_avisa_sem_perguntar()
    {
        using var temp = new TempFolder();
        using var settings = new TempFolder();
        temp.File("a.txt", "nada");
        var window = Open(temp, settings);
        TypeSearch(window, "foo");

        await window.ReplaceAllAsync("bar");

        Assert.False(window.IsDialogOpen);
        Assert.Equal("Nada encontrado.", window.SearchStatusText);
        window.Close();
    }
}
