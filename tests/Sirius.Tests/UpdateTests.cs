using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;

namespace Sirius.Tests;

public class UpdateTests
{
    private static AppVersion V(string text) => AppVersion.Parse(text)!;

    private sealed class Fixture : IDisposable
    {
        private readonly TempFolder _settingsFolder = new();

        public Fixture(AppSettings? settings = null)
        {
            Settings = settings ?? new AppSettings();
            Window = new MainWindow(Settings, SettingsPath, null)
            {
                CurrentVersion = V("0.1.0-alpha.1"),
                // A consulta real falharia o teste: nenhum caso aqui pode sair para a internet.
                FetchLatestTag = _ =>
                {
                    Fetches++;
                    return Task.FromResult(Latest);
                },
                OpenUrl = url =>
                {
                    Opened.Add(url);
                    return OpenResult;
                },
            };
            Window.Language.IsInstalled = _ => false;
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public AppSettings Settings { get; }
        public MainWindow Window { get; }
        public string SettingsPath => Path.Combine(_settingsFolder.Path, "settings.json");
        public string? Latest { get; set; }
        public int Fetches { get; private set; }
        public List<string> Opened { get; } = [];
        public bool OpenResult { get; set; } = true;

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            _settingsFolder.Dispose();
        }
    }

    private static async Task Answer(MainWindow window, bool primary, Task pending)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsDialogOpen);
        window.AnswerDialog(primary);
        await pending;
    }

    // Ordem das versões

    [Theory]
    [InlineData("0.1.0-alpha.1", "0.1.0-alpha.2")]
    [InlineData("0.1.0-alpha.9", "0.1.0-alpha.10")]
    [InlineData("0.1.0-alpha.2", "0.1.0-beta.1")]
    [InlineData("0.1.0-alpha", "0.1.0-alpha.1")]
    [InlineData("0.1.0-1", "0.1.0-alpha")]
    [InlineData("0.1.0-rc.1", "0.1.0")]
    [InlineData("0.1.0", "0.1.1")]
    [InlineData("0.1.9", "0.2.0")]
    [InlineData("0.1.0-alpha.7", "0.2.0")]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.99.99", "1.0.0-alpha.1")]
    public void Versoes_ficam_na_ordem_certa(string older, string newer)
    {
        Assert.True(V(older).CompareTo(V(newer)) < 0);
        Assert.True(V(newer).CompareTo(V(older)) > 0);
    }

    [Theory]
    [InlineData("v0.1.0-alpha.1", "0.1.0-alpha.1")]
    [InlineData("0.1.0-alpha.1+fa61c7467739", "0.1.0-alpha.1")]
    public void Prefixo_e_codigo_do_commit_nao_mudam_a_versao(string text, string same)
    {
        Assert.Equal(0, V(text).CompareTo(V(same)));
        Assert.Equal(same, V(text).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1")]
    [InlineData("1.2")]
    [InlineData("latest")]
    [InlineData("1.0.0/../../evil")]
    [InlineData("1.0.0 x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("99999999999999999999.0.0")]
    public void Texto_que_nao_e_versao_e_recusado(string? text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void Versao_atual_vem_do_programa_sem_o_codigo_do_commit()
    {
        Assert.DoesNotContain("+", AppVersion.Current.Text);
        Assert.True(AppVersion.Current.CompareTo(V("0.0.1")) > 0);
    }

    // Resposta do GitHub

    [Fact]
    public void Le_a_versao_da_resposta_do_github() =>
        Assert.Equal("v0.1.0-alpha.2", UpdateChecker.ParseLatestTag("""{"tag_name":"v0.1.0-alpha.2","name":"Sirius"}"""));

    [Theory]
    [InlineData("")]
    [InlineData("nao e json")]
    [InlineData("[]")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("""{"tag_name":123}""")]
    [InlineData("""{"tag_name":"https://exemplo.com/"}""")]
    public void Resposta_estranha_nao_vira_versao(string json) => Assert.Null(UpdateChecker.ParseLatestTag(json));

    [Fact]
    public void Endereco_aponta_para_a_pagina_da_versao() =>
        Assert.Equal("https://github.com/erickfuzer/Sirius/releases/tag/v0.1.0-alpha.2", UpdateChecker.ReleasePageFor(V("v0.1.0-alpha.2")));

    // O que anunciar

    [Theory]
    [InlineData("v0.1.0-alpha.2", null, "0.1.0-alpha.2")]
    [InlineData("v0.1.0-alpha.1", null, null)]
    [InlineData("v0.0.9", null, null)]
    [InlineData(null, null, null)]
    [InlineData("invalida", null, null)]
    [InlineData("v0.1.0-alpha.2", "v0.1.0-alpha.2", null)]
    [InlineData("v0.1.0-alpha.3", "v0.1.0-alpha.2", "0.1.0-alpha.3")]
    public void Anuncia_so_versao_mais_nova_e_nao_ignorada(string? latest, string? dismissed, string? expected) =>
        Assert.Equal(expected, UpdateChecker.Pending(V("0.1.0-alpha.1"), latest, dismissed)?.Text);

    // Na janela

    [AvaloniaFact]
    public void Abrir_a_janela_nao_consulta_a_internet()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, fixture.Fetches);
        Assert.False(fixture.Window.IsUpdateNoticeVisible);
    }

    [AvaloniaFact]
    public void Versao_nova_aparece_na_capsula_e_fica_guardada()
    {
        using var fixture = new Fixture { Latest = "v0.1.0-alpha.2" };

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Fetches);
        Assert.True(fixture.Window.IsUpdateNoticeVisible);
        Assert.Equal("0.1.0-alpha.2", fixture.Window.PendingUpdateText);
        var saved = AppSettings.Load(fixture.SettingsPath);
        Assert.Equal("v0.1.0-alpha.2", saved.LatestVersion);
    }

    [AvaloniaFact]
    public void Janela_aberta_antes_da_versao_nova_avisa_ao_voltar_depois_de_uma_hora()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        using var fixture = new Fixture();
        fixture.Window.UpdateClock = () => now;
        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fixture.Fetches);
        Assert.False(fixture.Window.IsUpdateNoticeVisible);

        // Falhou: tenta logo, mas sem insistir a cada troca de janela.
        fixture.Latest = "v0.1.0-alpha.2";
        now = now.AddMinutes(4);
        fixture.Window.CheckForUpdatesIfDue();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fixture.Fetches);

        now = now.AddMinutes(1);
        fixture.Window.CheckForUpdatesIfDue();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, fixture.Fetches);
        Assert.True(fixture.Window.IsUpdateNoticeVisible);

        // Respondeu: a próxima consulta só depois de uma hora.
        now = now.AddMinutes(59);
        fixture.Window.CheckForUpdatesIfDue();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, fixture.Fetches);

        // Passou o tempo: voltar para a janela é o que dispara a consulta.
        now = now.AddMinutes(1);
        ((Window)fixture.Window).Activate();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, fixture.Fetches);
    }

    [AvaloniaFact]
    public void Aviso_desligado_nao_consulta_ao_voltar_para_a_janela()
    {
        using var fixture = new Fixture(new AppSettings { CheckForUpdates = false });
        fixture.Window.CheckForUpdatesIfDue();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, fixture.Fetches);
    }

    [AvaloniaFact]
    public void Aviso_cabe_inteiro_mesmo_com_muitos_problemas_na_capsula()
    {
        using var fixture = new Fixture(new AppSettings { LatestVersion = "v0.1.0-alpha.2" });
        fixture.Window.StartUpdateChecks();
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var notice = fixture.Window.FindControl<Button>("UpdateButton")!;
        var natural = notice.Bounds.Width;
        Assert.True(natural > 0);

        var problems = fixture.Window.FindControl<Button>("ProblemsButton")!;
        problems.Content = "120 erros, 300 avisos, 500 dicas, 700 erros, 900 avisos, 1000 dicas, 2000 erros";
        problems.IsVisible = true;
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(natural, notice.Bounds.Width);
    }

    [AvaloniaFact]
    public void Texto_encurta_ao_lado_do_aviso_de_instalar_suporte()
    {
        using var temp = new TempFolder();
        var php = temp.File("index.php", "<?php\n");
        using var fixture = new Fixture(new AppSettings { LatestVersion = "v0.1.0-alpha.2" });
        fixture.Window.StartUpdateChecks();
        Assert.Equal("Versão 0.1.0-alpha.2 disponível", fixture.Window.UpdateNoticeLabel);

        fixture.Window.OpenFile(php);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("PHP", fixture.Window.MissingServerName);
        Assert.Equal("Nova: 0.1.0-alpha.2", fixture.Window.UpdateNoticeLabel);
    }

    [AvaloniaFact]
    public void Mesma_versao_nao_mostra_aviso()
    {
        using var fixture = new Fixture { Latest = "v0.1.0-alpha.1" };

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Fetches);
        Assert.False(fixture.Window.IsUpdateNoticeVisible);
    }

    [AvaloniaFact]
    public void Consulta_toda_vez_que_a_sirius_abre()
    {
        using var fixture = new Fixture { Latest = "v0.1.0-alpha.2" };

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("0.1.0-alpha.2", fixture.Window.PendingUpdateText);

        // Uma versão publicada um minuto depois já aparece ao abrir de novo.
        fixture.Latest = "v0.1.0-alpha.3";
        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Fetches);
        Assert.Equal("0.1.0-alpha.3", fixture.Window.PendingUpdateText);
    }

    [AvaloniaFact]
    public void Sem_internet_mantem_o_aviso_que_ja_sabia()
    {
        using var fixture = new Fixture(new AppSettings { LatestVersion = "v0.1.0-alpha.2" });

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Fetches);
        Assert.True(fixture.Window.IsUpdateNoticeVisible);
        Assert.Equal("v0.1.0-alpha.2", fixture.Settings.LatestVersion);
    }

    [AvaloniaFact]
    public void Falha_silenciosa_tenta_de_novo_na_proxima_vez()
    {
        using var fixture = new Fixture();

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Fetches);
        Assert.Equal("", fixture.Window.StatusText);
        Assert.False(fixture.Window.IsDialogOpen);
    }

    [AvaloniaFact]
    public void Desligado_nao_consulta_nem_mostra_aviso_guardado()
    {
        using var fixture = new Fixture(new AppSettings { CheckForUpdates = false, LatestVersion = "v0.1.0-alpha.2" }) { Latest = "v0.1.0-alpha.3" };

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, fixture.Fetches);
        Assert.False(fixture.Window.IsUpdateNoticeVisible);
    }

    [AvaloniaFact]
    public void Desligar_esconde_e_ligar_volta_a_consultar()
    {
        using var fixture = new Fixture(new AppSettings { LatestVersion = "v0.1.0-alpha.2" });
        fixture.Window.StartUpdateChecks();
        Assert.True(fixture.Window.IsUpdateNoticeVisible);

        fixture.Window.ToggleUpdateChecks();
        Assert.False(fixture.Window.IsUpdateNoticeVisible);
        Assert.False(AppSettings.Load(fixture.SettingsPath).CheckForUpdates);

        fixture.Window.ToggleUpdateChecks();
        Assert.True(fixture.Window.IsUpdateNoticeVisible);
        Assert.True(AppSettings.Load(fixture.SettingsPath).CheckForUpdates);
    }

    [AvaloniaFact]
    public async Task Abrir_a_pagina_usa_o_navegador_e_nao_baixa_nada()
    {
        using var fixture = new Fixture { Latest = "v0.1.0-alpha.2" };
        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        await Answer(fixture.Window, primary: true, fixture.Window.ShowUpdateAsync());

        Assert.Equal(["https://github.com/erickfuzer/Sirius/releases/tag/v0.1.0-alpha.2"], fixture.Opened);
        // O aviso continua até a versão nova estar instalada.
        Assert.True(fixture.Window.IsUpdateNoticeVisible);
    }

    [AvaloniaFact]
    public async Task Navegador_que_nao_abre_avisa_na_capsula()
    {
        using var fixture = new Fixture { Latest = "v0.1.0-alpha.2", OpenResult = false };
        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        await Answer(fixture.Window, primary: true, fixture.Window.ShowUpdateAsync());

        Assert.Contains("navegador", fixture.Window.StatusText);
    }

    [AvaloniaFact]
    public async Task Ignorar_esconde_ate_sair_outra_versao()
    {
        using var fixture = new Fixture { Latest = "v0.1.0-alpha.2" };
        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();

        await Answer(fixture.Window, primary: false, fixture.Window.ShowUpdateAsync());

        Assert.Empty(fixture.Opened);
        Assert.False(fixture.Window.IsUpdateNoticeVisible);
        Assert.Equal("v0.1.0-alpha.2", AppSettings.Load(fixture.SettingsPath).DismissedVersion);

        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.Window.IsUpdateNoticeVisible);

        fixture.Latest = "v0.1.0-alpha.3";
        fixture.Window.StartUpdateChecks();
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.Window.IsUpdateNoticeVisible);
        Assert.Equal("0.1.0-alpha.3", fixture.Window.PendingUpdateText);
    }

    [AvaloniaFact]
    public async Task Procurar_de_proposito_mostra_o_resultado()
    {
        using var fixture = new Fixture();

        await fixture.Window.CheckForUpdatesAsync(manual: true);
        Assert.Contains("Não foi possível", fixture.Window.StatusText);

        fixture.Latest = "v0.1.0-alpha.1";
        await fixture.Window.CheckForUpdatesAsync(manual: true);
        Assert.Contains("mais recente (0.1.0-alpha.1)", fixture.Window.StatusText);
        Assert.False(fixture.Window.IsDialogOpen);
    }

    [AvaloniaFact]
    public async Task Procurar_de_proposito_traz_de_volta_a_versao_ignorada()
    {
        using var fixture = new Fixture(new AppSettings { DismissedVersion = "v0.1.0-alpha.2" }) { Latest = "v0.1.0-alpha.2" };

        await Answer(fixture.Window, primary: true, fixture.Window.CheckForUpdatesAsync(manual: true));

        Assert.Null(fixture.Settings.DismissedVersion);
        Assert.Single(fixture.Opened);
    }

    [AvaloniaFact]
    public void Barra_de_comando_oferece_procurar_e_desligar()
    {
        using var fixture = new Fixture();

        var names = fixture.Window.PaletteCommandNames("versão nova").ToList();

        Assert.Contains("Procurar versão nova", names);
        Assert.Contains("Aviso de versão nova", names);
    }

    // Preferências

    [Fact]
    public void Preferencias_antigas_continuam_com_o_aviso_ligado()
    {
        using var folder = new TempFolder();
        var path = folder.File("settings.json", """{"Theme":"grafite"}""");

        var settings = AppSettings.Load(path);

        Assert.True(settings.CheckForUpdates);
        Assert.Null(settings.LatestVersion);
    }
}
