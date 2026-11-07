using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sirius.Theming;

namespace Sirius;

// Tela de preferências: tema, letra, recuo, fechar pares, quebra de linha e aviso de versão.
public partial class MainWindow
{
    public bool IsSettingsOpen => SettingsOverlay.IsVisible;
    public bool AutoClosePairs => _settings.AutoClosePairs;
    public string IndentationText => CodeEditor.Options.IndentationString;

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => OpenSettings();
    private void OnCloseSettings(object? sender, RoutedEventArgs e) => CloseSettings();
    private void OnSettingsOverlayPressed(object? sender, PointerPressedEventArgs e) => CloseSettings();

    public void OpenSettings()
    {
        PaletteOverlay.IsVisible = false;
        SettingsOverlay.IsVisible = true;
        RefreshSettings();
        // Tira o foco do editor, senão o que se digita iria para o código atrás do cartão.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SettingThemeClaro.Focus());
    }

    public void CloseSettings()
    {
        SettingsOverlay.IsVisible = false;
        FocusActiveDocument();
    }

    private void OnSettingClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag)
            ChangeSetting(tag);
    }

    // Cada botão diz o que muda, como "recuo:2" ou "pares:nao".
    public void ChangeSetting(string tag)
    {
        var parts = tag.Split(':', 2);
        var value = parts.Length == 2 ? parts[1] : "";
        switch (parts[0])
        {
            case "tema":
                if (value != Palette.Current.Name)
                    ToggleTheme();
                break;
            case "fonte":
                SetEditorFontSize(value switch
                {
                    "+" => EditorFontSize + 1,
                    "-" => EditorFontSize - 1,
                    _ => EditorDefaultFontSize,
                });
                break;
            case "recuo":
                _settings.IndentWithTabs = value == "tab";
                if (int.TryParse(value, out var size))
                    _settings.IndentSize = size;
                _settings.Save(_settingsPath);
                ApplyIndentation();
                break;
            case "pares":
                _settings.AutoClosePairs = value == "sim";
                _settings.Save(_settingsPath);
                break;
            case "quebra":
                SetWordWrap(value == "sim");
                break;
            case "versao":
                if ((value == "sim") != _settings.CheckForUpdates)
                    ToggleUpdateChecks();
                break;
        }
        RefreshSettings();
    }

    // Recuo com espaços ou com Tab. O Tab ocupa 4 colunas na tela.
    private void ApplyIndentation()
    {
        var options = CodeEditor.Options;
        options.IndentationSize = _settings.IndentWithTabs ? 4 : Math.Clamp(_settings.IndentSize, 1, 8);
        options.ConvertTabsToSpaces = !_settings.IndentWithTabs;
    }

    private void RefreshSettings()
    {
        static void On(Button button, bool on) => button.Classes.Set("on", on);

        var claro = Palette.Current == Palette.Claro;
        On(SettingThemeClaro, claro);
        On(SettingThemeGrafite, !claro);
        SettingFontSize.Text = $"{EditorFontSize:0.#}";
        On(SettingFontDefault, Math.Abs(EditorFontSize - EditorDefaultFontSize) < 0.01);
        On(SettingIndent2, !_settings.IndentWithTabs && _settings.IndentSize == 2);
        On(SettingIndent4, !_settings.IndentWithTabs && _settings.IndentSize == 4);
        On(SettingIndentTab, _settings.IndentWithTabs);
        On(SettingPairsOn, _settings.AutoClosePairs);
        On(SettingPairsOff, !_settings.AutoClosePairs);
        On(SettingWrapOn, _settings.WordWrap);
        On(SettingWrapOff, !_settings.WordWrap);
        On(SettingUpdatesOn, _settings.CheckForUpdates);
        On(SettingUpdatesOff, !_settings.CheckForUpdates);
    }
}
