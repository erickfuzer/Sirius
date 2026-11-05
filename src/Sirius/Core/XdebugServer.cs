using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using Sirius.Lsp;

namespace Sirius.Core;

// Fullname é o nome completo para achar no PHP, como $pessoa['nome'].
public sealed record DebugVariable(string Name, string FullName, string Type, string Value, IReadOnlyList<DebugVariable> Children);

public sealed record DebugPause(string Path, int Line, IReadOnlyList<DebugVariable> Variables);

// O protocolo DBGp, que o Xdebug fala: é ele quem conecta aqui a cada página.
public static class DbgpParser
{
    private static readonly XNamespace Protocol = "urn:debugger_protocol_v1";
    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    // Só as que mudam o que a página faz. $_SERVER e $_ENV trazem dezenas de itens que atrapalham.
    private static readonly HashSet<string> UsefulSuperglobals = new(StringComparer.Ordinal)
    {
        "$_GET", "$_POST", "$_FILES", "$_COOKIE", "$_SESSION",
    };

    public static XElement Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), Safe);
        return XDocument.Load(reader).Root ?? throw new XmlException("Resposta vazia.");
    }

    public static List<DebugVariable> Variables(XElement response) =>
        response.Elements(Protocol + "property").Select(Variable).Where(v => v.Type != "uninitialized").ToList();

    public static IEnumerable<DebugVariable> Superglobals(XElement response) =>
        Variables(response).Where(v => UsefulSuperglobals.Contains(v.FullName) && v.Children.Count > 0);

    public static (string File, int Line)? TopFrame(XElement response)
    {
        var frame = response.Element(Protocol + "stack");
        return frame?.Attribute("filename")?.Value is { } file && int.TryParse(frame.Attribute("lineno")?.Value, out var line)
            ? (file, line)
            : null;
    }

    private static DebugVariable Variable(XElement property)
    {
        var fullName = (string?)property.Attribute("fullname") ?? "";
        var name = (string?)property.Attribute("name") ?? fullName;
        var type = (string?)property.Attribute("type") ?? "";
        var children = property.Elements(Protocol + "property").Select(Variable).Where(v => v.Type != "uninitialized").ToList();
        var count = int.TryParse((string?)property.Attribute("numchildren"), out var total) ? total : children.Count;
        var className = (string?)property.Attribute("classname");
        return new DebugVariable(name, fullName.Length > 0 ? fullName : name, type, Display(property, type, className, count), children);
    }

    private static string Display(XElement property, string type, string? className, int count)
    {
        // Só o texto direto: o de um array estaria nos filhos.
        var raw = string.Concat(property.Nodes().OfType<XText>().Select(text => text.Value));
        if ((string?)property.Attribute("encoding") == "base64")
        {
            try
            {
                raw = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim()));
            }
            catch (FormatException)
            {
                // Melhor mostrar o que veio do que derrubar a depuração por causa de um valor.
            }
        }

        return type switch
        {
            "string" => "'" + raw + "'",
            "bool" => raw == "1" ? "true" : "false",
            "null" => "null",
            "array" => $"array({count})",
            "object" => string.IsNullOrEmpty(className) ? "objeto" : className,
            _ => raw,
        };
    }
}

