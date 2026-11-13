using System.Net;
using System.Text;

namespace Sirius.Core;

// O navegador que recarrega sozinho, como o Live Server do VS Code. Um pequeno servidor da Sirius, só no
// próprio computador, fica entre o navegador e o site: repassa as páginas do servidor de verdade (o do Executar
// ou o Apache), ou serve os arquivos da pasta quando é só HTML, e põe no fim de cada página um script que
// escuta a Sirius. Quando um arquivo do projeto muda, a página recarrega; se só mudou CSS, troca só o estilo.
public sealed class LiveServer : IDisposable
{
    public const int FirstPort = 5500;
    public const string ScriptPath = "/__sirius/recarga.js";
    public const string EventsPath = "/__sirius/eventos";

    // O que muda o que se vê na página. Imagens e dados ficam de fora: uma página que grava um envio ou um
    // .json a cada visita recarregaria sem parar.
    private static readonly HashSet<string> ReloadExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".php", ".css", ".js", ".mjs" };

    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length", "Host", "Accept-Encoding", "Expect",
    };

    // O Apache manda o CSS sem dizer por quanto tempo vale, e o navegador o guarda: um F5 comum mostrava o CSS
    // velho, e só o Ctrl F5 buscava de novo (visto no access.log do dono). Pela Sirius, tudo é conferido de novo
    // a cada recarga; o ETag e o Last-Modified ficam, então o que não mudou volta com um 304 rápido.
    private static readonly HashSet<string> CacheHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Cache-Control", "Expires", "Pragma" };

    private const string Script = """
        (function () {
          if (window.__siriusRecarga) return;
          window.__siriusRecarga = true;
          var atual = document.currentScript;
          var depoisDeEnviar = atual && atual.getAttribute('data-post') === '1';
          var eventos = new EventSource('/__sirius/eventos');
          eventos.onmessage = function (e) {
            if (e.data === 'css') {
              var links = document.querySelectorAll('link[rel~="stylesheet"]');
              for (var i = 0; i < links.length; i++) {
                var url = new URL(links[i].href, location.href);
                if (url.origin !== location.origin) continue;
                url.searchParams.set('sirius', Date.now());
                links[i].href = url.href;
              }
            } else if (depoisDeEnviar) {
              location.href = location.href;
            } else {
              location.reload();
            }
          };
        })();
        """;

    private readonly HttpClient _client;
    private readonly object _gate = new();
    private readonly List<HttpListenerResponse> _pages = new();
    private readonly Timer _heartbeat;
    private readonly Timer _debounce;
    private HttpListener? _listener;
    private FileSystemWatcher? _watcher;
    private string? _watchedRoot;
    private bool _pendingFull;
    private bool _pending;
    private volatile string? _upstream;
    private volatile string? _staticRoot;

    public LiveServer()
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            // Uma página parada num ponto da depuração espera o tempo que for preciso.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _heartbeat = new Timer(_ => Send(": sirius\n\n"), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        _debounce = new Timer(_ => FlushChanges(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Port { get; private set; }
    public string Origin => $"http://localhost:{Port}";
    public bool IsRunning => _listener?.IsListening == true;
    public TimeSpan ChangeDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    public int ConnectedPages
    {
        get { lock (_gate) return _pages.Count; }
    }

    // De onde vêm as páginas: o endereço de um servidor, como http://localhost:8000, ou nulo para servir a pasta.
    public string? Upstream
    {
        get => _upstream;
        set => _upstream = value?.TrimEnd('/');
    }

    public string? StaticRoot
    {
        get => _staticRoot;
        set => _staticRoot = value is null ? null : Path.GetFullPath(value);
    }

    // A primeira porta livre a partir da 5500, a mesma do Live Server. Só aceita o próprio computador.
    public void Start(int firstPort = FirstPort)
    {
        if (IsRunning)
            return;
        for (var port = firstPort; port < firstPort + 100; port++)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
            }
            catch (Exception e) when (e is HttpListenerException or ArgumentException)
            {
                listener.Close();
                continue;
            }
            _listener = listener;
            Port = port;
            _ = AcceptLoopAsync(listener);
            return;
        }
        throw new InvalidOperationException("Nenhuma porta livre para o navegador.");
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == ScriptPath)
            {
                await WriteAsync(context.Response, 200, "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(Script));
                return;
            }
            if (path == EventsPath)
            {
                OpenEvents(context.Response);
                return;
            }
            if (Upstream is { } upstream)
                await ProxyAsync(context, upstream);
            else
                await ServeFileAsync(context);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { context.Response.Abort(); } catch (Exception) { }
        }
    }

    // Repassar ao servidor de verdade

    private async Task ProxyAsync(HttpListenerContext context, string upstream)
    {
        var incoming = context.Request;
        using var request = new HttpRequestMessage(new HttpMethod(incoming.HttpMethod), upstream + incoming.RawUrl);
        if (incoming.HasEntityBody)
            request.Content = new StreamContent(incoming.InputStream);
        foreach (string? name in incoming.Headers)
        {
            if (name is null || HopHeaders.Contains(name))
                continue;
            var values = incoming.Headers.GetValues(name) ?? [];
            if (!request.Headers.TryAddWithoutValidation(name, values))
                request.Content?.Headers.TryAddWithoutValidation(name, values);
        }
        // O endereço da Sirius segue para o site: um PHP que monta links com o HTTP_HOST continua por aqui.
        request.Headers.Host = incoming.Headers["Host"];

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException)
        {
            await WriteHtmlAsync(context, 502, Page("O servidor não respondeu",
                $"A Sirius tentou buscar esta página em {Html(upstream)}, mas ninguém respondeu. Se ela vem do Executar, clique em Executar; se vem do Apache, ligue o Apache. A página recarrega sozinha quando você salvar um arquivo."));
            return;
        }

        using (response)
        {
            var outgoing = context.Response;
            outgoing.StatusCode = (int)response.StatusCode;
            outgoing.Headers["Cache-Control"] = "no-cache";
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (HopHeaders.Contains(header.Key) || CacheHeaders.Contains(header.Key))
                    continue;
                foreach (var value in header.Value)
                {
                    var text = header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) && value.StartsWith(upstream, StringComparison.OrdinalIgnoreCase)
                        ? Origin + value[upstream.Length..]
                        : value;
                    try
                    {
                        outgoing.Headers.Add(header.Key, text);
                    }
                    catch (ArgumentException)
                    {
                        // Um cabeçalho que o próprio HttpListener controla.
                    }
                }
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var isHead = incoming.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) && !isHead)
            {
                var body = await response.Content.ReadAsByteArrayAsync();
                var post = incoming.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
                var injected = Inject(body, post);
                outgoing.ContentLength64 = injected.Length;
                await outgoing.OutputStream.WriteAsync(injected);
                outgoing.Close();
                return;
            }

            if (response.Content.Headers.ContentLength is { } length)
                outgoing.ContentLength64 = length;
            else
                outgoing.SendChunked = true;
            if (!isHead)
                await response.Content.CopyToAsync(outgoing.OutputStream);
            outgoing.Close();
        }
    }

    // Servir a pasta, quando não há servidor

    private async Task ServeFileAsync(HttpListenerContext context)
    {
        var root = StaticRoot;
        if (root is null)
        {
            await WriteHtmlAsync(context, 404, Page("Nada para mostrar", "Abra um arquivo .html na Sirius e use Abrir no navegador."));
            return;
        }

        var urlPath = context.Request.Url!.AbsolutePath;
        var relative = Uri.UnescapeDataString(urlPath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            await WriteHtmlAsync(context, 404, NotFound(urlPath));
            return;
        }
        // Nada fora da pasta do projeto, nem por "..".
        if (!IsInside(root, full))
        {
            await WriteHtmlAsync(context, 404, NotFound(urlPath));
            return;
        }

        if (Directory.Exists(full))
        {
            // Sem a barra no fim, os links relativos da página apontariam para a pasta de cima.
            if (!urlPath.EndsWith('/'))
            {
                context.Response.StatusCode = 301;
                context.Response.RedirectLocation = urlPath + "/" + context.Request.Url.Query;
                context.Response.Close();
                return;
            }
            var index = new[] { "index.html", "index.htm" }.Select(name => Path.Combine(full, name)).FirstOrDefault(File.Exists);
            if (index is null)
            {
                await WriteHtmlAsync(context, 404, NotFound(urlPath));
                return;
            }
            full = index;
        }

        if (!File.Exists(full))
        {
            await WriteHtmlAsync(context, 404, NotFound(urlPath));
            return;
        }

        if (string.Equals(Path.GetExtension(full), ".php", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHtmlAsync(context, 503, Page("Esta página é PHP",
                "Para ver o resultado do PHP, clique em Executar na Sirius e abra a página de novo pelo botão Navegador."));
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await WriteHtmlAsync(context, 404, NotFound(urlPath));
            return;
        }

        var type = ContentType(full);
        context.Response.Headers["Cache-Control"] = "no-store";
        if (type.StartsWith("text/html", StringComparison.Ordinal))
            bytes = Inject(bytes, post: false);
        await WriteAsync(context.Response, 200, type, bytes, head: context.Request.HttpMethod == "HEAD");
    }

    public static bool IsInside(string root, string path)
    {
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".ico" => "image/x-icon",
        ".bmp" => "image/bmp",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".xml" => "application/xml",
        _ => "application/octet-stream",
    };

    // O script entra antes do último </body>, ou no fim, quando a página não tem um. Em bytes, para não
    // depender da codificação da página.
    public static byte[] Inject(byte[] page, bool post)
    {
        var tag = Encoding.ASCII.GetBytes($"<script src=\"{ScriptPath}\"{(post ? " data-post=\"1\"" : "")}></script>");
        var at = LastBodyClose(page);
        if (at < 0)
            return [.. page, .. tag];
        return [.. page.AsSpan(0, at), .. tag, .. page.AsSpan(at)];
    }

    private static int LastBodyClose(byte[] page)
    {
        var target = "</body"u8;
        for (var i = page.Length - target.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < target.Length && match; j++)
                match = char.ToLowerInvariant((char)page[i + j]) == (char)target[j];
            if (match)
                return i;
        }
        return -1;
    }

    // Avisar as páginas abertas

    private void OpenEvents(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        var hello = Encoding.UTF8.GetBytes("retry: 1000\n\n");
        response.OutputStream.Write(hello);
        response.OutputStream.Flush();
        lock (_gate)
            _pages.Add(response);
    }

    // Avisa já, sem esperar: cssOnly troca só o estilo, sem recarregar a página.
    public void Notify(bool cssOnly) => Send($"data: {(cssOnly ? "css" : "recarregar")}\n\n");

    private void Send(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        lock (_gate)
        {
            for (var i = _pages.Count - 1; i >= 0; i--)
            {
                try
                {
                    _pages[i].OutputStream.Write(bytes);
                    _pages[i].OutputStream.Flush();
                }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    try { _pages[i].Abort(); } catch (Exception) { }
                    _pages.RemoveAt(i);
                }
            }
        }
    }

    // Vigiar a pasta do projeto

    public void Watch(string? root)
    {
        if (root is not null)
            root = Path.GetFullPath(root);
        if (string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase))
            return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedRoot = root;
        if (root is null || !Directory.Exists(root))
            return;

        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, e) => FileChanged(e.FullPath);
        watcher.Created += (_, e) => FileChanged(e.FullPath);
        watcher.Deleted += (_, e) => FileChanged(e.FullPath);
        watcher.Renamed += (_, e) => FileChanged(e.FullPath);
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    public static bool ShouldReload(string root, string path)
    {
        if (!ReloadExtensions.Contains(Path.GetExtension(path)))
            return false;
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return !parts.Any(part => part is ".git" or "node_modules");
    }

    // Vários eventos de uma gravação só, como o arquivo temporário e o nome final, viram um aviso.
    public void FileChanged(string path)
    {
        if (_watchedRoot is not { } root || !ShouldReload(root, path))
            return;
        lock (_gate)
        {
            _pending = true;
            _pendingFull |= !string.Equals(Path.GetExtension(path), ".css", StringComparison.OrdinalIgnoreCase);
        }
        _debounce.Change(ChangeDelay, Timeout.InfiniteTimeSpan);
    }

    private void FlushChanges()
    {
        bool full;
        lock (_gate)
        {
            if (!_pending)
                return;
            full = _pendingFull;
            _pending = false;
            _pendingFull = false;
        }
        Notify(cssOnly: !full);
    }

    // Páginas da própria Sirius

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private static string NotFound(string path) =>
        Page("Página não encontrada", $"Não há nada em {Html(Uri.UnescapeDataString(path))} na pasta do projeto.");

    private static string Page(string title, string text) => $$"""
        <!doctype html>
        <html lang="pt-BR"><head><meta charset="utf-8"><title>{{Html(title)}}</title>
        <style>body{font:15px/1.5 system-ui,sans-serif;color:#16171A;background:#E8E9EC;margin:0;display:grid;place-items:center;min-height:100vh}
        main{background:#fff;border-radius:14px;padding:28px 32px;max-width:520px;box-shadow:0 14px 36px #14141620}
        h1{font-size:18px;margin:0 0 8px}p{margin:0;color:#5C5F67}</style></head>
        <body><main><h1>{{Html(title)}}</h1><p>{{text}}</p></main></body></html>
        """;

    private static Task WriteHtmlAsync(HttpListenerContext context, int status, string html)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        return WriteAsync(context.Response, status, "text/html; charset=utf-8", Inject(Encoding.UTF8.GetBytes(html), post: false),
            head: context.Request.HttpMethod == "HEAD");
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string type, byte[] body, bool head = false)
    {
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        if (!head)
            await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public void Dispose()
    {
        _heartbeat.Dispose();
        _debounce.Dispose();
        _watcher?.Dispose();
        lock (_gate)
        {
            foreach (var page in _pages)
            {
                try { page.Abort(); } catch (Exception) { }
            }
            _pages.Clear();
        }
        try { _listener?.Close(); } catch (ObjectDisposedException) { }
        _client.Dispose();
    }
}
