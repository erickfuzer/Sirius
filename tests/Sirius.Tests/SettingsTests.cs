using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Theming;
using Xunit;

namespace Sirius.Tests;

public class SettingsTests
{
    private static MainWindow Open(TempFolder temp, TempFolder config, AppSettings? settings = null, string file = "a.php")
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(temp.File(file, ""));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static AppSettings Saved(TempFolder config) => AppSettings.Load(Path.Combine(config.Path, "settings.json"));

    private static Button Named(MainWindow window, string name) => window.FindControl<Button>(name)!;

    // O clique do mouse, como a pessoa faz.
    private static void Click(MainWindow window, string name)
    {
        Named(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Type(MainWindow window, string text)
    {
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Abre_pelo_botao_e_pela_barra_e_fecha_com_esc()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config);

        Click(window, "SettingsButton");
        Assert.True(window.IsSettingsOpen);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsSettingsOpen);

        Assert.Contains("Configurações", window.PaletteCommandNames("config"));
        window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsSettingsOpen);

        // A barra de comando por cima fecha as preferências.
        window.OpenPalette();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsSettingsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Recuo_muda_na_hora_e_fica_guardado()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, file: "a.txt");
        Assert.Equal("    ", window.IndentationText);
        window.OpenSettings();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("on", Named(window, "SettingIndent4").Classes);

        Click(window, "SettingIndent2");
        Assert.Equal("  ", window.IndentationText);
        Assert.Contains("on", Named(window, "SettingIndent2").Classes);
        Assert.DoesNotContain("on", Named(window, "SettingIndent4").Classes);
        Assert.Equal(2, Saved(config).IndentSize);

        Click(window, "SettingIndentTab");
        Assert.Equal("\t", window.IndentationText);
        Assert.True(Saved(config).IndentWithTabs);
        window.Close();

        // Um .txt: a aba volta antes de o teste desligar os servidores de linguagem, e um .php subiria o real.
        var reopened = Open(temp, config, Saved(config), "a.txt");
        Assert.Equal("\t", reopened.IndentationText);
        reopened.Close();
    }

    [AvaloniaFact]
    public void Tab_usa_o_recuo_escolhido()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, file: "a.txt");
        window.ChangeSetting("recuo:2");

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("  ", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Desligar_os_pares_para_de_fechar_aspas_e_tags()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, file: "a.html");
        window.OpenSettings();
        Dispatcher.UIThread.RunJobs();

        Click(window, "SettingPairsOff");
        Assert.False(Saved(config).AutoClosePairs);
        window.CloseSettings();
        Dispatcher.UIThread.RunJobs();
        Type(window, "<div>f(\"");

        Assert.Equal("<div>f(\"", window.ActiveDocument!.Document.Text);

        window.ChangeSetting("pares:sim");
        window.ActiveDocument!.Document.Text = "";
        Type(window, "f(");
        Assert.Equal("f()", window.ActiveDocument!.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Tema_letra_quebra_e_aviso_pelos_botoes()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, new AppSettings { Theme = "claro" });
        window.OpenSettings();
        Dispatcher.UIThread.RunJobs();

        Click(window, "SettingThemeGrafite");
        Assert.Equal(Palette.Grafite.Name, window.ThemeName);
        Assert.Contains("on", Named(window, "SettingThemeGrafite").Classes);
        Click(window, "SettingThemeGrafite");
        Assert.Equal(Palette.Grafite.Name, window.ThemeName);

        window.ChangeSetting("fonte:+");
        Assert.Equal(14.5, window.EditorFontSize);
        Assert.DoesNotContain("on", Named(window, "SettingFontDefault").Classes);
        Click(window, "SettingFontDefault");
        Assert.Equal(13.5, window.EditorFontSize);

        Click(window, "SettingWrapOn");
        Assert.True(window.IsWordWrapOn);
        Click(window, "SettingUpdatesOff");
        Click(window, "SettingUpdatesOff");

        var saved = Saved(config);
        Assert.Equal("grafite", saved.Theme);
        Assert.True(saved.WordWrap);
        Assert.False(saved.CheckForUpdates);
        window.Close();
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

    private static bool Visible(MainWindow window, string name) => window.FindControl<Control>(name)!.IsVisible;

    private static string Text(MainWindow window, string name) => window.FindControl<TextBlock>(name)!.Text ?? "";

    [AvaloniaFact]
    public void Secoes_mostram_uma_de_cada_vez()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config);
        window.OpenSettings();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Visible(window, "PaneAparencia"));

        Click(window, "SectionPhp");
        Assert.Equal("php", window.SettingsSection);
        Assert.True(Visible(window, "PanePhp"));
        Assert.False(Visible(window, "PaneAparencia"));
        Assert.Equal("PHP e depuração", Text(window, "SettingsSectionTitle"));
        Assert.Contains("on", Named(window, "SectionPhp").Classes);
        Assert.Contains("descobre sozinha", Text(window, "SettingRunCommand"));

        // A barra de comando leva direto à seção do Claude Code.
        window.CloseSettings();
        Assert.Contains("Conectar o Claude Code à Sirius", window.PaletteCommandNames("claude"));
        window.OpenSettings("claude");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Visible(window, "PaneClaude"));
        window.Close();
    }

    [AvaloniaFact]
    public void Previa_do_svg_pelas_configuracoes()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config);
        window.OpenSettings("editor");
        Dispatcher.UIThread.RunJobs();

        Click(window, "SettingSvgOff");
        Assert.False(Saved(config).SvgPreview);
        Assert.Contains("on", Named(window, "SettingSvgOff").Classes);
        Click(window, "SettingSvgOn");
        Assert.True(Saved(config).SvgPreview);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nome_e_email_do_git_aparecem_e_mudam()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var gitconfig = config.File("gitconfig", "[user]\n\tname = Ana\n");
        var window = Open(temp, config);
        window.GitEnvironment = new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = gitconfig, ["GIT_CONFIG_NOSYSTEM"] = "1" };
        window.OpenSettings("git");
        await Until(() => Text(window, "SettingGitName") == "Ana");
        Assert.Contains("Ainda não escolhido", Text(window, "SettingGitEmail"));

        var changing = window.ChangeSettingAsync("git:email");
        await Until(() => window.IsDialogOpen);
        window.KeyTextInput("ana@exemplo.com");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await changing;

        Assert.Equal("ana@exemplo.com", Text(window, "SettingGitEmail"));
        Assert.Contains("ana@exemplo.com", File.ReadAllText(gitconfig));
        window.Close();
    }

    // O Claude Code de mentira: guarda o que foi pedido e lembra se a Sirius está na configuração dele.
    private sealed class FakeClaude
    {
        public bool Connected { get; set; }
        public List<string[]> Calls { get; } = new();

        public Task<(int Code, string Output)> Run(string claude, string[] arguments)
        {
            Calls.Add(arguments);
            switch (arguments[1])
            {
                case "add":
                    Connected = true;
                    break;
                case "remove":
                    Connected = false;
                    break;
                case "get":
                    return Task.FromResult(Connected ? (0, "sirius") : (1, "No MCP server named \"sirius\""));
            }
            return Task.FromResult((0, ""));
        }
    }

    [AvaloniaFact]
    public async Task Claude_code_conecta_e_desconecta_pelos_botoes()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config);
        var claude = new FakeClaude();
        window.FindClaude = () => @"C:\claude\claude.exe";
        window.RunClaude = claude.Run;

        window.OpenSettings("claude");
        await Until(() => window.ClaudeStatusText.StartsWith("Desconectado"));
        Assert.Contains("on", Named(window, "SettingClaudeOff").Classes);

        Click(window, "SettingClaudeOn");
        await Until(() => window.IsClaudeConnected);
        Assert.Contains("on", Named(window, "SettingClaudeOn").Classes);
        Assert.StartsWith("Conectado", window.ClaudeStatusText);
        // Para todas as pastas, e com o caminho deste programa: é ele que o Claude Code abre com --mcp.
        var add = claude.Calls.Single(call => call[1] == "add");
        Assert.Equal(["mcp", "add", "--scope", "user", "sirius", "--", Environment.ProcessPath!, "--mcp"], add);

        Click(window, "SettingClaudeOff");
        await Until(() => window.ClaudeStatusText.StartsWith("Desconectado"));
        Assert.False(claude.Connected);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sem_claude_code_avisa_e_nao_conecta()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config);
        var claude = new FakeClaude();
        window.FindClaude = () => null;
        window.RunClaude = claude.Run;

        window.OpenSettings("claude");
        await Until(() => window.ClaudeStatusText.Contains("não está instalado"));
        Assert.False(Named(window, "SettingClaudeOn").IsEnabled);
        Click(window, "SettingClaudeOn");
        Assert.Empty(claude.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Salvar_sozinho_desligado_nao_grava()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, file: "a.txt");
        window.AutoSaveDelay = TimeSpan.FromMilliseconds(30);
        var path = window.ActiveDocument!.FullPath;

        window.ActiveDocument.Document.Insert(0, "novo");
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("", File.ReadAllText(path));
        Assert.True(window.ActiveDocument.IsDirty);
        window.ActiveDocument.Document.UndoStack.Undo();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Salvar_sozinho_grava_depois_de_parar_de_digitar()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, file: "a.txt");
        window.AutoSaveDelay = TimeSpan.FromMilliseconds(30);
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "antes");

        // Ligar já grava o que estava pendente.
        window.OpenSettings("editor");
        Click(window, "SettingAutoSaveOn");
        Assert.True(Saved(config).AutoSave);
        Assert.Equal("antes", File.ReadAllText(document.FullPath));
        Assert.False(document.IsDirty);

        document.Document.Insert(document.Document.TextLength, " e depois");
        await Until(() => !document.IsDirty);
        Assert.Equal("antes e depois", File.ReadAllText(document.FullPath));
        window.Close();
    }

    [AvaloniaFact]
    public void Salvar_sozinho_nao_grava_por_cima_de_outro_programa_nem_aba_sem_nome()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = Open(temp, config, new AppSettings { AutoSave = true }, file: "a.txt");
        var document = window.ActiveDocument!;
        document.Document.Insert(0, "meu");
        File.WriteAllText(document.FullPath, "de fora");
        File.SetLastWriteTimeUtc(document.FullPath, DateTime.UtcNow.AddMinutes(5));

        window.NewUntitled();
        window.ActiveDocument!.Document.Insert(0, "rascunho");
        var asked = false;
        window.PickSavePath = _ => { asked = true; return Task.FromResult<string?>(null); };
        window.AutoSaveNow();

        Assert.Equal("de fora", File.ReadAllText(document.FullPath));
        Assert.True(document.IsDirty);
        Assert.False(asked);
        window.Close();
    }

    [Fact]
    public void Preferencias_antigas_sem_os_campos_novos_usam_o_padrao()
    {
        using var config = new TempFolder();
        var path = config.File("settings.json", """{ "Theme": "grafite" }""");

        var settings = AppSettings.Load(path);

        Assert.Equal(4, settings.IndentSize);
        Assert.False(settings.IndentWithTabs);
        Assert.True(settings.AutoClosePairs);
    }
}
