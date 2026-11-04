using System.Text;
using Avalonia.Threading;

namespace Sirius.Terminal;

// Liga um programa em execução à sua tela. Tudo aqui acontece na thread da interface.
public sealed class TerminalSession : IDisposable
{
    private readonly ConPtySession _pty;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private readonly Dispatcher _uiDispatcher = Dispatcher.UIThread;
    private bool _flushScheduled;

    private TerminalSession(ConPtySession pty, TerminalBuffer buffer)
    {
        _pty = pty;
        Buffer = buffer;
        Buffer.Reply += _pty.Write;
        _pty.Output += OnOutput;
        _pty.Exited += OnExited;
    }

    // Chame depois de se inscrever em Exited, para não perder o fim de um programa rápido.
    public void Begin() => _pty.Begin();

    public TerminalBuffer Buffer { get; }
    public bool IsRunning { get; private set; } = true;
    public int? ExitCode { get; private set; }

    public event Action? Changed;
    public event Action<int>? Exited;

    public static TerminalSession Start(string commandLine, string workingDirectory, int columns, int rows)
    {
        var buffer = new TerminalBuffer(columns, rows);
        return new TerminalSession(ConPtySession.Start(commandLine, workingDirectory, columns, rows), buffer);
    }

    public void Write(string text)
    {
        if (IsRunning)
            _pty.Write(text);
    }

    public void Resize(int columns, int rows)
    {
        Buffer.Resize(columns, rows);
        if (IsRunning)
            _pty.Resize(columns, rows);
    }

    public void Kill() => _pty.Kill();

    public void Dispose() => _pty.Dispose();

    private void OnOutput(string text)
    {
        lock (_gate)
        {
            _pending.Append(text);
            if (_flushScheduled)
                return;
            _flushScheduled = true;
        }
        _uiDispatcher.Post(Flush);
    }

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }
        if (text.Length == 0)
            return;
        Buffer.Feed(text);
        Changed?.Invoke();
    }

    private void OnExited(int code)
    {
        _uiDispatcher.Post(() =>
        {
            Flush();
            IsRunning = false;
            ExitCode = code;
            Exited?.Invoke(code);
            Changed?.Invoke();
        });
    }
}
