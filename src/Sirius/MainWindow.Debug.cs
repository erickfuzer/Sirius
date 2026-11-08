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
    private const int MaxPageOutputChars = 20_000;
    private static readonly TimeSpan SiteOpenDelay = TimeSpan.FromMilliseconds(800);

    private readonly System.Text.StringBuilder _pageOutput = new();
    private string _heldOutput = "";
    private bool _pageOutputStarted;

    private readonly ObservableCollection<VariableRow> _variableRows = new();
    private readonly ObservableCollection<FrameRow> _frameRows = new();
    private int _frameIndex;
    private readonly Dictionary<string, List<int>> _closedBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, string>> _closedConditions = new(StringComparer.OrdinalIgnoreCase);
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
    public string PageOutputShown => PageOutputText.Text ?? "";
    public bool IsPageOutputVisible => PageOutputBox.IsVisible;
    public IReadOnlyList<FrameRow> FrameRows => _frameRows;
    public bool AreFramesVisible => FramesBox.IsVisible;
    public int SelectedFrameIndex => _frameIndex;

    // O nível da pilha que a pessoa está olhando. O primeiro é onde o programa parou.
    private DebugStackFrame? SelectedFrame =>
        _pause?.Frames is { Count: > 0 } frames ? frames[Math.Clamp(_frameIndex, 0, frames.Count - 1)] : null;

    // Trocados nos testes, que nunca baixam o Xdebug de verdade.
    public Func<Task<DebugPreparation>> PrepareXdebug { get; set; } = null!;
    public Func<string, CancellationToken, Task<byte[]>> FetchXdebug { get; set; } = XdebugSetup.FetchAsync;

    private void InitializeDebug()
    {
        PrepareXdebug = PrepareRealXdebugAsync;
        ServerXdebugFile = RealServerXdebugFileAsync;
        Opened += (_, _) => _ = ResumeServerDebugAsync();
        VariablesList.ItemsSource = _variableRows;
        FramesList.ItemsSource = _frameRows;
        _gutter.BreakpointClicked += ToggleBreakpoint;
        _gutter.BreakpointConditionRequested += line => _ = EditBreakpointConditionAsync(line);
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
        _gutter.Conditions = CanHaveBreakpoints(document) ? () => document!.BreakpointConditions : null;
        _gutter.RedrawBreakpoints();
    }

    public bool CanSetBreakpointHere => CanHaveBreakpoints(ActiveDocument);

    // Expressões vigiadas

    private readonly List<string> _watches = new();
    public IReadOnlyList<string> Watches => _watches;

    private void OnAddWatch(object? sender, RoutedEventArgs e) => _ = AddWatchAsync();

    private void OnRemoveWatch(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is VariableRow { IsWatch: true } row)
            RemoveWatch(row.Name);
    }

    // Sem texto, pergunta. Com texto, como a seleção do editor, vigia direto.
    public async Task AddWatchAsync(string? expression = null)
    {
        expression ??= await AskTextAsync("Vigiar expressão",
            "Escreva uma expressão em PHP, como $total * 2 ou count($lista). O valor aparece em cima das variáveis a cada parada.", "Vigiar",
            value => string.IsNullOrWhiteSpace(value) ? "Escreva uma expressão." : null);
        if (string.IsNullOrWhiteSpace(expression))
            return;
        expression = expression.Trim();
        if (!_watches.Contains(expression))
            _watches.Add(expression);
        WatchesChanged();
        ShowVariables();
    }

    public void RemoveWatch(string expression)
    {
        if (_watches.Remove(expression))
            WatchesChanged();
    }

    private void WatchesChanged()
    {
        SaveSession();
        _debug?.SetWatches(_watches.ToList());
        RefreshVariableRows();
    }

    // Parar só quando a condição em PHP for verdadeira, como $i == 5. Pelo botão direito no ponto,
    // pelo menu do editor ou pela barra de comando. Sem ponto na linha, ele é criado.
    public async Task EditBreakpointConditionAsync(int? line = null)
    {
        if (!CanHaveBreakpoints(ActiveDocument))
        {
            ShowStatus("Pontos de parada valem em arquivos PHP.");
            return;
        }
        var document = ActiveDocument!;
        var target = line ?? CodeEditor.TextArea.Caret.Line;
        var current = document.BreakpointConditions.GetValueOrDefault(target, "");
        var condition = await AskTextAsync($"Parar na linha {target} só quando…",
            "Escreva uma condição em PHP, como $i == 5 ou $nome === 'Ana'. Deixe vazio para parar sempre.", "Salvar",
            _ => null, current);
        if (condition is null || ActiveDocument != document)
            return;

        document.SetBreakpointCondition(target, condition);
        _gutter.RedrawBreakpoints();
        BreakpointsChanged();
        ShowStatus(string.IsNullOrWhiteSpace(condition)
            ? $"A linha {target} para sempre."
            : $"A linha {target} para só quando {condition.Trim()}.");
    }

    // Pontos de parada

    public void ToggleBreakpoint(int line)
    {
        if (!CanHaveBreakpoints(ActiveDocument))
            return;
        var marked = ActiveDocument!.ToggleBreakpoint(line);
        _gutter.RedrawBreakpoints();
        BreakpointsChanged();
        if (marked && IsHtmlOnlyLine(ActiveDocument, line))
            ShowStatus($"A linha {line} é só HTML, sem PHP para parar. Na depuração, o ponto vai para a próxima linha com PHP.");
    }

    // Fora de <?php ... ?> e sem abrir PHP na própria linha: o Xdebug não tem onde parar ali.
    private static bool IsHtmlOnlyLine(OpenDocument document, int line)
    {
        var text = document.Document.Text;
        var start = document.Document.GetLineByNumber(line);
        return !Markup.InPhp(text, start.Offset) && !document.Document.GetText(start).Contains("<?", StringComparison.Ordinal);
    }

    // O Xdebug levou o ponto para a linha com código mais próxima. A margem acompanha, com a condição junto,
    // para a pessoa ver onde a página vai parar de verdade, como no VS Code.
    private void OnBreakpointMoved(XdebugServer server, string path, int from, int to)
    {
        if (server != _debug)
            return;
        if (_documents.Find(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase)) is { } document)
        {
            if (!document.BreakpointLines.Contains(from) || to < 1 || to > document.Document.LineCount)
                return;
            var condition = document.BreakpointConditions.GetValueOrDefault(from);
            document.ToggleBreakpoint(from);
            if (!document.BreakpointLines.Contains(to))
                document.SetBreakpointCondition(to, condition);
            _gutter.RedrawBreakpoints();
        }
        else if (_closedBreakpoints.TryGetValue(path, out var lines) && lines.Remove(from))
        {
            if (!lines.Contains(to))
                lines.Add(to);
            if (_closedConditions.TryGetValue(path, out var conditions) && conditions.Remove(from, out var condition))
                conditions[to] = condition;
        }
        else
        {
            return;
        }
        BreakpointsChanged();
        ShowStatus($"O ponto da linha {from} foi para a {to}, a próxima com PHP: na {from} não há código para parar.");
    }

    private void BreakpointsChanged()
    {
        SaveSession();
        _debug?.SetConditions(ConditionsSnapshot());
        _debug?.SetBreakpoints(BreakpointsSnapshot());
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> ConditionsSnapshot()
    {
        var all = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, conditions) in _closedConditions)
        {
            if (conditions.Count > 0)
                all[path] = conditions;
        }
        foreach (var document in _documents)
        {
            var conditions = document.BreakpointConditions;
            if (conditions.Count > 0)
                all[document.FullPath] = conditions;
        }
        return all;
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
        {
            _closedConditions.Remove(document.FullPath, out var conditions);
            document.SetBreakpoints(lines, conditions);
        }
    }

    // Fechar a aba não apaga o que estava marcado.
    private void KeepBreakpoints(OpenDocument document)
    {
        var lines = document.BreakpointLines;
        if (lines.Count > 0)
            _closedBreakpoints[document.FullPath] = lines.ToList();
        var conditions = document.BreakpointConditions;
        if (conditions.Count > 0)
            _closedConditions[document.FullPath] = conditions.ToDictionary();
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
        // Ouvindo o servidor, o Executar usa a mesma escuta: as páginas dos dois param, e nenhum desliga o outro.
        var shared = IsListeningToServer;
        var server = shared ? _debug! : CreateDebugServer();
        if (!shared)
            server.Start();
        LastDebugCommand = XdebugSetup.Command(command, iniFolder, server.Port);
        StartTask(command, null, LastDebugCommand);
        if (!IsTaskRunning)
        {
            if (!shared)
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

    private XdebugServer CreateDebugServer(int port = 0)
    {
        var server = new XdebugServer(port);
        server.SetConditions(ConditionsSnapshot());
        server.SetBreakpoints(BreakpointsSnapshot());
        server.SetWatches(_watches.ToList());

        // Os eventos vêm de uma thread de fundo. Este método roda na da interface.
        var dispatcher = Dispatcher.UIThread;
        server.Paused += pause => dispatcher.Post(() => OnDebugPaused(server, pause));
        server.Resumed += () => dispatcher.Post(() => OnDebugResumed(server));
        server.PageEnded += () => dispatcher.Post(() => OnDebugPageEnded(server));
        server.OutputWritten += text => dispatcher.Post(() => OnDebugOutput(server, text));
        server.BreakpointMoved += (path, from, to) => dispatcher.Post(() => OnBreakpointMoved(server, path, from, to));
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

    // Para a janela maximizada voltar maximizada, e não no tamanho menor.
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() == WindowState.Minimized)
            _stateBeforeMinimize = change.GetOldValue<WindowState>();
    }

    private void OnDebugPaused(XdebugServer server, DebugPause pause)
    {
        if (server != _debug)
            return;

        StartPageOutput();
        _pageOutputStarted = true;
        _heldOutput = pause.HeldOutput;

        _pause = pause;
        _frameIndex = 0;
        _pageWasPaused = true;
        if (File.Exists(pause.Path))
            GoToLocation(pause.Path, pause.Line, 1);
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        ShowVariables();
        ShowPausedInfo();
        UpdateDebugButtons();

        BringToFrontForPause();
    }

    private void ShowPausedInfo()
    {
        if (_pause is not { } pause)
            return;
        if (_frameIndex > 0 && SelectedFrame is { } frame)
        {
            TerminalInfo.Text = $"Vendo quem chamou: {frame.Name}, em {Path.GetFileName(frame.Path)}, linha {frame.Line}";
            return;
        }
        var function = pause.Function is null ? "" : $", em {pause.Function}()";
        TerminalInfo.Text = $"Parado em {Path.GetFileName(pause.Path)}, linha {pause.Line}{function}";
    }

    // Pilha de chamadas

    private void OnFrameClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FrameRow row)
            SelectFrame(row.Index);
    }

    // Leva à linha daquele nível e mostra as variáveis de lá. O programa continua parado onde estava.
    public void SelectFrame(int index)
    {
        if (_pause is not { } pause || index < 0 || index >= pause.Frames.Count)
            return;
        _frameIndex = index;
        var frame = pause.Frames[index];
        if (File.Exists(frame.Path))
            GoToLocation(frame.Path, frame.Line, 1);
        ShowPausedInfo();
        RefreshVariableRows();
    }

    private void RefreshFrameRows()
    {
        _frameRows.Clear();
        var frames = _pause?.Frames ?? [];
        // Só o arquivo principal não é uma pilha: a lista só ocuparia espaço.
        if (frames.Count > 1)
        {
            var palette = Palette.Current;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                _frameRows.Add(new FrameRow(frame.Name, $"{Path.GetFileName(frame.Path)}:{frame.Line}", $"{frame.Path}, linha {frame.Line}",
                    i == _frameIndex ? palette.CapsuleMark : Brushes.Transparent, i));
            }
        }
        FramesBox.IsVisible = _showingVariables && _frameRows.Count > 0;
    }

    // A pessoa está no navegador, olhando a página carregar: a Sirius precisa aparecer.
    // O Windows recusa o foco a quem está em segundo plano, por isso só o Activate não bastava.
    // Ligar e desligar o "sempre visível" sobe a janela sem tirar o foco de onde a pessoa digita.
    private void BringToFrontForPause()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize;
        base.Activate();
        Topmost = true;
        Topmost = false;
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

    // O navegador costuma esperar a página terminar para mostrar qualquer coisa. Aqui o echo aparece
    // na hora, e fica claro que a linha rodou.
    private void OnDebugOutput(XdebugServer server, string text)
    {
        if (server != _debug)
            return;
        StartPageOutput();
        _pageOutputStarted = true;
        _pageOutput.Append(text);
        if (_pageOutput.Length > MaxPageOutputChars)
            _pageOutput.Remove(0, _pageOutput.Length - MaxPageOutputChars);
        UpdatePageOutput();
    }

    // O texto da página anterior fica à vista até a nova escrever ou parar.
    private void StartPageOutput()
    {
        if (_pageOutputStarted)
            return;
        _pageOutput.Clear();
        _heldOutput = "";
    }

    private void UpdatePageOutput()
    {
        PageOutputText.Text = _pageOutput + _heldOutput;
        PageOutputBox.IsVisible = _showingVariables && PageOutputText.Text.Length > 0;
        Dispatcher.UIThread.Post(() => PageOutputScroll.ScrollToEnd(), DispatcherPriority.Background);
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
        // No fim, o PHP entrega o que segurava, e esse texto já veio pela saída.
        _heldOutput = "";
        _pageOutputStarted = false;
        RefreshVariableRows();
        UpdateDebugButtons();
    }

    public void EndDebug()
    {
        if (_debug is null)
            return;
        _debug.Dispose();
        _debug = null;
        _listeningToServer = false;
        _pause = null;
        _previousValues.Clear();
        _pageOutput.Clear();
        _heldOutput = "";
        _pageOutputStarted = false;
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
        else if (_pause is not null)
            ShowStatus("Esta linha não está dentro de uma função. Use Próxima ou Continuar.");
    }

    // Dica ao parar o mouse: só no arquivo da parada, onde os nomes valem. Null para deixar as outras dicas.
    public string? DebugTipAt(int offset)
    {
        // Vale o nível da pilha que está à vista: em quem chamou, os nomes são os de lá.
        if (SelectedFrame is not { } frame || ActiveDocument is not { } document
            || !string.Equals(document.FullPath, frame.Path, StringComparison.OrdinalIgnoreCase))
            return null;
        var line = document.Document.GetLineByOffset(offset);
        return DebugValues.At(frame.Variables, document.Document.GetText(line), offset - line.Offset) is { } variable
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
        AddWatchButton.IsVisible = true;
        RefreshVariableRows();
    }

    private void HideVariables()
    {
        _showingVariables = false;
        VariablesTabButton.Classes.Set("on", false);
        AddWatchButton.IsVisible = false;
        VariablesList.IsVisible = false;
        VariablesHint.IsVisible = false;
        PageOutputBox.IsVisible = false;
        FramesBox.IsVisible = false;
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

        // As vigiadas vêm primeiro, calculadas onde o programa parou.
        foreach (var expression in _watches)
        {
            var result = _pause?.Watches?.FirstOrDefault(watch => watch.Name == expression)
                ?? new DebugVariable(expression, "vigiar:" + expression, "", _pause is null ? "aparece na próxima parada" : "…", []);
            AddVariableRows([result], 0, highlight: false, watch: true);
        }
        var watchRows = _variableRows.Count;

        if (SelectedFrame is { } frame)
        {
            // Na primeira parada da página tudo é novidade, e pintar tudo não diz nada. O que mudou
            // só faz sentido onde o programa está, e não em quem chamou.
            AddVariableRows(frame.Variables, 0, highlight: _previousValues.Count > 0 && _frameIndex == 0);
        }

        RefreshFrameRows();
        if (!_showingVariables)
            return;
        UpdatePageOutput();
        VariablesList.IsVisible = _variableRows.Count > 0;
        VariablesHint.IsVisible = _variableRows.Count == watchRows;
        VariablesHint.Text = _pause is not null
            ? "Nenhuma variável nesta linha ainda."
            : "As variáveis aparecem aqui quando o programa parar em uma linha marcada. Abra a página no navegador.";
    }

    private void AddVariableRows(IEnumerable<DebugVariable> variables, int depth, bool highlight, bool watch = false)
    {
        foreach (var variable in variables)
        {
            var changed = highlight && (!_previousValues.TryGetValue(variable.FullName, out var old) || old != variable.Value);
            var expanded = _expandedVariables.Contains(variable.FullName);
            var chevron = variable.Children.Count == 0 ? null : expanded ? ChevronOpen : ChevronClosed;
            _variableRows.Add(new VariableRow(variable.Name, variable.Value, variable.Type, new Thickness(depth * VariableIndent, 0, 0, 0), chevron,
                changed ? Palette.Current.DiffAdded : Brushes.Transparent, variable, watch));
            if (expanded)
                AddVariableRows(variable.Children, depth + 1, highlight);
        }
    }
}
