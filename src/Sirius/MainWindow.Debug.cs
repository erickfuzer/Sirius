using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Lsp;
using Sirius.Theming;

namespace Sirius;

// Null em IniFolder: executar sem depurar. Cancelled: não executar.
public sealed record DebugPreparation(bool Cancelled, string? IniFolder);

// Depuração de PHP: pontos na margem, linha parada e variáveis na cápsula.
public partial class MainWindow
{
    private const double VariableIndent = 14;
    private static readonly TimeSpan SiteOpenDelay = TimeSpan.FromMilliseconds(800);

    private readonly ObservableCollection<VariableRow> _variableRows = new();
    private readonly Dictionary<string, List<int>> _closedBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedVariables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _previousValues = new(StringComparer.Ordinal);

    private XdebugServer? _debug;
    private DebugPause? _pause;
    private bool _showingVariables;
    private bool _pageWasPaused;

    public bool IsDebugging => _debug is not null;
    public bool IsDebugPaused => _pause is not null;
    public bool IsShowingVariables => _showingVariables;
    public bool AreDebugButtonsVisible => DebugButtons.IsVisible;
    public bool IsDebugOutVisible => DebugOutButton.IsVisible;
    public bool IsEditorTipOpen => ToolTip.GetIsOpen(CodeEditor);
    public string EditorTipText => (ToolTip.GetTip(CodeEditor) as TextBlock)?.Text ?? "";
    public int? PausedLine => _pause?.Line;
    public IReadOnlyList<VariableRow> VariableRows => _variableRows;
    public string TerminalInfoText => TerminalInfo.Text ?? "";
    public string? LastDebugCommand { get; private set; }
    public string VariablesHintText => VariablesHint.Text ?? "";

    // Trocados nos testes, que nunca baixam o Xdebug de verdade.
    public Func<Task<DebugPreparation>> PrepareXdebug { get; set; } = null!;
    public Func<string, CancellationToken, Task<byte[]>> FetchXdebug { get; set; } = XdebugSetup.FetchAsync;

    private void InitializeDebug()
    {
        PrepareXdebug = PrepareRealXdebugAsync;
        VariablesList.ItemsSource = _variableRows;
        _gutter.BreakpointClicked += ToggleBreakpoint;
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(new PausedLineRenderer(() => ActivePausedLine));
    }

    // A linha só é pintada na aba do arquivo em que a depuração parou.
    private int? ActivePausedLine =>
        _pause is { } pause && string.Equals(ActiveDocument?.FullPath, pause.Path, StringComparison.OrdinalIgnoreCase) ? pause.Line : null;

    private static bool CanHaveBreakpoints(OpenDocument? document) =>
        document is { IsImage: false, IsUntitled: false }
        && Path.GetExtension(document.FullPath).ToLowerInvariant() is ".php" or ".phtml" or ".inc";

    private void UpdateGutterBreakpoints()
    {
        var document = ActiveDocument;
        _gutter.Breakpoints = CanHaveBreakpoints(document) ? () => document!.BreakpointLines : null;
        _gutter.RedrawBreakpoints();
    }

    // Pontos de parada

    public void ToggleBreakpoint(int line)
    {
        if (!CanHaveBreakpoints(ActiveDocument))
            return;
        ActiveDocument!.ToggleBreakpoint(line);
        _gutter.RedrawBreakpoints();
        BreakpointsChanged();
    }

