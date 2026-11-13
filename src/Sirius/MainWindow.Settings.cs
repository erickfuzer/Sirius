using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sirius.Core;
using Sirius.Theming;

namespace Sirius;

// Configurações em seções: aparência, editor, PHP e depuração, Git, Claude Code e atualizações. O que se liga uma
// vez e se esquece mora aqui; o que se usa várias vezes por dia continua à mão.
public partial class MainWindow
{
    private string _settingsSection = "aparencia";
    private string? _gitName;
    private string? _gitEmail;

    public bool IsSettingsOpen => SettingsOverlay.IsVisible;
    public bool AutoClosePairs => _settings.AutoClosePairs;
    public string IndentationText => CodeEditor.Options.IndentationString;
    public string SettingsSection => _settingsSection;

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => OpenSettings();
    private void OnCloseSettings(object? sender, RoutedEventArgs e) => CloseSettings();
    private void OnSettingsOverlayPressed(object? sender, PointerPressedEventArgs e) => CloseSettings();

    public void OpenSettings(string? section = null)
    {
        PaletteOverlay.IsVisible = false;
        SettingsOverlay.IsVisible = true;
        if (section is not null)
            _settingsSection = section;
        RefreshSettings();
        LoadSectionAsync();
        // Tira o foco do editor, senão o que se digita iria para o código atrás do cartão.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SectionAparencia.Focus());
    }

    public void CloseSettings()
    {
        SettingsOverlay.IsVisible = false;
        FocusActiveDocument();
    }

