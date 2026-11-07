using Avalonia.Interactivity;
using Sirius.Core;

namespace Sirius;

public partial class MainWindow
{
    // O GitHub aceita 60 consultas por hora sem login: uma por hora sobra, mesmo com várias janelas.
    private static readonly TimeSpan RecheckAfterAnswer = TimeSpan.FromHours(1);
    private static readonly TimeSpan RecheckAfterFailure = TimeSpan.FromMinutes(5);

    private AppVersion? _pendingUpdate;
    private bool _checkingForUpdate;
    private bool _recheckingOnReturn;
    private DateTime _nextUpdateCheck = DateTime.MinValue;

    // Trocados nos testes, que nunca usam a internet nem abrem o navegador.
    public Func<CancellationToken, Task<string?>> FetchLatestTag { get; set; } = UpdateChecker.FetchLatestTagAsync;
    public Func<string, bool> OpenUrl { get; set; } = UpdateChecker.OpenInBrowser;
    public AppVersion CurrentVersion { get; set; } = AppVersion.Current;
    public Func<DateTime> UpdateClock { get; set; } = () => DateTime.UtcNow;

    public string? PendingUpdateText => _pendingUpdate?.Text;
    public string UpdateNoticeLabel => UpdateButton.Content as string ?? "";
    public bool IsUpdateNoticeVisible => UpdateButton.IsVisible;
    public string StatusText => CapsuleStatus.Text ?? "";
    public IEnumerable<string> PaletteCommandNames(string query) => AllCommands(query).Select(command => command.Name);

    // Chamado só pelo App, nunca pelo construtor: abrir uma janela nos testes não pode consultar a internet.
    public void StartUpdateChecks()
    {
        RefreshUpdateNotice();
        if (_settings.CheckForUpdates)
            _ = CheckForUpdatesAsync(manual: false);

        // Uma janela que fica aberta por dias só consultaria uma vez, antes de a versão nova existir.
        if (!_recheckingOnReturn)
        {
            _recheckingOnReturn = true;
            Activated += (_, _) => CheckForUpdatesIfDue();
        }
    }

    public void CheckForUpdatesIfDue()
    {
        if (_settings.CheckForUpdates && UpdateClock() >= _nextUpdateCheck)
            _ = CheckForUpdatesAsync(manual: false);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdate)
            return;

        _checkingForUpdate = true;
        try
        {
            if (manual)
                ShowStatus("Procurando versão nova...");

            var tag = await FetchLatestTag(CancellationToken.None);
            _nextUpdateCheck = UpdateClock() + (tag is null ? RecheckAfterFailure : RecheckAfterAnswer);
            if (tag is null)
            {
                if (manual)
                    ShowStatus("Não foi possível procurar agora. Confira a internet.");
                return;
            }

            _settings.LatestVersion = tag;
            // Procurar de propósito traz de volta uma versão que tinha sido ignorada.
            if (manual)
                _settings.DismissedVersion = null;
            _settings.Save(_settingsPath);
            RefreshUpdateNotice();

            if (!manual)
                return;
            if (_pendingUpdate is null)
                ShowStatus($"Você já está na versão mais recente ({CurrentVersion})");
            else
                await ShowUpdateAsync();
        }
        finally
        {
            _checkingForUpdate = false;
        }
    }

    private void RefreshUpdateNotice()
    {
        // Durante o download, o botão mostra o andamento, e uma consulta no meio não pode trocá-lo.
        if (_updating)
            return;
        _pendingUpdate = _settings.CheckForUpdates
            ? UpdateChecker.Pending(CurrentVersion, _settings.LatestVersion, _settings.DismissedVersion)
            : null;
        UpdateButton.IsVisible = _pendingUpdate is not null;
        if (_pendingUpdate is { } version)
        {
            // A cápsula tem largura fixa: ao lado de outro aviso, o texto longo seria cortado.
            var crowded = InstallButton.IsVisible || ProblemsButton.IsVisible || OpenSiteButton.IsVisible;
            UpdateButton.Content = crowded ? $"Nova: {version}" : $"Versão {version} disponível";
        }
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e) => _ = ShowUpdateAsync();

    public async Task ShowUpdateAsync()
    {
        if (_pendingUpdate is not { } version || _updating)
            return;

        var choice = await AskAsync($"Sirius {version} disponível",
            $"Você está na versão {CurrentVersion}. A Sirius baixa a versão nova do GitHub, fecha, instala por cima e abre de novo. Suas preferências e abas continuam.",
            "Atualizar agora", "Ver as novidades");

        if (choice == DialogChoice.Primary)
            await InstallUpdateAsync(version);
        else if (choice == DialogChoice.Secondary)
            OpenReleasePage(version);
    }

    private void OpenReleasePage(AppVersion version)
    {
        if (!OpenUrl(UpdateChecker.ReleasePageFor(version)))
            ShowStatus("Não foi possível abrir o navegador.");
    }

    public void IgnorePendingUpdate()
    {
        if (_pendingUpdate is not { } version)
            return;
        _settings.DismissedVersion = version.Tag;
        _settings.Save(_settingsPath);
        RefreshUpdateNotice();
        ShowStatus($"A versão {version} não será mais anunciada.");
    }

    // Atualizar pela própria Sirius

    private bool _updating;

    // Trocados nos testes, que nunca baixam nem rodam instalador.
    public Func<AppVersion, IProgress<double>, CancellationToken, Task<string?>> DownloadUpdate { get; set; } = UpdateDownload.DownloadVersionAsync;
    public Func<string, bool> RunInstaller { get; set; } = UpdateDownload.StartInstaller;
    public bool IsUpdating => _updating;

    public async Task InstallUpdateAsync(AppVersion version)
    {
        if (_updating)
            return;
        _updating = true;
        UpdateButton.IsEnabled = false;
        var closing = false;
        try
        {
            UpdateButton.Content = "Baixando 0%";
            var progress = new Progress<double>(part => UpdateButton.Content = $"Baixando {(int)(part * 100)}%");
            var installer = await DownloadUpdate(version, progress, CancellationToken.None);
            if (installer is null)
            {
                ShowStatus("Não foi possível baixar a versão nova. Tente de novo ou use Ver as novidades.");
                return;
            }

            // O mesmo cuidado de fechar a janela: programa rodando e arquivos sem salvar perguntam antes.
            UpdateButton.Content = "Instalando...";
            SaveSession();
            _sessionFrozen = true;
            if (!await ConfirmStopTaskAsync() || !await CloseAllAsync())
            {
                _sessionFrozen = false;
                ShowStatus("Atualização adiada. O instalador já baixado fica guardado.");
                return;
            }

            if (!RunInstaller(installer))
            {
                _sessionFrozen = false;
                ShowStatus("Não foi possível abrir o instalador.");
                return;
            }

            // O instalador espera a Sirius fechar para trocar os arquivos, e depois a abre de novo.
            closing = true;
            _allowClose = true;
            Close();
        }
        finally
        {
            if (!closing)
            {
                _updating = false;
                UpdateButton.IsEnabled = true;
                RefreshUpdateNotice();
            }
        }
    }

    public void ToggleUpdateChecks()
    {
        _settings.CheckForUpdates = !_settings.CheckForUpdates;
        _settings.Save(_settingsPath);
        if (_settings.CheckForUpdates)
            StartUpdateChecks();
        else
            RefreshUpdateNotice();
        ShowStatus(_settings.CheckForUpdates ? "Aviso de versão nova ligado" : "Aviso de versão nova desligado");
    }
}
