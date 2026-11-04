using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sirius.Core;
using Sirius.Terminal;
using Sirius.Theming;

namespace Sirius;

// A cápsula que se abre em terminal, e o botão Executar.
public partial class MainWindow
{
    private const double CapsuleClosedWidth = 520;
    private const double CapsuleOpenWidth = 1040;
    private const double CapsuleOpenHeight = 340;
    private const double CapsuleClosedHeight = 40;
    private const double CapsuleGap = 24;
    private const double TerminalSideMargins = 24;
    private const double TerminalChromeHeight = 82;
    private const int StopGraceMilliseconds = 1500;

    private TerminalSession? _shell;
    private TerminalSession? _task;
    private TaskCompletionSource<int>? _taskFinished;
    private string? _taskCommand;
    private bool _terminalOpen;

    public bool IsTerminalOpen => _terminalOpen;
    public bool IsTaskRunning => _task is { IsRunning: true };
    public TerminalSession? VisibleSession => TerminalHost.Session;
    public Task<int> TaskFinished => _taskFinished?.Task ?? Task.FromResult(0);
    public string RunLabelText => RunLabel.Text ?? "";

    private void InitializeTerminal()
    {
        SheetHost.SizeChanged += (_, _) => UpdateCapsuleSize();
        TerminalHost.ResolveLink = path => ErrorLinks.Resolve(path, RootFolder);
        TerminalHost.LinkActivated += GoToLocation;
    }

    private void UpdateCapsuleSize()
    {
        var available = Math.Max(0, SheetHost.Bounds.Width - CapsuleGap);
        var tallest = Math.Max(CapsuleClosedHeight, SheetHost.Bounds.Height - CapsuleGap - 48);
        var width = Math.Min(_terminalOpen ? CapsuleOpenWidth : CapsuleClosedWidth, available);
        var height = _terminalOpen ? Math.Min(CapsuleOpenHeight, tallest) : CapsuleClosedHeight;
        Capsule.Width = width;
        Capsule.Height = height;

        // O editor encolhe para o terminal não esconder o código.
        CodeEditor.Margin = _terminalOpen ? new Thickness(0, 0, 0, height + CapsuleGap) : default;

        // O terminal já nasce no tamanho final, e a animação só o revela. Assim os
        // programas não recebem um novo tamanho a cada quadro.
        if (_terminalOpen)
        {
            TerminalHost.Width = Math.Max(0, width - TerminalSideMargins);
            TerminalHost.Height = Math.Max(0, height - TerminalChromeHeight);
            TerminalHost.SyncSize();
        }
    }

    private void OnToggleTerminal(object? sender, RoutedEventArgs e) => ToggleTerminal();

    public void ToggleTerminal()
    {
        if (_terminalOpen)
            CloseTerminal();
        else
            ShowShell();
    }

    public void CloseTerminal()
    {
        SetTerminalOpen(false);
        if (ActiveDocument is not null)
            CodeEditor.TextArea.Focus();
    }

    private void SetTerminalOpen(bool open)
    {
        _terminalOpen = open;
        TerminalArea.IsVisible = open;
        TerminalButton.Classes.Set("on", open);
        UpdateCapsuleSize();
    }

    private void OnShowShell(object? sender, RoutedEventArgs e) => ShowShell();

    private void OnShowTask(object? sender, RoutedEventArgs e) => ShowSession(_task);

    public void ShowShell()
    {
        if (_shell is not { IsRunning: true })
        {
            _shell?.Dispose();
            _shell = null;
            SetTerminalOpen(true);
            try
            {
                _shell = TerminalSession.Start(RunDetector.FindShell(), WorkingFolder(), TerminalHost.GridColumns, TerminalHost.GridRows);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                SetTerminalOpen(false);
                ShowStatus("Não foi possível abrir o terminal.");
                return;
            }
        }
        ShowSession(_shell);
    }

    private void ShowSession(TerminalSession? session)
    {
        if (session is null)
            session = _shell;
        if (session is null)
        {
            ShowShell();
            return;
        }

        SetTerminalOpen(true);
        TerminalHost.Session = session;
        TaskTabButton.IsVisible = _task is not null;
        TaskTabButton.Classes.Set("on", session == _task);
        ShellTabButton.Classes.Set("on", session == _shell);
        UpdateTerminalInfo();
        Dispatcher.UIThread.Post(() => TerminalHost.Focus());
    }

    private string WorkingFolder() =>
        RootFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Executar