    private void OnSettingClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag)
            _ = ChangeSettingAsync(tag);
    }

    public void ChangeSetting(string tag) => _ = ChangeSettingAsync(tag);

    // Cada botão diz o que muda, como "recuo:2" ou "pares:nao".
    public async Task ChangeSettingAsync(string tag)
    {
        var parts = tag.Split(':', 2);
        var value = parts.Length == 2 ? parts[1] : "";
        switch (parts[0])
        {
            case "secao":
                _settingsSection = value;
                RefreshSettings();
                LoadSectionAsync();
                return;
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
            case "autosalvar":
                _settings.AutoSave = value == "sim";
                _settings.Save(_settingsPath);
                // Ligar já salva o que estava pendente, para tela e disco ficarem iguais desde agora.
                AutoSaveNow();
                break;
            case "recarga":
                _settings.LiveReload = value == "sim";
                _settings.Save(_settingsPath);
                // Desligado, as páginas param de ouvir a Sirius, e o Navegador passa a abrir o endereço direto.
                if (!_settings.LiveReload)
                    StopBrowserServer();
                break;
            case "svg":
                if ((value == "sim") != _settings.SvgPreview)
                    ToggleSvgPreview();
                break;
            case "executar":
                await AskAndSaveRunCommandAsync();
                break;
            case "servidor":
                if ((value == "sim") != IsListeningToServer)
                    await ToggleServerDebugAsync();
                break;
            case "phpini":
                await DisableServerXdebugAsync();
                break;
            case "git":
                await ChangeGitIdentityAsync(value == "email");
                break;
            case "claude":
                await SetClaudeConnectedAsync(value == "conectar");
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

    // O que depende de consultar outro programa, como o Git e o Claude Code, chega depois de a seção aparecer.
    private async void LoadSectionAsync()
    {
        if (_settingsSection == "git")
        {
            var git = new GitClient(RootFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), GitEnvironment);
            _gitName = await git.ConfigAsync("user.name");
            _gitEmail = await git.ConfigAsync("user.email");
            RefreshSettings();
        }
        else if (_settingsSection == "claude")
        {
            await RefreshClaudeStateAsync();
            RefreshSettings();
        }
    }

    private async Task ChangeGitIdentityAsync(bool email)
    {
        var git = new GitClient(RootFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), GitEnvironment);
        var answer = email
            ? await AskTextAsync("Qual e-mail usar nos commits?",
                "Ele fica público se você publicar. No GitHub dá para usar um endereço noreply, em Settings, Emails.", "Salvar",
                value => value.Contains('@') ? null : "Digite um e-mail.", _gitEmail ?? "")
            : await AskTextAsync("Como assinar seus commits?",
                "Este nome aparece no histórico e, se você publicar o projeto, fica público.", "Salvar",
                value => string.IsNullOrWhiteSpace(value) ? "Digite um nome." : null, _gitName ?? "");
        if (answer is null)
            return;
        await git.SetGlobalConfigAsync(email ? "user.email" : "user.name", answer.Trim());
        if (email)
            _gitEmail = answer.Trim();
        else
            _gitName = answer.Trim();
    }

    private static readonly (string Key, string Title)[] SettingsSections =
    [
        ("aparencia", "Aparência"), ("editor", "Editor"), ("php", "PHP e depuração"),
        ("git", "Git"), ("claude", "Claude Code"), ("versao", "Atualizações"),
    ];

    private void RefreshSettings()
    {
        static void On(Button button, bool on) => button.Classes.Set("on", on);

        On(SectionAparencia, _settingsSection == "aparencia");
        On(SectionEditor, _settingsSection == "editor");
        On(SectionPhp, _settingsSection == "php");
        On(SectionGit, _settingsSection == "git");
        On(SectionClaude, _settingsSection == "claude");
        On(SectionUpdates, _settingsSection == "versao");
        PaneAparencia.IsVisible = _settingsSection == "aparencia";
        PaneEditor.IsVisible = _settingsSection == "editor";
        PanePhp.IsVisible = _settingsSection == "php";
        PaneGit.IsVisible = _settingsSection == "git";
        PaneClaude.IsVisible = _settingsSection == "claude";
        PaneUpdates.IsVisible = _settingsSection == "versao";
        SettingsSectionTitle.Text = SettingsSections.FirstOrDefault(section => section.Key == _settingsSection).Title ?? "";

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
        On(SettingAutoSaveOn, _settings.AutoSave);
        On(SettingAutoSaveOff, !_settings.AutoSave);
        On(SettingLiveReloadOn, _settings.LiveReload);
        On(SettingLiveReloadOff, !_settings.LiveReload);
        On(SettingSvgOn, _settings.SvgPreview);
        On(SettingSvgOff, !_settings.SvgPreview);

        // Só o que a pessoa escolheu: a detecção roda apenas no clique em Executar.
        SettingRunCommand.Text = RootFolder is null
            ? "Abra uma pasta para escolher o comando dela."
            : _settings.RunCommands.TryGetValue(RootFolder, out var command)
                ? $"Neste projeto: {command}"
                : "A Sirius descobre sozinha ao clicar em Executar.";
        SettingRunChange.IsEnabled = RootFolder is not null;
        On(SettingServerOn, IsListeningToServer);
        On(SettingServerOff, !IsListeningToServer);

        SettingGitName.Text = _gitName ?? "Ainda não escolhido. A Sirius pergunta no primeiro commit.";
        SettingGitEmail.Text = _gitEmail ?? "Ainda não escolhido. A Sirius pergunta no primeiro commit.";

        On(SettingClaudeOn, _claudeState == ClaudeState.Connected);
        On(SettingClaudeOff, _claudeState == ClaudeState.Disconnected);
        SettingClaudeOn.IsEnabled = _claudeState is not (ClaudeState.Missing or ClaudeState.Busy);
        SettingClaudeOff.IsEnabled = _claudeState is not (ClaudeState.Missing or ClaudeState.Busy);
        SettingClaudeStatus.Text = _claudeStatus;

        On(SettingUpdatesOn, _settings.CheckForUpdates);
        On(SettingUpdatesOff, !_settings.CheckForUpdates);
        SettingVersion.Text = $"Você está na versão {CurrentVersion}.";
    }
}
