using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sirius.Core;
using Xunit;

namespace Sirius.Tests;

public class LiveServerTests
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private static LiveServer StaticServer(string root)
    {
        var server = new LiveServer { StaticRoot = root };
        server.Start(5600);
        return server;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Get(string url)
    {
        var response = await Client.GetAsync(url, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), response);
    }

    // Servir a pasta

    [Fact]
    public async Task Serve_o_html_da_pasta_com_o_script_antes_do_fim_do_body()
    {
        using var temp = new TempFolder();
        temp.File("pagina.html", "<html><body><h1>Oi</h1></body></html>");
        using var server = StaticServer(temp.Path);

        var (status, body, response) = await Get(server.Origin + "/pagina.html");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("<html><body><h1>Oi</h1><script src=\"/__sirius/recarga.js\"></script></body></html>", body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Css_e_imagem_saem_como_estao_com_o_tipo_certo()
    {
        using var temp = new TempFolder();
        temp.File("css/estilo.css", "body { color: red; }");
        using var server = StaticServer(temp.Path);

        var (_, body, response) = await Get(server.Origin + "/css/estilo.css");

        Assert.Equal("body { color: red; }", body);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_pasta_abre_o_index_e_sem_a_barra_vai_para_o_endereco_com_ela()
    {
        using var temp = new TempFolder();
        temp.File("loja/index.html", "<p>loja</p>");
        using var server = StaticServer(temp.Path);

        var (status, _, response) = await Get(server.Origin + "/loja");
        Assert.Equal(HttpStatusCode.MovedPermanently, status);
        Assert.Equal("/loja/", response.Headers.Location?.OriginalString);

        var (_, body, _) = await Get(server.Origin + "/loja/");
        Assert.StartsWith("<p>loja</p>", body);
    }

    [Fact]
    public async Task Nunca_serve_nada_fora_da_pasta()
    {
        using var temp = new TempFolder();
        temp.File("site/index.html", "ok");
        temp.File("segredo.txt", "senha");
        using var server = StaticServer(Path.Combine(temp.Path, "site"));

        // Pedido escrito à mão: o HttpClient arrumaria o "..". A barra invertida codificada chega inteira.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port, TestContext.Current.CancellationToken);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /..%5Csegredo.txt HTTP/1.1\r\nHost: localhost:{server.Port}\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
        var answer = await new StreamReader(stream).ReadToEndAsync(TestContext.Current.CancellationToken);

        // Recusado pela Sirius (404) ou antes, pelo próprio Windows (400): o que importa é a senha não sair.
        Assert.DoesNotContain("senha", answer);
        Assert.Matches(@"^HTTP/1\.1 4\d\d", answer.Split('\n')[0]);
    }

    [Fact]
    public async Task Pagina_php_sem_servidor_explica_que_falta_o_Executar()
    {
        using var temp = new TempFolder();
        temp.File("index.php", "<?php echo 'oi';");
        using var server = StaticServer(temp.Path);

        var (status, body, _) = await Get(server.Origin + "/index.php");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("clique em Executar", body);
        Assert.DoesNotContain("<?php", body);
    }

    [Theory]
    [InlineData("<p>oi</p>", false, "<p>oi</p><script src=\"/__sirius/recarga.js\"></script>")]
    [InlineData("<BODY>a</BODY>", false, "<BODY>a<script src=\"/__sirius/recarga.js\"></script></BODY>")]
    [InlineData("<body>a</body><!-- </body> -->", false, "<body>a</body><!-- <script src=\"/__sirius/recarga.js\"></script></body> -->")]
    [InlineData("<body></body>", true, "<body><script src=\"/__sirius/recarga.js\" data-post=\"1\"></script></body>")]
    public void Poe_o_script_no_ultimo_fim_de_body_ou_no_fim(string page, bool post, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(LiveServer.Inject(Encoding.UTF8.GetBytes(page), post)));
    }

    [Fact]
    public async Task O_script_de_recarga_existe()
    {
        using var temp = new TempFolder();
        using var server = StaticServer(temp.Path);

        var (_, body, response) = await Get(server.Origin + LiveServer.ScriptPath);

        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("EventSource('/__sirius/eventos')", body);
        Assert.Contains("location.reload()", body);
    }

    // Repassar a outro servidor

    // Um servidor de mentira, no lugar do PHP ou do Apache.
    private sealed class FakeSite : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Origin { get; }
        public string? LastHost { get; private set; }

        public FakeSite()
        {
            for (var port = 5800; ; port++)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Start();
                    Origin = $"http://localhost:{port}";
                    break;
                }
                catch (HttpListenerException) when (port < 5900)
                {
                }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    LastHost = c.Request.Headers["Host"];
                    var path = c.Request.Url!.AbsolutePath;
                    string body;
                    if (path == "/vai")
                    {
                        c.Response.StatusCode = 302;
                        c.Response.Headers["Location"] = Origin + "/pagina?x=1";
                        c.Response.Close();
                        continue;
                    }
                    if (path == "/estilo.css")
                    {
                        c.Response.ContentType = "text/css";
                        body = "a{}";
                    }
                    else if (path == "/envio")
                    {
                        c.Response.ContentType = "text/html; charset=utf-8";
                        body = "<body>recebi " + await new StreamReader(c.Request.InputStream).ReadToEndAsync() + "</body>";
                    }
                    else
                    {
                        c.Response.ContentType = "text/html; charset=utf-8";
                        c.Response.Headers.Add("Set-Cookie", "a=1; Path=/");
                        c.Response.Headers.Add("Set-Cookie", "b=2; Path=/");
                        body = "<html><body>ação " + c.Request.Headers["Cookie"] + "</body></html>";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    c.Response.ContentLength64 = bytes.Length;
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task Repassa_a_pagina_do_servidor_com_o_script_os_cookies_e_o_endereco_da_Sirius()
    {
        using var site = new FakeSite();
        using var server = new LiveServer { Upstream = site.Origin };
        server.Start(5600);

        using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/pagina");
        request.Headers.Add("Cookie", "sessao=7");
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("<html><body>ação sessao=7<script src=\"/__sirius/recarga.js\"></script></body></html>", body);
        Assert.Equal(["a=1; Path=/", "b=2; Path=/"], response.Headers.GetValues("Set-Cookie"));
        Assert.Equal($"localhost:{server.Port}", site.LastHost);
    }

    [Fact]
    public async Task Redirecionamento_para_o_servidor_volta_pela_Sirius()
    {
        using var site = new FakeSite();
        using var server = new LiveServer { Upstream = site.Origin };
        server.Start(5600);

        var (status, _, response) = await Get(server.Origin + "/vai");

        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.Equal(server.Origin + "/pagina?x=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Envio_de_formulario_chega_ao_servidor_e_a_recarga_nao_reenvia()
    {
        using var site = new FakeSite();
        using var server = new LiveServer { Upstream = site.Origin };
        server.Start(5600);

        var response = await Client.PostAsync(server.Origin + "/envio",
            new FormUrlEncodedContent([new("peso", "70")]), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("<body>recebi peso=70<script src=\"/__sirius/recarga.js\" data-post=\"1\"></script></body>", body);
    }

    [Fact]
    public async Task Css_do_servidor_passa_sem_mudar()
    {
        using var site = new FakeSite();
        using var server = new LiveServer { Upstream = site.Origin };
        server.Start(5600);

        var (_, body, _) = await Get(server.Origin + "/estilo.css");

        Assert.Equal("a{}", body);
    }

    [Fact]
    public async Task Servidor_desligado_vira_uma_pagina_que_explica_e_ainda_recarrega()
    {
        using var server = new LiveServer { Upstream = "http://localhost:1" };
        server.Start(5600);

        var (status, body, _) = await Get(server.Origin + "/index.php");

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("ninguém respondeu", body);
        Assert.Contains(LiveServer.ScriptPath, body);
    }

    // Avisar as páginas

    // Uma página aberta, de mentira: guarda cada aviso que chega, lido em segundo plano.
    private sealed class FakePage : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string> _messages = new();
        private readonly HttpResponseMessage _response;

        private FakePage(HttpResponseMessage response, Stream stream)
        {
            _response = response;
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(stream);
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (line.StartsWith("data: ", StringComparison.Ordinal))
                            _messages.Add(line["data: ".Length..]);
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            });
        }

        public static async Task<FakePage> Open(LiveServer server)
        {
            var response = await Client.GetAsync(server.Origin + LiveServer.EventsPath, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            var page = new FakePage(response, await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
            for (var i = 0; i < 50 && server.ConnectedPages == 0; i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            return page;
        }

        public string? Next(int milliseconds = 3000) => _messages.TryTake(out var message, milliseconds) ? message : null;

        public void Dispose() => _response.Dispose();
    }

    [Fact]
    public async Task A_pagina_aberta_recebe_o_aviso_de_recarregar_ou_so_trocar_o_css()
    {
        using var temp = new TempFolder();
        using var server = StaticServer(temp.Path);
        using var page = await FakePage.Open(server);

        Assert.Equal(1, server.ConnectedPages);
        server.Notify(cssOnly: true);
        Assert.Equal("css", page.Next());
        server.Notify(cssOnly: false);
        Assert.Equal("recarregar", page.Next());
    }

    [Fact]
    public async Task Salvar_um_arquivo_da_pasta_avisa_e_dados_nao()
    {
        using var temp = new TempFolder();
        using var server = StaticServer(temp.Path);
        server.ChangeDelay = TimeSpan.FromMilliseconds(50);
        server.Watch(temp.Path);
        using var page = await FakePage.Open(server);

        File.WriteAllText(Path.Combine(temp.Path, "estilo.css"), "a{}");
        Assert.Equal("css", page.Next());

        File.WriteAllText(Path.Combine(temp.Path, "dados.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "foto.png"), "x");
        Assert.Null(page.Next(600));

        File.WriteAllText(Path.Combine(temp.Path, "index.php"), "<?php");
        File.WriteAllText(Path.Combine(temp.Path, "estilo.css"), "b{}");
        Assert.Equal("recarregar", page.Next());
        Assert.Null(page.Next(400));
    }

    [Theory]
    [InlineData(@"index.php", true)]
    [InlineData(@"css\estilo.css", true)]
    [InlineData(@"js\app.js", true)]
    [InlineData(@"dados.json", false)]
    [InlineData(@"uploads\foto.png", false)]
    [InlineData(@".git\index", false)]
    [InlineData(@"node_modules\pacote\a.js", false)]
    public void So_arquivos_que_mudam_a_pagina_recarregam(string relative, bool expected)
    {
        Assert.Equal(expected, LiveServer.ShouldReload(@"C:\site", Path.Combine(@"C:\site", relative)));
    }

    // Apache

    [Fact]
    public void Le_a_porta_e_a_pasta_do_Apache_como_no_httpd_conf_de_fabrica()
    {
        var conf = """
            Define SRVROOT "C:/servidor/apache"
            ServerRoot "${SRVROOT}"
            # Listen 12.34.56.78:80
            Listen 80
            DocumentRoot "C:/servidor/htdocs"
            <VirtualHost *:80>
                DocumentRoot "C:/outro"
            </VirtualHost>
            """;

        var site = ApacheSite.FromConfig(conf, null);

        Assert.Equal("http://localhost", site?.Origin);
        Assert.Equal(@"C:\servidor\htdocs", site?.DocumentRoot);
    }

    [Theory]
    [InlineData("Listen 127.0.0.1:8080\nDocumentRoot \"C:/www\"", "http://localhost:8080", @"C:\www")]
    [InlineData("Listen [::]:8081\nDocumentRoot C:/www", "http://localhost:8081", @"C:\www")]
    [InlineData("Listen 8082\nDocumentRoot \"htdocs\"", "http://localhost:8082", @"C:\apache\htdocs")]
    [InlineData("Define R C:/r\nDocumentRoot \"${R}/htdocs\"", "http://localhost", @"C:\r\htdocs")]
    public void Entende_as_formas_do_Listen_e_do_DocumentRoot(string conf, string origin, string root)
    {
        var site = ApacheSite.FromConfig(conf, @"C:\apache");
        Assert.Equal(origin, site?.Origin);
        Assert.Equal(root, site?.DocumentRoot);
    }

    [Fact]
    public void O_endereco_de_um_arquivo_dentro_da_pasta_do_Apache()
    {
        using var temp = new TempFolder();
        var file = temp.File("meu trabalho/ação.php", "");
        var site = new ApacheSite("http://localhost", temp.Path);

        Assert.Equal("/meu%20trabalho/a%C3%A7%C3%A3o.php", site.PathFor(file));
        Assert.Equal("/meu%20trabalho/", site.PathFor(Path.GetDirectoryName(file)!));
        Assert.Null(site.PathFor(@"C:\fora\index.php"));
    }

    [Theory]
    [InlineData("php -S localhost:8000", @"C:\loja")]
    [InlineData("php -S localhost:8000 -t public", @"C:\loja\public")]
    [InlineData("php -S localhost:8000 -t \"pasta web\"", @"C:\loja\pasta web")]
    public void A_pasta_que_o_servidor_do_PHP_mostra(string command, string expected)
    {
        Assert.Equal(expected, RunDetector.ServedRoot(command, @"C:\loja"));
    }

    // Na janela

    private static MainWindow Open(TempFolder temp, string name, string text, List<string> opened, AppSettings? settings = null)
    {
        var path = temp.File(name, text);
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(temp.Path, "settings.json"), Path.GetDirectoryName(path))
        {
            OpenUrl = url => { opened.Add(url); return true; },
            FindApache = () => null,
        };
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.OpenFile(path);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void O_botao_Navegador_aparece_em_html_e_php_e_nao_em_css()
    {
        using var temp = new TempFolder();
        var opened = new List<string>();
        var window = Open(temp, "site/index.html", "<p>oi</p>", opened);
        Assert.True(window.IsBrowserButtonVisible);
        Assert.Contains("Abrir no navegador", window.PaletteCommandNames(""));

        window.OpenFile(temp.File("site/estilo.css", "a{}"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsBrowserButtonVisible);
        Assert.DoesNotContain("Abrir no navegador", window.PaletteCommandNames(""));

        window.OpenFile(temp.File("site/pagina.php", "<?php"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsBrowserButtonVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Html_sem_servidor_abre_pela_Sirius_servindo_a_pasta()
    {
        using var temp = new TempFolder();
        var opened = new List<string>();
        var window = Open(temp, "site/sobre nós.html", "<body>sobre</body>", opened);

        window.OpenInBrowser();

        var server = window.BrowserServer!;
        Assert.Equal([server.Origin + "/sobre%20n%C3%B3s.html"], opened);
        Assert.Null(server.Upstream);
        Assert.Contains("recarrega", window.StatusText);
        var page = await Task.Run(() => Client.GetStringAsync(opened[0]));
        Assert.Equal("<body>sobre<script src=\"/__sirius/recarga.js\"></script></body>", page);
        window.Close();
        Assert.Null(window.BrowserServer);
    }

    [AvaloniaFact]
    public void Php_sem_servidor_pede_o_Executar()
    {
        using var temp = new TempFolder();
        var opened = new List<string>();
        var window = Open(temp, "site/index.php", "<?php echo 1;", opened);

        window.OpenInBrowser();

        Assert.Empty(opened);
        Assert.Contains("clique em Executar", window.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void Pasta_dentro_do_Apache_abre_pelo_Apache_na_pagina_inicial()
    {
        using var temp = new TempFolder();
        var opened = new List<string>();
        temp.File("htdocs/trabalho/index.php", "<?php");
        var window = Open(temp, "htdocs/trabalho/calculo.php", "<?php", opened);
        window.FindApache = () => new ApacheSite("http://localhost:8099", Path.Combine(temp.Path, "htdocs"));

        window.OpenInBrowser();

        Assert.Equal([window.BrowserServer!.Origin + "/trabalho/"], opened);
        Assert.Equal("http://localhost:8099", window.BrowserServer.Upstream);
        window.Close();
    }

    [AvaloniaFact]
    public void Com_a_recarga_desligada_abre_o_arquivo_direto()
    {
        using var temp = new TempFolder();
        var opened = new List<string>();
        var window = Open(temp, "site/index.html", "<p>oi</p>", opened, new AppSettings { LiveReload = false });

        window.OpenInBrowser();

        Assert.Equal([new Uri(Path.Combine(temp.Path, "site", "index.html")).AbsoluteUri], opened);
        Assert.Null(window.BrowserServer);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Desligar_nas_configuracoes_fecha_o_servidor_da_Sirius()
    {
        using var temp = new TempFolder();
        var opened = new List<string>();
        var window = Open(temp, "site/index.html", "<p>oi</p>", opened);
        window.OpenInBrowser();
        Assert.NotNull(window.BrowserServer);

        await window.ChangeSettingAsync("recarga:nao");

        Assert.Null(window.BrowserServer);
        Assert.False(AppSettings.Load(Path.Combine(temp.Path, "settings.json")).LiveReload);
        window.Close();
    }
}