// Fica ouvindo só neste computador. O Xdebug conecta a cada página que começa.
public sealed class XdebugServer : IDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
    private readonly object _lock = new();
    private Dictionary<string, IReadOnlyCollection<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _paused;
    private TcpClient? _client;

    public int Port { get; private set; }
    public bool IsPaused => _paused;

    // Vêm de outra thread: quem desenha precisa passar para a da interface.
    public event Action<DebugPause>? Paused;
    public event Action? Resumed;
    public event Action? PageEnded;

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public void SetBreakpoints(IReadOnlyDictionary<string, IReadOnlyCollection<int>> breakpoints)
    {
        lock (_lock)
            _breakpoints = new Dictionary<string, IReadOnlyCollection<int>>(breakpoints, StringComparer.OrdinalIgnoreCase);
        // Com o programa rodando, o Xdebug não escuta: vale a partir da próxima parada.
        if (_paused)
            _commands.Writer.TryWrite("sync");
    }

    public void Continue() => Send("run");
    public void StepOver() => Send("step_over");

    private void Send(string command)
    {
        if (_paused)
            _commands.Writer.TryWrite(command);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _client = client;
                try
                {
                    await new Connection(this, client.GetStream()).RunAsync();
                }
                catch (Exception e) when (e is IOException or SocketException or XmlException or InvalidDataException or FormatException)
                {
                    // A página fechou no meio, como ao parar o servidor. A próxima conecta de novo.
                }
                _paused = false;
                _client = null;
                PageEnded?.Invoke();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Parou de ouvir porque a execução terminou.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _client?.Dispose();
        _commands.Writer.TryComplete();
    }

    private sealed class Connection(XdebugServer server, NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private readonly Dictionary<(string File, int Line), string> _applied = new();
        private int _bufferStart;
        private int _bufferEnd;
        private int _nextId = 1;

        private CancellationToken Cancel => server._stop.Token;

        public async Task RunAsync()
        {
            await ReadMessageAsync(); // A apresentação do Xdebug, sem nada a responder.

            await RequestAsync("feature_set -n max_depth -v 3");
            await RequestAsync("feature_set -n max_children -v 100");
            await RequestAsync("feature_set -n max_data -v 4096");
            await SyncBreakpointsAsync();

            var response = await RequestAsync("run");
            while ((string?)response.Attribute("status") == "break")
            {
                var pause = await ReadPauseAsync();
                await SyncBreakpointsAsync();
                if (pause is null)
                {
                    response = await RequestAsync("run");
                    continue;
                }

                while (server._commands.Reader.TryRead(out _))
                {
                    // Nada que chegou antes desta parada vale para ela.
                }
                server._paused = true;
                server.Paused?.Invoke(pause);

                var command = await NextCommandAsync();
                server._paused = false;
                server.Resumed?.Invoke();
                response = await RequestAsync(command);
            }

            // O programa acabou, mas o Xdebug segura a página até receber isto.
            await RequestAsync("stop");
        }

        private async Task<string> NextCommandAsync()
        {
            while (true)
            {
                var command = await server._commands.Reader.ReadAsync(Cancel);
                if (command != "sync")
                    return command;
                await SyncBreakpointsAsync();
            }
        }

        private async Task<DebugPause?> ReadPauseAsync()
        {
            if (DbgpParser.TopFrame(await RequestAsync("stack_get -d 0")) is not { } frame
                || !frame.File.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return null; // Código sem arquivo, como um eval: nada para mostrar.
            }

            var variables = DbgpParser.Variables(await RequestAsync("context_get -d 0 -c 0"));
            variables.AddRange(DbgpParser.Superglobals(await RequestAsync("context_get -d 0 -c 1")));
            return new DebugPause(LspParsers.ToPath(frame.File), frame.Line, variables);
        }

        private async Task SyncBreakpointsAsync()
        {
            Dictionary<string, IReadOnlyCollection<int>> desired;
            lock (server._lock)
                desired = server._breakpoints;

            var wanted = desired.SelectMany(pair => pair.Value.Select(line => (File: pair.Key, Line: line))).ToHashSet();
            foreach (var old in _applied.Keys.Where(key => !wanted.Contains(key)).ToList())
            {
                await RequestAsync($"breakpoint_remove -d {_applied[old]}");
                _applied.Remove(old);
            }

            foreach (var item in wanted.Where(key => !_applied.ContainsKey(key)))
            {
                var uri = new Uri(item.File).AbsoluteUri;
                var response = await RequestAsync($"breakpoint_set -t line -f {uri} -n {item.Line}");
                if ((string?)response.Attribute("id") is { } id)
                    _applied[item] = id;
            }
        }

        private async Task<XElement> RequestAsync(string command)
        {
            var parts = command.Split(' ', 2);
            var id = (_nextId++).ToString();
            var text = parts.Length == 1 ? $"{parts[0]} -i {id}\0" : $"{parts[0]} -i {id} {parts[1]}\0";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), Cancel);

            while (true)
            {
                var message = await ReadMessageAsync();
                // Avisos do Xdebug podem chegar antes da resposta, como a linha que um ponto assumiu.
                if (message.Name.LocalName == "response" && (string?)message.Attribute("transaction_id") == id)
                    return message;
            }
        }

        // Cada mensagem vem como: tamanho em texto, zero, o XML, zero.
        private async Task<XElement> ReadMessageAsync()
        {
            var length = 0;
            while (true)
            {
                var value = await ReadByteAsync();
                if (value == 0)
                    break;
                if (value is < (byte)'0' or > (byte)'9' || length > MaxMessageBytes / 10)
                    throw new InvalidDataException("Mensagem inválida do Xdebug.");
                length = length * 10 + (value - '0');
            }
            if (length > MaxMessageBytes)
                throw new InvalidDataException("Mensagem grande demais do Xdebug.");

            var content = new byte[length];
            for (var i = 0; i < length; i++)
                content[i] = await ReadByteAsync();
            if (await ReadByteAsync() != 0)
                throw new InvalidDataException("Mensagem inválida do Xdebug.");
            return DbgpParser.Parse(Encoding.UTF8.GetString(content));
        }

        private async ValueTask<byte> ReadByteAsync()
        {
            if (_bufferStart == _bufferEnd)
            {
                _bufferStart = 0;
                _bufferEnd = await stream.ReadAsync(_buffer, Cancel);
                if (_bufferEnd == 0)
                    throw new IOException("O Xdebug fechou a conexão.");
            }
            return _buffer[_bufferStart++];
        }
    }
}
