using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Theming;

[assembly: AvaloniaTestApplication(typeof(Sirius.Tests.TestAppBuilder))]

namespace Sirius.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

// Usam a janela de verdade, sem tela, simulando o que a pessoa faz.
public class MainWindowTests
{
    private static MainWindow Open(TempFolder temp, AppSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Abrir_pasta_mostra_os_arquivos()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "class A {}");

        var window = Open(temp);

        Assert.Contains(window.TreeRows, r => r.Name == "a.cs");
        Assert.Null(window.ActiveDocument);
    }

    [AvaloniaFact]
    public void Editar_marca_como_alterado_e_salvar_grava_no_disco()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "class A {}");
        var window = Open(temp);

        window.OpenFile(path);
        var document = window.ActiveDocument!;
        Assert.False(document.IsDirty);

        document.Document.Insert(0, "// oi\n");
        Assert.True(document.IsDirty);
        Assert.True(window.TreeRows.Single(r => r.Name == "a.cs").IsDirty);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.False(document.IsDirty);
        Assert.False(window.TreeRows.Single(r => r.Name == "a.cs").IsDirty);
        Assert.Equal("// oi\nclass A {}", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void Desfazer_tudo_volta_a_nao_alterado()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "x");
        var window = Open(temp);
        window.OpenFile(path);
        var document = window.ActiveDocument!;

        document.Document.Insert(0, "y");
        document.Document.UndoStack.Undo();

        Assert.False(document.IsDirty);
        Assert.Equal("x", document.Document.Text);
    }

    [AvaloniaFact]
    public void Abrir_o_mesmo_arquivo_duas_vezes_nao_duplica_a_aba()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "x");
        var window = Open(temp);

        window.OpenFile(path);
        window.OpenFile(path);

        Assert.Single(window.Documents);
    }

    [AvaloniaFact]
    public async Task Fechar_alterado_pergunta_e_descartar_nao_grava()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "original");
        var window = Open(temp);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "mudanca ");

        var closing = window.CloseDocumentAsync(document);
        Assert.True(window.IsDialogOpen);
        Assert.False(closing.IsCompleted);

        window.AnswerDialog(primary: false);
        Assert.True(await closing);

        Assert.Empty(window.Documents);
        Assert.Null(window.ActiveDocument);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Fechar_alterado_e_escolher_salvar_grava()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "original");
        var window = Open(temp);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "novo ");

        var closing = window.CloseDocumentAsync(document);
        window.AnswerDialog(primary: true);
        Assert.True(await closing);

        Assert.Equal("novo original", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Cancelar_o_fechamento_mantem_a_aba()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "original");
        var window = Open(temp);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "novo ");

        var closing = window.CloseDocumentAsync(document);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(await closing);

        Assert.Single(window.Documents);
        Assert.True(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task Arquivo_alterado_por_fora_pergunta_antes_de_substituir()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "original");
        var window = Open(temp);
        window.OpenFile(path);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "minha ");
        File.WriteAllText(path, "de outro programa");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var saving = window.SaveAsync(document);
        Assert.True(window.IsDialogOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(await saving);
        Assert.Equal("de outro programa", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void Arquivo_binario_nao_abre_aba()
    {
        using var temp = new TempFolder();
        var path = temp.File("foto.bin");
        File.WriteAllBytes(path, [1, 0, 2]);
        var window = Open(temp);

        window.OpenFile(path);

        Assert.Empty(window.Documents);
    }

    [AvaloniaFact]
    public async Task Barra_de_comando_encontra_e_abre_arquivo()
    {
        using var temp = new TempFolder();
        temp.File(Path.Combine("src", "alvo.cs"), "x");
        temp.File("outro.cs", "y");
        var window = Open(temp);
        await window.ProjectIndexed;

        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsPaletteOpen);

        window.KeyTextInput("alvo");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsPaletteOpen);
        Assert.Equal("alvo.cs", window.ActiveDocument?.Name);
    }

    private static async Task<MainWindow> OpenAndSearch(TempFolder temp, string text)
    {
        var window = Open(temp);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        await window.RunSearchAsync();
        return window;
    }

    [AvaloniaFact]
    public async Task Atalho_abre_a_busca_e_mostra_os_resultados()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "linha\nalvo aqui");
        temp.File("b.cs", "nada");

        var window = await OpenAndSearch(temp, "alvo");

        Assert.True(window.IsSearchOpen);
        Assert.Equal(2, window.SearchRows.Count);
        Assert.True(window.SearchRows[0].IsFile);
        Assert.Equal("a.cs", window.SearchRows[0].Title);
        Assert.Equal(2, window.SearchRows[1].Line);
        Assert.Equal("1 linha em 1 arquivo", window.SearchStatusText);
    }

    [AvaloniaFact]
    public async Task Clicar_no_resultado_abre_o_arquivo_na_linha_certa()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "linha\n  alvo aqui\nfim");

        var window = await OpenAndSearch(temp, "alvo");
        window.ActivateSearchRow(1, keepSearchFocus: false);

        Assert.Equal("a.cs", window.ActiveDocument?.Name);
        Assert.Equal(2, window.CaretLine);
        Assert.Equal("alvo", window.SelectedText);
    }

    [AvaloniaFact]
    public async Task Enter_avanca_pelos_resultados_e_volta_ao_inicio()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "alvo\nx\nalvo");
        temp.File("b.cs", "alvo");

        var window = await OpenAndSearch(temp, "alvo");

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("a.cs", 1), (window.ActiveDocument?.Name, window.CaretLine));
        Assert.True(window.IsSearchOpen);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("a.cs", 3), (window.ActiveDocument?.Name, window.CaretLine));

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("b.cs", 1), (window.ActiveDocument?.Name, window.CaretLine));

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("a.cs", 1), (window.ActiveDocument?.Name, window.CaretLine));
    }

    [AvaloniaFact]
    public async Task Esc_fecha_a_busca_e_volta_para_a_arvore()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "alvo");

        var window = await OpenAndSearch(temp, "alvo");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(window.IsSearchOpen);
    }

    [AvaloniaFact]
    public async Task Busca_enxerga_o_que_ainda_nao_foi_salvo()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "antigo");
        var window = Open(temp);
        window.OpenFile(path);
        window.ActiveDocument!.Document.Text = "novidade";

        window.OpenSearch();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("novidade");
        Dispatcher.UIThread.RunJobs();
        await window.RunSearchAsync();

        Assert.Equal(2, window.SearchRows.Count);
        Assert.Equal("antigo", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Regex_invalida_avisa_sem_mostrar_resultados()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "alvo");
        var window = Open(temp);
        window.OpenSearch();
        window.SetRegex(true);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("(");
        Dispatcher.UIThread.RunJobs();
        await window.RunSearchAsync();

        Assert.Empty(window.SearchRows);
        Assert.Equal("Expressão regular inválida.", window.SearchStatusText);
    }

    [AvaloniaFact]
    public async Task Abrir_outra_pasta_limpa_os_resultados()
    {
        using var temp = new TempFolder();
        temp.File("a.cs", "alvo");
        using var other = new TempFolder();

        var window = await OpenAndSearch(temp, "alvo");
        window.OpenFolder(other.Path);

        Assert.False(window.IsSearchOpen);
        Assert.Empty(window.SearchRows);
    }

    private static void Answer(MainWindow window, string text)
    {
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Criar_arquivo_grava_abre_e_aparece_na_arvore()
    {
        using var temp = new TempFolder();
        var window = Open(temp);

        var creating = window.CreateFileAsync(temp.Path);
        Answer(window, "novo.cs");
        await creating;

        Assert.True(File.Exists(Path.Combine(temp.Path, "novo.cs")));
        Assert.Equal("novo.cs", window.ActiveDocument?.Name);
        Assert.Contains(window.TreeRows, r => r.Name == "novo.cs");
    }

    [AvaloniaFact]
    public async Task Nome_que_ja_existe_mostra_erro_e_nao_sobrescreve()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "importante");
        var window = Open(temp);

        var creating = window.CreateFileAsync(temp.Path);
        Answer(window, "a.cs");

        Assert.True(window.IsDialogOpen);
        Assert.False(creating.IsCompleted);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await creating;

        Assert.False(window.IsDialogOpen);
        Assert.Equal("importante", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Criar_pasta_aparece_aberta_na_arvore()
    {
        using var temp = new TempFolder();
        var window = Open(temp);

        var creating = window.CreateFolderAsync(temp.Path);
        Answer(window, "pasta");
        await creating;

        Assert.True(Directory.Exists(Path.Combine(temp.Path, "pasta")));
        Assert.Contains(window.TreeRows, r => r.Name == "pasta" && r.IsFolder);
    }

    private static List<string> FakeTrash(MainWindow window)
    {
        var trashed = new List<string>();
        window.MoveToTrash = path =>
        {
            trashed.Add(path);
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            else
                File.Delete(path);
        };
        return trashed;
    }

    [AvaloniaFact]
    public async Task Excluir_pergunta_e_cancelar_nao_mexe_em_nada()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.cs", "x");
        var window = Open(temp);
        var trashed = FakeTrash(window);

        var deleting = window.DeleteAsync(path);
        Assert.True(window.IsDialogOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await deleting;

        Assert.Empty(trashed);
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task Excluir_pasta_fecha_as_abas_de_dentro_e_mantem_as_outras()
    {
        using var temp = new TempFolder();
        var inside = temp.File(Path.Combine("src", "a.cs"), "x");
        var outside = temp.File("b.cs", "y");
        var window = Open(temp);
        var trashed = FakeTrash(window);
        window.OpenFile(inside);
        window.OpenFile(outside);
        window.OpenFile(inside);

        var folder = Path.Combine(temp.Path, "src");
        var deleting = window.DeleteAsync(folder);
        window.AnswerDialog(primary: true);
        await deleting;

        Assert.Equal([folder], trashed);
        Assert.Equal("b.cs", Assert.Single(window.Documents).Name);
        Assert.Equal("b.cs", window.ActiveDocument?.Name);
        Assert.DoesNotContain(window.TreeRows, r => r.Name == "src");
    }

    [AvaloniaFact]
    public void Barra_lateral_redimensiona_dentro_dos_limites()
    {
        using var temp = new TempFolder();
        var window = Open(temp);
        var start = window.CurrentSidebarWidth;

        window.ResizeSidebar(50);
        Assert.Equal(start + 50, window.CurrentSidebarWidth);

        window.ResizeSidebar(-5000);
        Assert.Equal(160, window.CurrentSidebarWidth);

        window.ResizeSidebar(5000);
        Assert.True(window.CurrentSidebarWidth <= 640);
    }

    [AvaloniaFact]
    public void Largura_guardada_e_usada_ao_abrir()
    {
        using var temp = new TempFolder();
        var window = Open(temp, new AppSettings { SidebarWidth = 300 });
        Assert.Equal(300, window.CurrentSidebarWidth);
    }

    [AvaloniaFact]
    public void Busca_devolve_a_largura_ao_fechar()
    {
        using var temp = new TempFolder();
        var window = Open(temp, new AppSettings { SidebarWidth = 200 });

        window.OpenSearch();
        Assert.True(window.CurrentSidebarWidth >= 300);

        window.CloseSearch();
        Assert.Equal(200, window.CurrentSidebarWidth);
    }

    [AvaloniaFact]
    public void Trocar_tema_e_guardado_nas_preferencias()
    {
        using var temp = new TempFolder();
        var settings = new AppSettings();
        var window = Open(temp, settings);
        var before = window.ThemeName;

        window.ToggleTheme();

        Assert.NotEqual(before, window.ThemeName);
        Assert.Equal(window.ThemeName, settings.Theme);
        Assert.Equal(window.ThemeName, AppSettings.Load(Path.Combine(temp.Path, "settings.json")).Theme);
        Palette.Apply(Palette.Claro.Name);
    }

    [AvaloniaFact]
    public void Modo_foco_liga_e_desliga()
    {
        using var temp = new TempFolder();
        var window = Open(temp);

        window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control);
        Assert.True(window.IsFocusMode);

        window.ToggleFocus();
        Assert.False(window.IsFocusMode);
    }
}
