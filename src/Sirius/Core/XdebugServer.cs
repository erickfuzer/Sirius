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

// Um nível da pilha de chamadas: onde está e as variáveis dali. Name é o que a pessoa lê, como "soma()".
public sealed record DebugStackFrame(string Path, int Line, string Name, IReadOnlyList<DebugVariable> Variables);

// Function é null no corpo principal do arquivo. Depth diz quantas funções há até aqui, contando o corpo principal.
// HeldOutput é o que a página escreveu e o PHP ainda segura, por causa do output_buffering.
// Stack começa onde o programa parou e segue por quem chamou, até o arquivo principal.
public sealed record DebugPause(string Path, int Line, IReadOnlyList<DebugVariable> Variables, string? Function = null, int Depth = 1, string HeldOutput = "",
    IReadOnlyList<DebugStackFrame>? Stack = null)
{
    public IReadOnlyList<DebugStackFrame> Frames =>
        Stack is { Count: > 0 } stack ? stack : [new DebugStackFrame(Path, Line, DbgpParser.FrameName(Function), Variables)];
}

public readonly record struct DebugFrame(string File, int Line, string? Function, int Depth);

// Um nível como o Xdebug descreve: Where é "{main}", o nome da função ou "{closure:...}".
public readonly record struct DbgpFrame(string File, int Line, string? Where, int Level);

// O valor sob o mouse, a partir do texto da linha.
public static partial class DebugValues
{
    private const int MaxTipItems = 12;

