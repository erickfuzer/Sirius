using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Sirius.Lsp;

public sealed class LspException(string message) : Exception(message);

// Conversa com um servidor de linguagem: mensagens JSON precedidas do tamanho em bytes.
public sealed class LspConnection : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private bool _closed;

    public LspConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    // Os eventos chegam na thread de leitura.
    public event Action<string, JsonElement>? NotificationReceived;
    public event Action? Closed;

    // Responde aos pedidos que o servidor faz para nós.
    public Func<string, JsonElement, object?> RequestHandler { get; set; } = DefaultRequestHandler;

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "sirius-lsp-read" }.Start();
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (_closed)
            completion.TrySetException(new LspException("O servidor foi encerrado."));

        Send(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        await using (cancellation.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed))
            {
                removed.TrySetCanceled(cancellation);
                Notify("$/cancelRequest", new { id });
            }
        }))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    public void Notify(string method, object? parameters) =>
        Send(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
    }

    private static object? DefaultRequestHandler(string method, JsonElement parameters)
    {
        // Sem configurações próprias: cada item pedido recebe null, e o servidor usa o padrão dele.
        if (method == "workspace/configuration" && parameters.TryGetProperty("items", out var items))
            return new object?[items.GetArrayLength()];
        return null;
    }

    private void Send(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeGate)
        {
            if (_closed)
                return;
            try
            {
                _output.Write(header);
                _output.Write(body);
                _output.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // A thread de leitura percebe o fim e avisa quem estiver esperando.
            }
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (ReadMessage() is { } body)
            {
                using var document = JsonDocument.Parse(body);
                Dispatch(document.RootElement);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or JsonException or FormatException)
        {
            // Servidor encerrado ou mensagem inválida: tratamos os dois como fim da conexão.
        }

        _closed = true;
        foreach (var pending in _pending.Values)
            pending.TrySetException(new LspException("O servidor foi encerrado."));
        _pending.Clear();
        Closed?.Invoke();
    }

    private byte[]? ReadMessage()
    {
        var length = -1;
        var line = new StringBuilder();
        while (true)
        {
            var value = _input.ReadByte();
            if (value < 0)
                return null;
            if (value != '\n')
            {
                if (value != '\r')
                    line.Append((char)value);
                continue;
            }

            if (line.Length == 0)
                break;
            var text = line.ToString();
            const string prefix = "Content-Length:";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                length = int.Parse(text[prefix.Length..].Trim());
            line.Clear();
        }

        if (length < 0)
            return null;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = _input.Read(body, read, length - read);
            if (count <= 0)
                return null;
            read += count;
        }
        return body;
    }

    private void Dispatch(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var id);
        var hasMethod = message.TryGetProperty("method", out var method);
        var parameters = message.TryGetProperty("params", out var value) ? value.Clone() : default;

        if (hasId && hasMethod)
        {
            object? result = null;
            try
            {
                result = RequestHandler(method.GetString()!, parameters);
            }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or JsonException)
            {
                // Um pedido que não soubemos responder recebe null e a conversa continua.
            }
            Send(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id.Clone(), ["result"] = result });
        }
        else if (hasMethod)
        {
            NotificationReceived?.Invoke(method.GetString()!, parameters);
        }
        else if (hasId && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt64(), out var pending))
        {
            if (message.TryGetProperty("error", out var error))
                pending.TrySetException(new LspException(error.TryGetProperty("message", out var text) ? text.GetString() ?? "Erro do servidor." : "Erro do servidor."));
            else
                pending.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }
}