    private void BreakpointsChanged()
    {
        SaveSession();
        _debug?.SetBreakpoints(BreakpointsSnapshot());
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<int>> BreakpointsSnapshot()
    {
        var all = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, lines) in _closedBreakpoints)
        {
            if (lines.Count > 0)
                all[path] = lines;
        }
        foreach (var document in _documents)
        {
            var lines = document.BreakpointLines;
            if (lines.Count > 0)
                all[document.FullPath] = lines;
        }
        return all;
    }

    // A aba que abre retoma os pontos que o arquivo tinha.
    private void RestoreBreakpoints(OpenDocument document)
    {
        if (CanHaveBreakpoints(document) && _closedBreakpoints.Remove(document.FullPath, out var lines))
            document.SetBreakpoints(lines);
    }

    // Fechar a aba não apaga o que estava marcado.
    private void KeepBreakpoints(OpenDocument document)
    {
        var lines = document.BreakpointLines;
        if (lines.Count > 0)
            _closedBreakpoints[document.FullPath] = lines.ToList();
    }

    // Preparar o Xdebug

    private async Task<DebugPreparation> PrepareRealXdebugAsync()
    {
        if (LanguageServerCatalog.FindExecutable("php") is not { } php)
            return new DebugPreparation(false, null);

        ShowStatus("Preparando a depuração...");
        if (await XdebugSetup.ProbeAsync(php) is not { } probe)
            return await RunWithoutDebugAsync("A Sirius não conseguiu reconhecer este PHP.");

        var folder = XdebugSetup.Folder;
        try
        {
            // Quem já instalou o Xdebug por conta própria não precisa de outro, e dois dariam erro.
            if (probe.XdebugLoaded)
                return new DebugPreparation(false, XdebugSetup.WriteIni(folder, "php-proprio", null));

            if (XdebugCatalog.Find(probe.Build) is not { } release)
                return await RunWithoutDebugAsync($"Ainda não há Xdebug conhecido para o PHP {probe.Build.Version} deste computador.");

            var file = XdebugSetup.InstalledFile(release, folder);
            if (file is null)
            {
                var choice = await AskAsync("Baixar o Xdebug?",
                    "Para parar nas linhas marcadas, a Sirius precisa do Xdebug, um complemento gratuito do PHP. Ele vem de xdebug.org e fica só na pasta da Sirius, sem mudar a sua instalação do PHP.",
                    "Baixar", "Executar sem parar");
                if (choice == DialogChoice.Cancel)
                    return new DebugPreparation(true, null);
                if (choice != DialogChoice.Primary)
                    return new DebugPreparation(false, null);

                ShowStatus("Baixando o Xdebug...");
                file = await XdebugSetup.DownloadAsync(release, folder, FetchXdebug);
                if (file is null)
                    return await RunWithoutDebugAsync("Não foi possível baixar o Xdebug. Confira a internet.");
                ClearStatus();
            }
            return new DebugPreparation(false, XdebugSetup.WriteIni(folder, Path.GetFileNameWithoutExtension(release.FileName), file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return await RunWithoutDebugAsync("Não foi possível preparar a pasta do Xdebug.");
        }
    }

    private async Task<DebugPreparation> RunWithoutDebugAsync(string reason)
    {
        var choice = await AskAsync("Executar sem parar nas linhas?", reason + " O projeto ainda pode rodar normalmente.", "Executar", null);
        return choice == DialogChoice.Primary ? new DebugPreparation(false, null) : new DebugPreparation(true, null);
    }

    // Executar depurando

    private void StartDebugTask(string command, string iniFolder)
    {
        var server = CreateDebugServer();
        server.Start();
        LastDebugCommand = XdebugSetup.Command(command, iniFolder, server.Port);
        StartTask(command, null, LastDebugCommand);
        if (!IsTaskRunning)
        {
            server.Dispose();
            return;
        }

        AttachDebug(server);
        ShowVariables();

        // O site só existe depois que o servidor sobe, e a parada só acontece quando alguém abre uma página.
        DispatcherTimer.RunOnce(() =>
        {
            if (_debug == server)
                OpenSite();
        }, SiteOpenDelay);
    }

    private XdebugServer CreateDebugServer()
    {
        var server = new XdebugServer();
        server.SetBreakpoints(BreakpointsSnapshot());

        // Os eventos vêm de uma thread de fundo. Este método roda na da interface.
        var dispatcher = Dispatcher.UIThread;
        server.Paused += pause => dispatcher.Post(() => OnDebugPaused(server, pause));
        server.Resumed += () => dispatcher.Post(() => OnDebugResumed(server));
        server.PageEnded += () => dispatcher.Post(() => OnDebugPageEnded(server));
        return server;
    }

    // Ligar a janela a um servidor já de pé. Os testes usam com um Xdebug falso.
    public void AttachDebug(XdebugServer server)
    {
        _debug = server;
        _pageWasPaused = false;
        _previousValues.Clear();
        VariablesTabButton.IsVisible = true;
        TerminalInfo.Text = "Aguardando uma linha marcada";
        RefreshVariableRows();
        UpdateDebugButtons();
    }

    public XdebugServer StartDebugServerForTest()
    {
        var server = CreateDebugServer();
        server.Start();
        AttachDebug(server);
        return server;
    }

    private void OnDebugPaused(XdebugServer server, DebugPause pause)
    {
        if (server != _debug)
            return;

        _pause = pause;
        _pageWasPaused = true;
        if (File.Exists(pause.Path))
            GoToLocation(pause.Path, pause.Line, 1);
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        ShowVariables();
        var function = pause.Function is null ? "" : $", em {pause.Function}()";
        TerminalInfo.Text = $"Parado em {Path.GetFileName(pause.Path)}, linha {pause.Line}{function}";
        UpdateDebugButtons();

        // A pessoa está no navegador, olhando a página carregar: a Sirius precisa aparecer.
        base.Activate();
    }

    private void OnDebugResumed(XdebugServer server)
    {
        if (server != _debug)
            return;
        RememberValues();
        _pause = null;
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        TerminalInfo.Text = "Executando...";
        UpdateDebugButtons();
    }

    private void OnDebugPageEnded(XdebugServer server)
    {
        if (server != _debug)
            return;
        _pause = null;
        _previousValues.Clear();
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        // Páginas sem ponto marcado passam o tempo todo, como o CSS de um site: não muda o aviso.
        if (_pageWasPaused)
            TerminalInfo.Text = "A página terminou. Recarregue no navegador para parar de novo";
        _pageWasPaused = false;
        RefreshVariableRows();
        UpdateDebugButtons();
    }

    public void EndDebug()
    {
        if (_debug is null)
            return;
        _debug.Dispose();
        _debug = null;
        _pause = null;
        _previousValues.Clear();
        VariablesTabButton.IsVisible = false;
        UpdateDebugButtons();
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        if (_showingVariables)
        {
            HideVariables();
            // Volta para a saída do programa: mostra o que aconteceu ao parar.
            TerminalHost.IsVisible = true;
            TaskTabButton.Classes.Set("on", TerminalHost.Session == _task && _task is not null);
            ShellTabButton.Classes.Set("on", TerminalHost.Session == _shell && _shell is not null);
        }
        if (!_showingProblems)
            UpdateTerminalInfo();
    }

    private void UpdateDebugButtons()
    {
        DebugButtons.IsVisible = _pause is not null;
        DebugOutButton.IsVisible = _pause is { Depth: > 1 };
    }

    private void OnDebugContinue(object? sender, RoutedEventArgs e) => DebugContinue();
    private void OnDebugNext(object? sender, RoutedEventArgs e) => DebugNext();

    private void OnDebugInto(object? sender, RoutedEventArgs e) => DebugInto();
    private void OnDebugOut(object? sender, RoutedEventArgs e) => DebugOut();

    public void DebugContinue() => _debug?.Continue();
    public void DebugNext() => _debug?.StepOver();
    public void DebugInto() => _debug?.StepInto();

    // No corpo principal não há de onde sair: o programa só correria até o fim, sem avisar.
    public void DebugOut()
    {
        if (_pause is { Depth: > 1 })
            _debug?.StepOut();
    }

    // Dica ao parar o mouse: só no arquivo da parada, onde os nomes valem. Null para deixar as outras dicas.
    public string? DebugTipAt(int offset)
    {
        if (ActivePausedLine is null || ActiveDocument is not { } document)
            return null;
        var line = document.Document.GetLineByOffset(offset);
        return DebugValues.At(_pause!.Variables, document.Document.GetText(line), offset - line.Offset) is { } variable
            ? DebugValues.Describe(variable)
            : null;
    }

    // Variáveis

    private void OnShowVariables(object? sender, RoutedEventArgs e) => ShowVariables();

    public void ShowVariables()
    {
        SetTerminalOpen(true);
        _showingProblems = false;
        ProblemsList.IsVisible = false;
        ProblemsTabButton.Classes.Set("on", false);
        TerminalHost.IsVisible = false;
        TaskTabButton.Classes.Set("on", false);
        ShellTabButton.Classes.Set("on", false);
        TaskTabButton.IsVisible = _task is not null;
        VariablesTabButton.Classes.Set("on", true);
        _showingVariables = true;
        RefreshVariableRows();
    }

    private void HideVariables()
    {
        _showingVariables = false;
        VariablesTabButton.Classes.Set("on", false);
        VariablesList.IsVisible = false;
        VariablesHint.IsVisible = false;
    }

    private void OnVariableClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is VariableRow { Variable.Children.Count: > 0 } row)
            ToggleVariable(row.Variable.FullName);
    }

    public void ToggleVariable(string fullName)
    {
        if (!_expandedVariables.Remove(fullName))
            _expandedVariables.Add(fullName);
        RefreshVariableRows();
    }

    private void RememberValues()
    {
        _previousValues.Clear();
        if (_pause is not null)
            Remember(_pause.Variables);
    }

    private void Remember(IEnumerable<DebugVariable> variables)
    {
        foreach (var variable in variables)
        {
            _previousValues[variable.FullName] = variable.Value;
            Remember(variable.Children);
        }
    }

    private void RefreshVariableRows()
    {
        _variableRows.Clear();
        if (_pause is not null)
        {
            // Na primeira parada da página tudo é novidade, e pintar tudo não diz nada.
            AddVariableRows(_pause.Variables, 0, highlight: _previousValues.Count > 0);
        }

        if (!_showingVariables)
            return;
        VariablesList.IsVisible = _variableRows.Count > 0;
        VariablesHint.IsVisible = _variableRows.Count == 0;
        VariablesHint.Text = _pause is not null
            ? "Nenhuma variável nesta linha ainda."
            : "As variáveis aparecem aqui quando o programa parar em uma linha marcada. Abra a página no navegador.";
    }

    private void AddVariableRows(IEnumerable<DebugVariable> variables, int depth, bool highlight)
    {
        foreach (var variable in variables)
        {
            var changed = highlight && (!_previousValues.TryGetValue(variable.FullName, out var old) || old != variable.Value);
            var expanded = _expandedVariables.Contains(variable.FullName);
            var chevron = variable.Children.Count == 0 ? null : expanded ? ChevronOpen : ChevronClosed;
            _variableRows.Add(new VariableRow(variable.Name, variable.Value, variable.Type, new Thickness(depth * VariableIndent, 0, 0, 0), chevron,
                changed ? Palette.Current.DiffAdded : Brushes.Transparent, variable));
            if (expanded)
                AddVariableRows(variable.Children, depth + 1, highlight);
        }
    }
}