    [System.Text.RegularExpressions.GeneratedRegex(@"\$[A-Za-z_]\w*(?:\[(?:'[^']*'|""[^""]*""|\d+)\]|->[A-Za-z_]\w*)*")]
    private static partial System.Text.RegularExpressions.Regex Chain();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:'[^']*'|""[^""]*""|\d+)\]|->[A-Za-z_]\w*")]
    private static partial System.Text.RegularExpressions.Regex Step();

    // Em $a['b']['c'], o mouse no $a mostra $a, e no 'c' mostra o item inteiro até ali.
    public static DebugVariable? At(IReadOnlyList<DebugVariable> variables, string line, int column)
    {
        foreach (System.Text.RegularExpressions.Match chain in Chain().Matches(line))
        {
            if (column < chain.Index || column >= chain.Index + chain.Length)
                continue;

            var end = chain.Length;
            var relative = column - chain.Index;
            var first = Step().Match(chain.Value);
            if (first.Success && relative < first.Index)
                end = first.Index;
            else
            {
                foreach (System.Text.RegularExpressions.Match step in Step().Matches(chain.Value))
                {
                    if (relative >= step.Index && relative < step.Index + step.Length)
                    {
                        end = step.Index + step.Length;
                        break;
                    }
                }
            }

            // O Xdebug sempre escreve as chaves com aspas duplas.
            return Find(variables, chain.Value[..end].Replace('\'', '"'));
        }
        return null;
    }

    private static DebugVariable? Find(IEnumerable<DebugVariable> variables, string fullName)
    {
        foreach (var variable in variables)
        {
            if (variable.FullName == fullName)
                return variable;
            if (Find(variable.Children, fullName) is { } child)
                return child;
        }
        return null;
    }

    public static string Describe(DebugVariable variable)
    {
        var text = new StringBuilder($"{variable.FullName} = {variable.Value}");
        foreach (var child in variable.Children.Take(MaxTipItems))
            text.Append('\n').Append(child.Name).Append(": ").Append(child.Value);
        if (variable.Children.Count > MaxTipItems)
            text.Append('\n').Append($"E mais {variable.Children.Count - MaxTipItems}");
        return text.ToString();
    }
}

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

    // A resposta de um "eval" que devolve texto. Sem buffer, o PHP devolve false, e aí não há o que mostrar.
    public static string EvalText(XElement response)
    {
        if (response.Element(Protocol + "property") is not { } property || (string?)property.Attribute("type") != "string")
            return "";
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(property.Value.Trim()));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    // Lê a saída de "stack_get": o primeiro é onde o programa está, e os outros são quem chamou.
    public static DebugFrame? TopFrame(XElement response)
    {
        var frames = response.Elements(Protocol + "stack").ToList();
        if (frames.Count == 0
            || frames[0].Attribute("filename")?.Value is not { } file
            || !int.TryParse(frames[0].Attribute("lineno")?.Value, out var line))
        {
            return null;
        }

        var where = (string?)frames[0].Attribute("where");
        return new DebugFrame(file, line, where is null || where.StartsWith('{') ? null : where, frames.Count);
    }

    // Todos os níveis da pilha, do mais recente ao arquivo principal.
    public static List<DbgpFrame> Frames(XElement response)
    {
        var frames = new List<DbgpFrame>();
        foreach (var (stack, index) in response.Elements(Protocol + "stack").Select((s, i) => (s, i)))
        {
            if (stack.Attribute("filename")?.Value is not { } file || !int.TryParse(stack.Attribute("lineno")?.Value, out var line))
                continue;
            var level = int.TryParse(stack.Attribute("level")?.Value, out var value) ? value : index;
            frames.Add(new DbgpFrame(file, line, (string?)stack.Attribute("where"), level));
        }
        return frames;
    }

    // O nome que a pessoa lê na lista. Métodos chegam como Classe->metodo ou Classe::metodo.
    public static string FrameName(string? where) => where switch
    {
        null or "{main}" => "Arquivo principal",
        _ when where.StartsWith("{closure", StringComparison.Ordinal) => "Função sem nome",
        _ when where.StartsWith('{') => where.Trim('{', '}'),
        "include" or "include_once" or "require" or "require_once" => where,
        _ => where + "()",
    };

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
    private const int MaxStackFrames = 20;
    private static readonly string ReadHeldOutput = Convert.ToBase64String(Encoding.UTF8.GetBytes("ob_get_contents()"));

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
    private readonly object _lock = new();
    private Dictionary<string, IReadOnlyCollection<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, IReadOnlyDictionary<int, string>> _conditions = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _paused;
    private TcpClient? _client;

    public int Port { get; private set; }
    public bool IsPaused => _paused;

    // Vêm de outra thread: quem desenha precisa passar para a da interface.
    public event Action<DebugPause>? Paused;
    public event Action? Resumed;
    public event Action? PageEnded;
    public event Action<string>? OutputWritten;

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

    // Pontos que só param quando a condição em PHP é verdadeira, como $i == 5. Arquivo, linha e condição.
    public void SetConditions(IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> conditions)
    {
        lock (_lock)
            _conditions = new Dictionary<string, IReadOnlyDictionary<int, string>>(conditions, StringComparer.OrdinalIgnoreCase);
        if (_paused)
            _commands.Writer.TryWrite("sync");
    }

    public void Continue() => Send("run");
    public void StepOver() => Send("step_over");
    public void StepInto() => Send("step_into");
    public void StepOut() => Send("step_out");

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
        // A condição faz parte da chave: trocá-la apaga o ponto antigo e cria outro.
        private readonly Dictionary<(string File, int Line, string Condition), string> _applied = new();
        // Um acento pode vir partido entre dois pedaços da saída.
        private readonly Decoder _output = Encoding.UTF8.GetDecoder();
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
            // Copia: a saída continua indo para o navegador normalmente.
            await RequestAsync("stdout -c 1");
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
            var stack = await RequestAsync("stack_get");
            if (DbgpParser.TopFrame(stack) is not { } frame
                || !IsFile(frame.File))
            {
                return null; // Código sem arquivo, como um eval: nada para mostrar.
            }

            var variables = DbgpParser.Variables(await RequestAsync("context_get -d 0 -c 0"));
            // As superglobais são as mesmas em todos os níveis da pilha.
            var globals = DbgpParser.Superglobals(await RequestAsync("context_get -d 0 -c 1")).ToList();
            variables.AddRange(globals);

            // Cada nível de quem chamou, com as variáveis de lá. Uma recursão funda para no limite.
            var frames = new List<DebugStackFrame>();
            foreach (var level in DbgpParser.Frames(stack).Where(f => IsFile(f.File)).Take(MaxStackFrames))
            {
                var locals = level.Level == 0
                    ? variables
                    : DbgpParser.Variables(await RequestAsync($"context_get -d {level.Level} -c 0")).Concat(globals).ToList();
                frames.Add(new DebugStackFrame(LspParsers.ToPath(level.File), level.Line, DbgpParser.FrameName(level.Where), locals));
            }

            // Só lê o buffer, sem esvaziar: mudar o output_buffering faria header() se comportar diferente aqui.
            var held = DbgpParser.EvalText(await RequestAsync("eval -- " + ReadHeldOutput));
            return new DebugPause(LspParsers.ToPath(frame.File), frame.Line, variables, frame.Function, frame.Depth, held, frames);
        }

        private static bool IsFile(string uri) => uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

        private async Task SyncBreakpointsAsync()
        {
            Dictionary<string, IReadOnlyCollection<int>> desired;
            Dictionary<string, IReadOnlyDictionary<int, string>> conditions;
            lock (server._lock)
                (desired, conditions) = (server._breakpoints, server._conditions);

            string ConditionOf(string file, int line) =>
                conditions.TryGetValue(file, out var lines) && lines.TryGetValue(line, out var condition) ? condition.Trim() : "";

            var wanted = desired.SelectMany(pair => pair.Value.Select(line => (File: pair.Key, Line: line, Condition: ConditionOf(pair.Key, line)))).ToHashSet();
            foreach (var old in _applied.Keys.Where(key => !wanted.Contains(key)).ToList())
            {
                await RequestAsync($"breakpoint_remove -d {_applied[old]}");
                _applied.Remove(old);
            }

            foreach (var item in wanted.Where(key => !_applied.ContainsKey(key)))
            {
                var uri = new Uri(item.File).AbsoluteUri;
                // A condição vai em base64, como pede o protocolo: aspas e espaços passam sem estragar o comando.
                var command = item.Condition.Length == 0
                    ? $"breakpoint_set -t line -f {uri} -n {item.Line}"
                    : $"breakpoint_set -t conditional -f {uri} -n {item.Line} -- {Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Condition))}";
                var response = await RequestAsync(command);
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
                if (message.Name.LocalName == "stream" && (string?)message.Attribute("type") == "stdout")
                    ReportOutput(message.Value);
            }
        }

        private void ReportOutput(string base64)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                return; // Perder um pedaço da saída não pode derrubar a depuração.
            }

            var chars = new char[_output.GetCharCount(bytes, flush: false)];
            var count = _output.GetChars(bytes, chars, flush: false);
            if (count > 0)
                server.OutputWritten?.Invoke(new string(chars, 0, count));
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