    private void OnRunClick(object? sender, RoutedEventArgs e) => _ = RunOrStopAsync();

    public async Task RunOrStopAsync()
    {
        if (IsTaskRunning)
            StopTask();
        else
            await RunProjectAsync();
    }

    public string? RunCommandForProject()
    {
        if (RootFolder is null)
            return null;
        return _settings.RunCommands.TryGetValue(RootFolder, out var saved)
            ? saved
            : RunDetector.Detect(RootFolder, ActiveDocument?.FullPath);
    }

    public async Task RunProjectAsync()
    {
        if (RootFolder is null)
        {
            ShowStatus("Abra uma pasta para executar.");
            return;
        }

        var command = RunCommandForProject() ?? await AskRunCommandAsync();
        if (command is null)
            return;

        // O programa precisa rodar com o que está na tela, não com a versão antiga do disco.
        foreach (var document in _documents.Where(d => d.IsDirty).ToList())
        {
            if (!await SaveAsync(document))
                return;
        }

        _task?.Dispose();
        _task = null;
        var wasOpen = _terminalOpen;
        SetTerminalOpen(true);
        try
        {
            _task = TerminalSession.Start(RunDetector.WrapForShell(command), RootFolder, TerminalHost.GridColumns, TerminalHost.GridRows);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            SetTerminalOpen(wasOpen);
            ShowStatus("Não foi possível iniciar este comando.");
            return;
        }

        var session = _task;
        _taskCommand = command;
        _taskFinished = new TaskCompletionSource<int>();
        session.Exited += code => OnTaskExited(session, code);
        UpdateRunButton();
        ShowSession(session);
    }

    private void OnTaskExited(TerminalSession session, int code)
    {
        session.Buffer.Feed($"\r\n\x1b[2mProcesso encerrado com código {code}.\x1b[0m\r\n");
        if (session != _task)
            return;

        UpdateRunButton();
        UpdateTerminalInfo();
        ShowStatus(code == 0 ? "Concluído" : $"Terminou com erro (código {code})");
        _taskFinished?.TrySetResult(code);
    }

    public void StopTask()
    {
        if (_task is not { IsRunning: true } session)
            return;

        // Primeiro pede com jeito, como o Ctrl C; se o programa ignorar, encerra à força.
        session.Write("\x03");
        DispatcherTimer.RunOnce(() =>
        {
            if (session.IsRunning)
                session.Kill();
        }, TimeSpan.FromMilliseconds(StopGraceMilliseconds));
    }

    private void UpdateRunButton()
    {
        var running = IsTaskRunning;
        RunLabel.Text = running ? "Parar" : "Executar";
        RunIcon.IsVisible = !running;
        StopIcon.IsVisible = running;
    }

    private void UpdateTerminalInfo()
    {
        TerminalInfo.Text = TerminalHost.Session == _task && _task is not null ? _taskCommand : "";
    }

    public async Task AskAndSaveRunCommandAsync()
    {
        if (RootFolder is { } root && await AskRunCommandAsync(RunCommandForProject() ?? "") is { } command)
        {
            _settings.RunCommands[root] = command;
            _settings.Save(_settingsPath);
        }
    }

    private Task<string?> AskRunCommandAsync(string initial = "") =>
        AskTextAsync("Como executar este projeto?", "Digite o comando, como npm run dev ou python main.py.", "Salvar",
            text => string.IsNullOrWhiteSpace(text) ? "Digite um comando." : null, initial);

    // Abre o arquivo de um erro do terminal na linha indicada.
    public void GoToLocation(string path, int line, int column)
    {
        OpenFile(path);
        if (!string.Equals(ActiveDocument?.FullPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        var document = ActiveDocument!.Document;
        var target = document.GetLineByNumber(Math.Clamp(line, 1, document.LineCount));
        CodeEditor.TextArea.ClearSelection();
        CodeEditor.TextArea.Caret.Offset = target.Offset + Math.Min(column - 1, target.Length);
        var lineNumber = target.LineNumber;
        Dispatcher.UIThread.Post(() => CodeEditor.ScrollToLine(lineNumber), DispatcherPriority.Loaded);
    }

    private void StopAllSessions()
    {
        _task?.Dispose();
        _shell?.Dispose();
        _task = null;
        _shell = null;
        _taskCommand = null;
        _taskFinished?.TrySetResult(-1);
        TerminalHost.Session = null;
        UpdateRunButton();
        SetTerminalOpen(false);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopAllSessions();
    }
}
