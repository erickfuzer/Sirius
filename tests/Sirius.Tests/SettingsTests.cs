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

        Assert.Contains("Preferências", window.PaletteCommandNames("prefer"));
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
