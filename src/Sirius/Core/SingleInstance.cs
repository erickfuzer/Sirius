using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Sirius.Core;

// "Abrir com Sirius" num arquivo, com a Sirius já aberta: a nova entrega o caminho à que já está aberta
// e fecha. O canal é um pipe do Windows que só a mesma pessoa, na mesma sessão, consegue usar.
public sealed class InstanceServer : IDisposable
{
    private const int MaxPaths = 64;
    private const int MaxPathLength = 32 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IReadOnlyList<string>> _waiting = new();
    private readonly object _lock = new();
    private Action<IReadOnlyList<string>>? _received;

    public InstanceServer(string name) => _name = name;

    // Uma por pessoa e por sessão do Windows, como numa área de trabalho remota.
    public static string DefaultName
    {
        get
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var user = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value : null;
            return $"Sirius-{user ?? Environment.UserName}-{process.SessionId}";
        }
    }

    public bool IsServing { get; private set; }

    // O que chegar antes de haver janela fica guardado e é entregue quando alguém passa a ouvir.
    // Chega numa thread de fundo.
    public void OnReceived(Action<IReadOnlyList<string>> received)
    {
        List<IReadOnlyList<string>> waiting;
        lock (_lock)
        {
            _received = received;
            waiting = _waiting.ToList();
            _waiting.Clear();
        }
        foreach (var paths in waiting)
            received(paths);
    }

    // Só uma Sirius consegue criar o primeiro pipe com este nome: ela passa a receber os arquivos.
    public bool TryStart()
    {
        if (IsServing)
            return true;
        NamedPipeServerStream first;
        try
        {
            first = Create(firstInstance: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        IsServing = true;
        _ = Task.Run(() => ListenAsync(first));
        return true;
    }

    private NamedPipeServerStream Create(bool firstInstance) => new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private async Task ListenAsync(NamedPipeServerStream server)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                // O próximo é criado antes de atender: o nome nunca fica livre para outra Sirius tomar.
                var next = Create(firstInstance: false);
                _ = HandleAsync(server);
                server = next;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                break;
            }
        }
        await server.DisposeAsync();
    }

    private async Task HandleAsync(NamedPipeServerStream stream)
    {
        var paths = new List<string>();
        try
        {
            await using (stream)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(ReadTimeout);
                var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                while (paths.Count < MaxPaths && await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                {
                    // Só caminhos completos: nada aqui é interpretado como comando.
                    if (line.Length <= MaxPathLength && Path.IsPathFullyQualified(line))
                        paths.Add(line);
                }
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 64, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("ok".AsMemory(), timeout.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Quem mandou desistiu no meio. O que já chegou ainda vale.
        }

        if (paths.Count == 0)
            return;
        Action<IReadOnlyList<string>>? received;
        lock (_lock)
        {
            received = _received;
            if (received is null)
                _waiting.Add(paths);
        }
        received?.Invoke(paths);
    }

    public void Dispose() => _stop.Cancel();
}

public static class InstanceClient
{
    // Qualquer processo pode trazer uma janela para a frente se quem está na frente deixar.
    // Quem é aberto pelo Explorer está na frente, e deixa a Sirius aberta subir.
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private const int AnyProcess = -1;

    // Devolve true se a Sirius aberta recebeu os caminhos. Senão, quem chamou abre a própria janela.
    public static bool TrySend(string name, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            if (OperatingSystem.IsWindows())
                AllowSetForegroundWindow(AnyProcess);

            using var cancel = new CancellationTokenSource(timeout);
            var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            foreach (var path in paths)
                writer.WriteLineAsync(path.AsMemory(), cancel.Token).GetAwaiter().GetResult();
            writer.WriteLineAsync("".AsMemory(), cancel.Token).GetAwaiter().GetResult();
            var reader = new StreamReader(client, new UTF8Encoding(false), false, 64, leaveOpen: true);
            return reader.ReadLineAsync(cancel.Token).AsTask().GetAwaiter().GetResult() == "ok";
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }
}
