using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sirius.Core;
using Sirius.Editing;
using Sirius.Theming;

namespace Sirius.Tests;

public class XdebugSetupTests
{
    private const string Info82 = "phpinfo()\nPHP Version => 8.2.12\n\nArchitecture => x64\nPHP Extension Build => API20220829,TS,VS16\n";

    [Fact]
    public void Reconhece_o_php_pela_saida_do_phpinfo()
    {
        Assert.Equal(new PhpBuild("8.2", true, 16, true), XdebugSetup.ParseInfo(Info82));
        Assert.Equal(new PhpBuild("8.4", false, 17, true),
            XdebugSetup.ParseInfo("PHP Version => 8.4.3\nArchitecture => x64\nPHP Extension Build => API20240924,NTS,VS17\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PHP Version => 8.2.12\n")]
    [InlineData("PHP Extension Build => API20220829,TS,VS16\n")]
    public void Texto_sem_as_informacoes_nao_vira_um_php(string info) => Assert.Null(XdebugSetup.ParseInfo(info));

    [Fact]
    public void Escolhe_o_arquivo_exato_de_cada_php()
    {
        var release = XdebugCatalog.Find(new PhpBuild("8.2", true, 16, true))!;

        Assert.Equal($"php_xdebug-{XdebugCatalog.Version}-8.2-ts-vs16-x86_64.dll", release.FileName);
        Assert.StartsWith("https://xdebug.org/files/", release.Url);
        Assert.Equal(64, release.Sha256.Length);
        Assert.Contains("-8.4-nts-vs17-", XdebugCatalog.Find(new PhpBuild("8.4", false, 17, true))!.FileName);
    }

    [Theory]
    [InlineData("7.4", true, 15, true)]
    [InlineData("8.2", true, 17, true)]
    [InlineData("9.0", true, 17, true)]
    [InlineData("8.2", true, 16, false)]
    public void Php_sem_arquivo_conhecido_nao_baixa_nada(string version, bool threadSafe, int compiler, bool is64) =>
        Assert.Null(XdebugCatalog.Find(new PhpBuild(version, threadSafe, compiler, is64)));

    private static XdebugRelease ReleaseOf(byte[] content) => new("teste.dll", Convert.ToHexString(SHA256.HashData(content)));

    [Fact]
    public async Task Baixa_e_guarda_quando_o_codigo_confere()
    {
        using var folder = new TempFolder();
        var content = Encoding.UTF8.GetBytes("conteudo");
        var release = ReleaseOf(content);

        var path = await XdebugSetup.DownloadAsync(release, folder.Path, (_, _) => Task.FromResult(content));

        Assert.Equal(content, File.ReadAllBytes(path!));
        Assert.Equal(path, XdebugSetup.InstalledFile(release, folder.Path));
    }

    [Fact]
    public async Task Arquivo_diferente_do_esperado_nunca_vai_para_o_disco()
    {
        using var folder = new TempFolder();
        var release = ReleaseOf(Encoding.UTF8.GetBytes("o certo"));

        var path = await XdebugSetup.DownloadAsync(release, folder.Path, (_, _) => Task.FromResult(Encoding.UTF8.GetBytes("o errado")));

        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task Internet_que_falha_nao_derruba_nada()
    {
        using var folder = new TempFolder();
        var release = ReleaseOf([1]);

        Assert.Null(await XdebugSetup.DownloadAsync(release, folder.Path, (_, _) => throw new HttpRequestException()));
    }

    [Fact]
    public void Arquivo_alterado_no_disco_nao_vale_mais()
    {
        using var folder = new TempFolder();
        var release = ReleaseOf(Encoding.UTF8.GetBytes("inteiro"));
        File.WriteAllText(folder.File(release.FileName), "pela metade");

        Assert.Null(XdebugSetup.InstalledFile(release, folder.Path));
    }

    [Fact]
    public void Configuracao_carrega_o_arquivo_e_liga_a_depuracao()
    {
        var ini = XdebugSetup.IniText(@"C:\Pasta Com Espaço\x.dll");

        Assert.Contains("zend_extension=\"C:/Pasta Com Espaço/x.dll\"", ini);
        Assert.Contains("xdebug.mode=debug", ini);
        Assert.Contains("xdebug.start_with_request=yes", ini);
        Assert.Contains("xdebug.client_host=127.0.0.1", ini);
    }

    [Fact]
    public void Php_que_ja_tem_xdebug_nao_carrega_outro()
    {
        Assert.DoesNotContain("zend_extension", XdebugSetup.IniText(null));
    }

    [Fact]
    public void Comando_so_ganha_variaveis_na_frente()
    {
        var command = XdebugSetup.Command("php -S localhost:8000", @"C:\ini", 9123);

        Assert.EndsWith("&& php -S localhost:8000", command);
        Assert.Contains("set \"PHP_INI_SCAN_DIR=C:\\ini\"", command);
        Assert.Contains("set \"XDEBUG_CONFIG=client_port=9123\"", command);
        Assert.Contains("set \"XDEBUG_MODE=debug\"", command);
    }
}

public class DbgpParserTests
{
    private const string Ns = "xmlns=\"urn:debugger_protocol_v1\"";

    private static List<DebugVariable> Variables(string inner) =>
        DbgpParser.Variables(DbgpParser.Parse($"<response {Ns}>{inner}</response>"));

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Mostra_cada_tipo_como_se_escreve_em_php()
    {
        var variables = Variables(
            "<property name=\"$a\" fullname=\"$a\" type=\"int\"><![CDATA[7]]></property>" +
            "<property name=\"$b\" fullname=\"$b\" type=\"float\"><![CDATA[1.75]]></property>" +
            $"<property name=\"$c\" fullname=\"$c\" type=\"string\" encoding=\"base64\"><![CDATA[{Base64("olá")}]]></property>" +
            "<property name=\"$d\" fullname=\"$d\" type=\"bool\"><![CDATA[1]]></property>" +
            "<property name=\"$e\" fullname=\"$e\" type=\"bool\"><![CDATA[0]]></property>" +
            "<property name=\"$f\" fullname=\"$f\" type=\"null\"></property>" +
            "<property name=\"$g\" fullname=\"$g\" type=\"object\" classname=\"Catalogo\" numchildren=\"0\"></property>");

        Assert.Equal(["7", "1.75", "'olá'", "true", "false", "null", "Catalogo"], variables.Select(v => v.Value));
    }

    [Fact]
    public void Array_traz_os_itens_e_a_contagem()
    {
        var array = Assert.Single(Variables(
            "<property name=\"$lista\" fullname=\"$lista\" type=\"array\" children=\"1\" numchildren=\"2\">" +
            "<property name=\"0\" fullname=\"$lista[0]\" type=\"int\"><![CDATA[10]]></property>" +
            "<property name=\"1\" fullname=\"$lista[1]\" type=\"int\"><![CDATA[20]]></property>" +
            "</property>"));

        Assert.Equal("array(2)", array.Value);
        Assert.Equal(["$lista[0]", "$lista[1]"], array.Children.Select(c => c.FullName));
    }

    [Fact]
    public void Variavel_que_ainda_nao_existe_nao_aparece()
    {
        var variables = Variables(
            "<property name=\"$depois\" fullname=\"$depois\" type=\"uninitialized\"></property>" +
            "<property name=\"$agora\" fullname=\"$agora\" type=\"int\"><![CDATA[1]]></property>");

        Assert.Equal("$agora", Assert.Single(variables).Name);
    }

    [Fact]
    public void Das_superglobais_so_ficam_as_uteis_e_com_conteudo()
    {
        var response = DbgpParser.Parse($"<response {Ns}>" +
            "<property name=\"_POST\" fullname=\"$_POST\" type=\"array\" numchildren=\"1\"><property name=\"peso\" fullname=\"$_POST['peso']\" type=\"int\"><![CDATA[70]]></property></property>" +
            "<property name=\"_GET\" fullname=\"$_GET\" type=\"array\" numchildren=\"0\"></property>" +
            "<property name=\"_SERVER\" fullname=\"$_SERVER\" type=\"array\" numchildren=\"1\"><property name=\"x\" fullname=\"$_SERVER['x']\" type=\"int\"><![CDATA[1]]></property></property>" +
            "</response>");

        Assert.Equal("$_POST", Assert.Single(DbgpParser.Superglobals(response)).FullName);
    }

    [Fact]
    public void Le_o_arquivo_e_a_linha_da_parada()
    {
        var response = DbgpParser.Parse($"<response {Ns}><stack where=\"{{main}}\" level=\"0\" type=\"file\" filename=\"file:///C:/a/b.php\" lineno=\"12\"></stack></response>");

        Assert.Equal(new DebugFrame("file:///C:/a/b.php", 12, null, 1), DbgpParser.TopFrame(response));
        Assert.Null(DbgpParser.TopFrame(DbgpParser.Parse($"<response {Ns}></response>")));
    }

    [Fact]
    public void Dentro_de_uma_funcao_conta_quem_chamou()
    {
        var response = DbgpParser.Parse($"<response {Ns}>" +
            "<stack where=\"Catalogo->total\" level=\"0\" type=\"file\" filename=\"file:///C:/a.php\" lineno=\"9\"></stack>" +
            "<stack where=\"{main}\" level=\"1\" type=\"file\" filename=\"file:///C:/a.php\" lineno=\"20\"></stack>" +
            "</response>");

        Assert.Equal(new DebugFrame("file:///C:/a.php", 9, "Catalogo->total", 2), DbgpParser.TopFrame(response));
    }

    [Fact]
    public void Xml_com_dtd_e_recusado()
    {
        Assert.Throws<System.Xml.XmlException>(() =>
            DbgpParser.Parse("<!DOCTYPE a [<!ENTITY x \"y\">]><a>&x;</a>"));
    }
}

public class DebugValuesTests
{
    private static readonly DebugVariable Pessoa = new("$pessoa", "$pessoa", "array", "array(2)",
    [
        new DebugVariable("nome", "$pessoa[\"nome\"]", "string", "'Ana'", []),
        new DebugVariable("notas", "$pessoa[\"notas\"]", "array", "array(1)",
        [
            new DebugVariable("0", "$pessoa[\"notas\"][0]", "int", "9", []),
        ]),
    ]);

    private static readonly DebugVariable Total = new("$total", "$total", "int", "7", []);
    private static readonly DebugVariable Carro = new("$carro", "$carro", "object", "Carro",
    [
        new DebugVariable("cor", "$carro->cor", "string", "'azul'", []),
    ]);
    private static readonly IReadOnlyList<DebugVariable> All = [Pessoa, Total, Carro];

    private static string? At(string line, string under) =>
        DebugValues.At(All, line, line.IndexOf(under, StringComparison.Ordinal))?.FullName;

    [Fact]
    public void Acha_a_variavel_com_o_mouse_em_qualquer_letra()
    {
        const string line = "echo $total + 1;";

        Assert.Equal("$total", DebugValues.At(All, line, 5)?.FullName);
        Assert.Equal("$total", DebugValues.At(All, line, 10)?.FullName);
        Assert.Null(DebugValues.At(All, line, 11));
        Assert.Null(DebugValues.At(All, line, 0));
    }

    [Fact]
    public void Mouse_no_comeco_do_array_mostra_o_array_inteiro()
    {
        Assert.Equal("$pessoa", At("echo $pessoa['nome'];", "$pessoa"));
    }

    [Theory]
    [InlineData("echo $pessoa['nome'];", "'nome'", "$pessoa[\"nome\"]")]
    [InlineData("echo $pessoa[\"nome\"];", "\"nome\"", "$pessoa[\"nome\"]")]
    [InlineData("echo $pessoa['notas'][0];", "'notas'", "$pessoa[\"notas\"]")]
    [InlineData("echo $pessoa['notas'][0];", "[0]", "$pessoa[\"notas\"][0]")]
    [InlineData("echo $carro->cor;", "cor", "$carro->cor")]
    public void Mouse_em_um_item_mostra_ate_aquele_item(string line, string under, string expected)
    {
        Assert.Equal(expected, At(line, under));
    }

    [Fact]
    public void Variavel_que_nao_existe_nesta_parada_nao_tem_dica()
    {
        Assert.Null(At("echo $outra;", "$outra"));
        Assert.Null(At("echo $pessoa['idade'];", "'idade'"));
        Assert.Null(At("echo total;", "total"));
    }

    [Fact]
    public void Dica_traz_o_valor_e_os_itens()
    {
        Assert.Equal("$total = 7", DebugValues.Describe(Total));
        Assert.Equal("$pessoa = array(2)\nnome: 'Ana'\nnotas: array(1)", DebugValues.Describe(Pessoa));
    }

    [Fact]
    public void Dica_de_array_grande_corta_os_itens()
    {
        var items = Enumerable.Range(0, 40).Select(i => new DebugVariable(i.ToString(), $"$a[{i}]", "int", "1", [])).ToList();

        var tip = DebugValues.Describe(new DebugVariable("$a", "$a", "array", "array(40)", items));

        Assert.Equal(14, tip.Split('\n').Length);
        Assert.EndsWith("E mais 28", tip);
    }
}

// Faz o papel do Xdebug: conecta no servidor e responde pelo mesmo protocolo.
public sealed class FakeXdebug : IDisposable
{
    // CallerVariables e CallerLine descrevem quem chamou a função, no nível 1 da pilha.
    public sealed record Stop(string File, int Line, string Variables = "", string Globals = "", string? Function = null, byte[][]? Output = null, string? Held = null,
        string? CallerVariables = null, int CallerLine = 1, string? CallerFile = null, IReadOnlyDictionary<string, string>? Evals = null);

    private readonly TcpClient _client = new();
    private readonly Queue<Stop> _stops;
    private Stop? _current;

    public FakeXdebug(params Stop[] stops) => _stops = new Queue<Stop>(stops);

    public ConcurrentQueue<string> Commands { get; } = new();
    public Task Finished { get; private set; } = Task.CompletedTask;

    public static string Int(string name, int value) =>
        $"<property name=\"{name}\" fullname=\"{name}\" type=\"int\"><![CDATA[{value}]]></property>";

    public void Connect(int port) => Finished = RunAsync(port);

    private async Task RunAsync(int port)
    {
        await _client.ConnectAsync(IPAddress.Loopback, port);
        var stream = _client.GetStream();
        await WriteAsync(stream, "<init xmlns=\"urn:debugger_protocol_v1\" fileuri=\"file:///C:/x.php\"/>");

        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer);
            }
            catch (IOException)
            {
                return;
            }
            if (read == 0)
                return;
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            int end;
            while ((end = text.ToString().IndexOf('\0')) >= 0)
            {
                var command = text.ToString(0, end);
                text.Remove(0, end + 1);
                if (!await AnswerAsync(stream, command))
                    return;
            }
        }
    }

    private async Task<bool> AnswerAsync(NetworkStream stream, string command)
    {
        Commands.Enqueue(command);
        var parts = command.Split(' ');
        var name = parts[0];
        var id = parts[Array.IndexOf(parts, "-i") + 1];
        var inner = "";
        var attributes = "";
        switch (name)
        {
            case "breakpoint_set":
                attributes = $" id=\"{id}\"";
                break;
            case "run" or "step_over" or "step_into" or "step_out":
                if (_stops.TryDequeue(out var next))
                {
                    _current = next;
                    attributes = " status=\"break\" reason=\"ok\"";
                    // O que a linha escreveu chega antes da resposta de que ela parou.
                    foreach (var piece in next.Output ?? [])
                        await WriteAsync(stream, $"<stream xmlns=\"urn:debugger_protocol_v1\" type=\"stdout\" encoding=\"base64\">{Convert.ToBase64String(piece)}</stream>");
                }
                else
                {
                    attributes = " status=\"stopping\" reason=\"ok\"";
                }
                break;
            case "stack_get":
                var uri = new Uri(_current!.File).AbsoluteUri;
                inner = _current.Function is { } function
                    ? $"<stack where=\"{function}\" level=\"0\" type=\"file\" filename=\"{uri}\" lineno=\"{_current.Line}\"></stack>" +
                      $"<stack where=\"{{main}}\" level=\"1\" type=\"file\" filename=\"{new Uri(_current.CallerFile ?? _current.File).AbsoluteUri}\" lineno=\"{_current.CallerLine}\"></stack>"
                    : $"<stack where=\"{{main}}\" level=\"0\" type=\"file\" filename=\"{uri}\" lineno=\"{_current.Line}\"></stack>";
                break;
            case "context_get":
                inner = command.Contains("-c 1") ? _current!.Globals
                    : command.Contains("-d 1 ") && _current!.CallerVariables is { } caller ? caller
                    : _current!.Variables;
                break;
            case "eval" when Expression(command) is { } expression && expression != "ob_get_contents()":
                // Uma expressão vigiada: o resultado combinado na parada, ou um erro como o do Xdebug.
                inner = _current!.Evals?.GetValueOrDefault(expression)
                    ?? "<error code=\"206\"><message><![CDATA[error evaluating code]]></message></error>";
                break;
            case "eval":
                // Sem buffer aberto, ob_get_contents() devolve false.
                inner = _current!.Held is { } held
                    ? $"<property type=\"string\" encoding=\"base64\"><![CDATA[{Convert.ToBase64String(Encoding.UTF8.GetBytes(held))}]]></property>"
                    : "<property type=\"bool\"><![CDATA[0]]></property>";
                break;
        }

        await WriteAsync(stream, $"<response xmlns=\"urn:debugger_protocol_v1\" command=\"{name}\" transaction_id=\"{id}\"{attributes}>{inner}</response>");
        if (name != "stop")
            return true;
        _client.Close();
        return false;
    }

    private static string? Expression(string command)
    {
        var at = command.IndexOf(" -- ", StringComparison.Ordinal);
        return at < 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(command[(at + 4)..]));
    }

    private static async Task WriteAsync(NetworkStream stream, string xml)
    {
        var body = Encoding.UTF8.GetBytes(xml);
        var head = Encoding.ASCII.GetBytes(body.Length + "\0");
        await stream.WriteAsync(head.Concat(body).Append((byte)0).ToArray());
    }

    public void Dispose() => _client.Dispose();
}

public class XdebugServerTests
{
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static Dictionary<string, IReadOnlyCollection<int>> Points(string file, params int[] lines) => new() { [file] = lines };

    [Fact]
    public void So_ouve_neste_computador_e_escolhe_uma_porta_livre()
    {
        using var first = new XdebugServer();
        using var second = new XdebugServer();
        first.Start();
        second.Start();

        Assert.NotEqual(0, first.Port);
        Assert.NotEqual(first.Port, second.Port);
    }

    [Fact]
    public async Task Marca_os_pontos_para_e_mostra_as_variaveis()
    {
        var file = @"C:\Meu Projeto\calculo.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.SetBreakpoints(Points(file, 5));
        server.Start();
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 5, FakeXdebug.Int("$peso", 70)));

        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        pauses.TryPeek(out var pause);
        Assert.Equal(file, pause!.Path);
        Assert.Equal(5, pause.Line);
        Assert.Equal("$peso", Assert.Single(pause.Variables).Name);
        Assert.True(server.IsPaused);
        Assert.Contains(php.Commands, c => c.StartsWith("breakpoint_set") && c.Contains("-f file:///C:/Meu%20Projeto/calculo.php") && c.EndsWith("-n 5"));

        server.Continue();
        await php.Finished;
    }

    [Fact]
    public async Task Proxima_linha_e_continuar_vao_para_o_xdebug()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        var ended = 0;
        var resumed = 0;
        server.Paused += pauses.Enqueue;
        server.Resumed += () => Interlocked.Increment(ref resumed);
        server.PageEnded += () => Interlocked.Increment(ref ended);
        server.Start();
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 1), new FakeXdebug.Stop(file, 2));
        php.Connect(server.Port);

        await Until(() => pauses.Count == 1);
        server.StepOver();
        await Until(() => pauses.Count == 2);
        Assert.Contains(php.Commands, c => c.StartsWith("step_over"));
        Assert.Equal(1, Volatile.Read(ref resumed));

        server.Continue();
        await Until(() => Volatile.Read(ref ended) == 1);

        Assert.False(server.IsPaused);
        Assert.Contains(php.Commands, c => c.StartsWith("stop"));
    }

    [Fact]
    public async Task Entrar_e_sair_de_funcao_vao_para_o_xdebug()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.Start();
        using var php = new FakeXdebug(
            new FakeXdebug.Stop(file, 1),
            new FakeXdebug.Stop(file, 5, Function: "calcular"),
            new FakeXdebug.Stop(file, 2));
        php.Connect(server.Port);
        await Until(() => pauses.Count == 1);
        Assert.Null(pauses.Last().Function);
        Assert.Equal(1, pauses.Last().Depth);

        server.StepInto();
        await Until(() => pauses.Count == 2);
        Assert.Contains(php.Commands, c => c.StartsWith("step_into"));
        Assert.Equal("calcular", pauses.Last().Function);
        Assert.Equal(2, pauses.Last().Depth);

        server.StepOut();
        await Until(() => pauses.Count == 3);
        Assert.Contains(php.Commands, c => c.StartsWith("step_out"));

        server.Continue();
        await php.Finished;
    }

    [Fact]
    public async Task Pede_a_saida_e_junta_o_acento_partido_entre_dois_pedacos()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pieces = new ConcurrentQueue<string>();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.OutputWritten += pieces.Enqueue;
        server.Paused += pauses.Enqueue;
        server.Start();

        // "á" em UTF-8 são dois bytes, aqui em pedaços separados.
        var word = Encoding.UTF8.GetBytes("olá");
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 2, Output: [word[..3], word[3..], Encoding.UTF8.GetBytes(" mundo")]));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        Assert.Contains(php.Commands, command => command.StartsWith("stdout") && command.EndsWith("-c 1"));
        Assert.Equal("olá mundo", string.Concat(pieces));
        Assert.Equal("", pauses.Last().HeldOutput);
    }

    [Fact]
    public async Task Le_o_que_o_php_ainda_segura_na_parada()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.Start();

        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 2, Held: "ação "));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        Assert.Equal("ação ", pauses.Last().HeldOutput);
    }

    [Fact]
    public async Task Botoes_fora_de_uma_parada_nao_mandam_nada()
    {
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.Start();

        server.Continue();
        server.StepOver();

        using var php = new FakeXdebug(new FakeXdebug.Stop(@"C:\a.php", 1));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Continuou parado: os cliques de antes não valem para esta parada.
        Assert.True(server.IsPaused);
    }

    [Fact]
    public async Task Mudar_os_pontos_durante_a_parada_vale_na_hora()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.SetBreakpoints(Points(file, 1));
        server.Start();
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 1));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        server.SetBreakpoints(Points(file, 2));

        await Until(() => php.Commands.Any(c => c.StartsWith("breakpoint_set") && c.EndsWith("-n 2")));
        await Until(() => php.Commands.Any(c => c.StartsWith("breakpoint_remove")));
    }

    [Fact]
    public async Task Ponto_com_condicao_vai_como_conditional_em_base64()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.SetConditions(new Dictionary<string, IReadOnlyDictionary<int, string>> { [file] = new Dictionary<int, string> { [3] = "$i == 5" } });
        server.SetBreakpoints(Points(file, 1, 3));
        server.Start();
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 1));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("$i == 5"));
        Assert.Contains(php.Commands, c => c.StartsWith("breakpoint_set") && c.Contains("-t conditional") && c.Contains("-n 3") && c.EndsWith("-- " + expected));
        Assert.Contains(php.Commands, c => c.StartsWith("breakpoint_set") && c.Contains("-t line") && c.EndsWith("-n 1"));

        // Trocar a condição com o programa parado troca o ponto no Xdebug.
        server.SetConditions(new Dictionary<string, IReadOnlyDictionary<int, string>> { [file] = new Dictionary<int, string> { [3] = "$i > 9" } });
        var changed = Convert.ToBase64String(Encoding.UTF8.GetBytes("$i > 9"));
        await Until(() => php.Commands.Any(c => c.EndsWith("-- " + changed)));
        await Until(() => php.Commands.Any(c => c.StartsWith("breakpoint_remove")));
    }

    [Fact]
    public async Task Expressoes_vigiadas_sao_calculadas_a_cada_parada()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.SetWatches(["$total * 2", "$nada"]);
        server.Start();
        var evals = new Dictionary<string, string> { ["$total * 2"] = "<property type=\"int\"><![CDATA[84]]></property>" };
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 1, Evals: evals));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        pauses.TryPeek(out var pause);
        Assert.Equal(["$total * 2", "$nada"], pause!.Watches!.Select(w => w.Name));
        Assert.Equal("84", pause.Watches![0].Value);
        Assert.Equal("error evaluating code", pause.Watches[1].Value);

        // Uma nova durante a parada é calculada na hora, sem esperar a próxima.
        server.SetWatches(["$total * 2", "$nada", "$total * 2 + 1"]);
        await Until(() => pauses.Count == 2);
        Assert.Equal(3, pauses.Last().Watches!.Count);
        server.Continue();
        await php.Finished;
    }

    [Fact]
    public async Task Cada_pagina_e_uma_conexao_e_a_seguinte_tambem_para()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.Start();

        using var first = new FakeXdebug(new FakeXdebug.Stop(file, 1));
        first.Connect(server.Port);
        await Until(() => pauses.Count == 1);
        server.Continue();
        await first.Finished;

        using var second = new FakeXdebug(new FakeXdebug.Stop(file, 3));
        second.Connect(server.Port);
        await Until(() => pauses.Count == 2);
        Assert.Equal(3, pauses.Last().Line);
        server.Continue();
        await second.Finished;
    }

    [Fact]
    public async Task Mensagem_invalida_encerra_so_aquela_pagina()
    {
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        var ended = 0;
        server.Paused += pauses.Enqueue;
        server.PageEnded += () => Interlocked.Increment(ref ended);
        server.Start();

        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Loopback, server.Port, TestContext.Current.CancellationToken);
            await bad.GetStream().WriteAsync(Encoding.ASCII.GetBytes("xyz\0nada\0"), TestContext.Current.CancellationToken);
            await Until(() => Volatile.Read(ref ended) == 1);
        }

        using var php = new FakeXdebug(new FakeXdebug.Stop(@"C:\a.php", 1));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);
        server.Continue();
        await php.Finished;
    }

    [Fact]
    public async Task Fechar_com_uma_pagina_parada_solta_o_php()
    {
        var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.Start();
        using var php = new FakeXdebug(new FakeXdebug.Stop(@"C:\a.php", 1));
        php.Connect(server.Port);
        await Until(() => !pauses.IsEmpty);

        server.Dispose();

        await php.Finished.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Le_as_variaveis_de_cada_nivel_da_pilha()
    {
        var file = @"C:\a.php";
        using var server = new XdebugServer();
        var pauses = new ConcurrentQueue<DebugPause>();
        server.Paused += pauses.Enqueue;
        server.Start();
        var post = "<property name=\"$_POST\" fullname=\"$_POST\" type=\"array\" numchildren=\"1\">" + FakeXdebug.Int("a", 1).Replace("fullname=\"a\"", "fullname=\"$_POST['a']\"") + "</property>";
        using var php = new FakeXdebug(new FakeXdebug.Stop(file, 3, FakeXdebug.Int("$x", 5), post, Function: "calcular",
            CallerVariables: FakeXdebug.Int("$base", 7), CallerLine: 6, CallerFile: @"C:\index.php"));
        php.Connect(server.Port);
        await Until(() => pauses.Count == 1);

        var frames = pauses.Single().Frames;
        Assert.Equal(2, frames.Count);
        Assert.Equal((@"C:\a.php", 3, "calcular()"), (frames[0].Path, frames[0].Line, frames[0].Name));
        Assert.Equal((@"C:\index.php", 6, "Arquivo principal"), (frames[1].Path, frames[1].Line, frames[1].Name));
        Assert.Equal(["$x", "$_POST"], frames[0].Variables.Select(v => v.Name));
        Assert.Equal(["$base", "$_POST"], frames[1].Variables.Select(v => v.Name));
        Assert.Contains(php.Commands, c => c.StartsWith("context_get") && c.Contains("-d 1 -c 0"));

        server.Continue();
        await php.Finished;
    }

    [Theory]
    [InlineData(null, "Arquivo principal")]
    [InlineData("{main}", "Arquivo principal")]
    [InlineData("calcular", "calcular()")]
    [InlineData("Carrinho->total", "Carrinho->total()")]
    [InlineData("{closure:C:\\a.php:3-5}", "Função sem nome")]
    [InlineData("require_once", "require_once")]
    public void Nome_de_cada_nivel_da_pilha(string? where, string expected) =>
        Assert.Equal(expected, DbgpParser.FrameName(where));

    [Fact]
    public void Parada_antiga_sem_pilha_vira_um_nivel_so()
    {
        var pause = new DebugPause(@"C:\a.php", 2, [], "f", 2);
        var frame = Assert.Single(pause.Frames);
        Assert.Equal("f()", frame.Name);
    }
}

public class OpenDocumentBreakpointTests
{
    // Abrir um arquivo carrega as gramáticas, que são compartilhadas: fora da thread dos outros testes, elas quebram.
    private static OpenDocument Document(TempFolder temp) =>
        OpenDocument.Load(temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n$c = 3;\n"));

    [AvaloniaFact]
    public void Marca_e_desmarca_a_linha()
    {
        using var temp = new TempFolder();
        var document = Document(temp);

        Assert.True(document.ToggleBreakpoint(2));
        Assert.True(document.ToggleBreakpoint(4));
        Assert.Equal([2, 4], document.BreakpointLines);

        Assert.False(document.ToggleBreakpoint(2));
        Assert.Equal([4], document.BreakpointLines);
    }

    [AvaloniaFact]
    public void O_ponto_acompanha_a_linha_quando_se_escreve_acima()
    {
        using var temp = new TempFolder();
        var document = Document(temp);
        document.ToggleBreakpoint(3);

        document.Document.Insert(document.Document.GetLineByNumber(2).Offset, "// novo\n// outro\n");

        Assert.Equal([5], document.BreakpointLines);
    }

    [AvaloniaFact]
    public void Apagar_a_linha_inteira_com_outras_leva_o_ponto()
    {
        using var temp = new TempFolder();
        var document = Document(temp);
        document.ToggleBreakpoint(3);
        var from = document.Document.GetLineByNumber(2).Offset;
        var to = document.Document.GetLineByNumber(4).Offset;

        document.Document.Remove(from, to - from);

        Assert.Empty(document.BreakpointLines);
    }

    [AvaloniaFact]
    public void Linhas_guardadas_fora_do_arquivo_sao_ignoradas()
    {
        using var temp = new TempFolder();
        var document = Document(temp);

        document.SetBreakpoints([2, 99, 0, -1]);

        Assert.Equal([2], document.BreakpointLines);
    }
}

// A janela de verdade com um Xdebug falso.
public class DebugWindowTests
{
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(15);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static MainWindow Open(TempFolder temp, TempFolder config, AppSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new AppSettings(), Path.Combine(config.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.OpenUrl = _ => true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Ponto_de_parada_so_vale_em_arquivo_php()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var css = temp.File("a.css", "body {}\n");
        var window = Open(temp, config);

        window.OpenFile(css);
        window.ToggleBreakpoint(1);
        Assert.Empty(window.BreakpointsSnapshot());

        window.OpenFile(php);
        window.ToggleBreakpoint(2);
        Assert.Equal([2], window.BreakpointsSnapshot()[php]);
        window.Close();
    }

    private static void Click(MainWindow window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Controls.Button ButtonNamed(MainWindow window, string name) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(button => button.Name == name);

    [AvaloniaFact]
    public void Clique_na_margem_marca_a_linha_e_fora_dela_so_move_o_cursor()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var gutter = window.GetVisualDescendants().OfType<GutterMargin>().Single();
        var firstLine = gutter.TranslatePoint(new Point(10, 8), window)!.Value;

        Click(window, firstLine);
        Assert.Equal([1], window.ActiveDocument!.BreakpointLines);

        Click(window, firstLine);
        Assert.Empty(window.ActiveDocument!.BreakpointLines);

        // Perto do fio, à direita dos números, continua sendo só ir para a linha.
        Click(window, gutter.TranslatePoint(new Point(gutter.Bounds.Width - 4, 8), window)!.Value);
        Assert.Empty(window.ActiveDocument!.BreakpointLines);
        window.Close();
    }

    // Pelo botão direito de verdade: soltar o botão não pode tirar o foco da caixa, senão o que se digita some.
    [AvaloniaFact]
    public async Task Botao_direito_no_ponto_pede_a_condicao_e_recebe_o_texto()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var gutter = window.GetVisualDescendants().OfType<GutterMargin>().Single();
        var firstLine = gutter.TranslatePoint(new Point(10, 8), window)!.Value;

        window.MouseMove(firstLine);
        window.MouseDown(firstLine, MouseButton.Right);
        window.MouseUp(firstLine, MouseButton.Right);
        await Until(() => window.IsDialogOpen);
        window.KeyTextInput("$a > 0");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await Until(() => !window.IsDialogOpen);

        Assert.Equal("$a > 0", window.ActiveDocument!.BreakpointConditions[1]);
        Assert.Equal("<?php\n$a = 1;\n$b = 2;\n", window.ActiveDocument.Document.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Margem_de_outros_arquivos_nao_marca_nada()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var css = temp.File("a.css", "body {}\n");
        var window = Open(temp, config);
        window.OpenFile(css);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var gutter = window.GetVisualDescendants().OfType<GutterMargin>().Single();

        Click(window, gutter.TranslatePoint(new Point(10, 8), window)!.Value);

        Assert.Empty(window.ActiveDocument!.BreakpointLines);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Botoes_da_capsula_avancam_e_continuam()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 2), new FakeXdebug.Stop(php, 3));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 2);

        // A cápsula anima ao abrir, e sem tela a animação não anda: o clique físico é conferido na janela real.
        ButtonNamed(window, "DebugNextButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => window.PausedLine == 3);

        ButtonNamed(window, "DebugContinueButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => !window.IsDebugPaused);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Fechar_a_aba_mantem_os_pontos_e_abrir_de_novo_retoma()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        window.ToggleBreakpoint(2);

        await window.CloseDocumentAsync(window.ActiveDocument!);
        Assert.Equal([2], window.BreakpointsSnapshot()[php]);

        window.OpenFile(php);
        Assert.Equal([2], window.ActiveDocument!.BreakpointLines);
        window.Close();
    }

    [AvaloniaFact]
    public void Pontos_voltam_ao_reabrir_a_pasta()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var settings = new AppSettings();
        var window = Open(temp, config, settings);
        window.OpenFile(php);
        window.ToggleBreakpoint(2);
        window.Close();

        // A pasta abre só depois de desligar os servidores reais, que prenderiam a pasta temporária.
        var saved = AppSettings.Load(Path.Combine(config.Path, "settings.json"));
        saved.LastFolder = null;
        var reopened = new MainWindow(saved, Path.Combine(config.Path, "settings.json"), null);
        reopened.Language.IsInstalled = _ => false;
        reopened.Show();
        reopened.OpenFolder(temp.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([2], reopened.BreakpointsSnapshot()[php]);
        Assert.Equal([2], reopened.ActiveDocument!.BreakpointLines);
        reopened.Close();
    }

    private static void Answer(MainWindow window, string text)
    {
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        if (text.Length > 0)
            window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Condicao_marca_a_linha_e_vai_para_o_snapshot()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\nfor ($i = 0; $i < 10; $i++) {\n    echo $i;\n}\n");
        var window = Open(temp, config);
        window.OpenFile(php);

        var asking = window.EditBreakpointConditionAsync(3);
        await Until(() => window.IsDialogOpen);
        Answer(window, "$i == 5");
        await asking;

        Assert.Equal([3], window.ActiveDocument!.BreakpointLines);
        Assert.Equal("$i == 5", window.ConditionsSnapshot()[php][3]);
        Assert.Contains("só quando $i == 5", window.StatusText);

        // Vazio volta a parar sempre, e o ponto continua.
        asking = window.EditBreakpointConditionAsync(3);
        await Until(() => window.IsDialogOpen);
        Answer(window, "");
        await asking;
        Assert.Equal([3], window.ActiveDocument!.BreakpointLines);
        Assert.Empty(window.ConditionsSnapshot());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Condicao_acompanha_a_linha_e_fecha_e_reabre_com_a_aba()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        window.ActiveDocument!.SetBreakpointCondition(3, "$a > 0");

        // Uma linha nova em cima empurra o ponto e a condição juntos.
        window.ActiveDocument.Document.Insert(6, "// nota\n");
        Assert.Equal("$a > 0", window.ActiveDocument.BreakpointConditions[4]);

        Assert.True(await window.SaveAsync(window.ActiveDocument));
        await window.CloseDocumentAsync(window.ActiveDocument);
        Assert.Equal("$a > 0", window.ConditionsSnapshot()[php][4]);
        window.OpenFile(php);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("$a > 0", window.ActiveDocument!.BreakpointConditions[4]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Condicoes_voltam_ao_reabrir_a_pasta()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var window = Open(temp, config, new AppSettings());
        window.OpenFile(php);
        var asking = window.EditBreakpointConditionAsync(2);
        await Until(() => window.IsDialogOpen);
        Answer(window, "$a === 1");
        await asking;
        window.Close();

        var saved = AppSettings.Load(Path.Combine(config.Path, "settings.json"));
        saved.LastFolder = null;
        var reopened = new MainWindow(saved, Path.Combine(config.Path, "settings.json"), null);
        reopened.Language.IsInstalled = _ => false;
        reopened.Show();
        reopened.OpenFolder(temp.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("$a === 1", reopened.ConditionsSnapshot()[php][2]);
        Assert.Equal("$a === 1", reopened.ActiveDocument!.BreakpointConditions[2]);
        reopened.Close();
    }

    [AvaloniaFact]
    public async Task Vigiar_mostra_a_expressao_em_cima_e_o_x_tira()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$total = 42;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        var server = window.StartDebugServerForTest();
        var evals = new Dictionary<string, string> { ["$total * 2"] = "<property type=\"int\"><![CDATA[84]]></property>" };
        using var fake = new FakeXdebug(new FakeXdebug.Stop(php, 2, FakeXdebug.Int("$total", 42), Evals: evals));

        var asking = window.AddWatchAsync();
        await Until(() => window.IsDialogOpen);
        Answer(window, "$total * 2");
        await asking;
        Assert.Equal(["$total * 2"], window.Watches);
        Assert.Equal("aparece na próxima parada", window.VariableRows[0].Value);

        fake.Connect(server.Port);
        await Until(() => window.IsDebugPaused && window.VariableRows.Count == 2);
        Assert.True(window.VariableRows[0].IsWatch);
        Assert.Equal(("$total * 2", "84"), (window.VariableRows[0].Name, window.VariableRows[0].Value));
        Assert.Equal("$total", window.VariableRows[1].Name);
        Assert.False(window.VariableRows[1].IsWatch);

        window.RemoveWatch("$total * 2");
        Assert.Empty(window.Watches);
        Assert.Equal("$total", Assert.Single(window.VariableRows).Name);
        server.Continue();
        await fake.Finished;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Vigiadas_voltam_ao_reabrir_a_pasta()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        temp.File("a.php", "<?php\n");
        var window = Open(temp, config, new AppSettings());
        await window.AddWatchAsync("count($lista)");
        window.Close();

        var saved = AppSettings.Load(Path.Combine(config.Path, "settings.json"));
        saved.LastFolder = null;
        var reopened = new MainWindow(saved, Path.Combine(config.Path, "settings.json"), null);
        reopened.Language.IsInstalled = _ => false;
        reopened.Show();
        reopened.OpenFolder(temp.Path);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["count($lista)"], reopened.Watches);
        reopened.Close();
    }

    [AvaloniaFact]
    public void Mouse_na_metade_direita_da_ultima_letra_ainda_aponta_para_ela()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$n = 1;\n");
        var window = Open(temp, config);
        window.OpenFile(php);
        Dispatcher.UIThread.RunJobs();
        var view = window.FindControl<AvaloniaEdit.TextEditor>("CodeEditor")!.TextArea.TextView;
        view.EnsureVisualLines();

        // A letra n fica entre as colunas 2 e 3 da linha 2.
        var left = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(2, 2), AvaloniaEdit.Rendering.VisualYPosition.TextMiddle);
        var right = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(2, 3), AvaloniaEdit.Rendering.VisualYPosition.TextMiddle);
        Assert.True(right.X > left.X);
        var point = new Point(left.X + (right.X - left.X) * 0.8, left.Y) - view.ScrollOffset;

        Assert.Equal(window.ActiveDocument!.Document.Text.IndexOf("n =", StringComparison.Ordinal), window.OffsetAt(point));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Parar_com_a_janela_minimizada_volta_maximizada_e_sem_ficar_sempre_visivel()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        window.WindowState = WindowState.Maximized;
        window.WindowState = WindowState.Minimized;

        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 3, FakeXdebug.Int("$a", 1)));
        engine.Connect(server.Port);
        await Until(() => window.IsDebugPaused);

        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.False(window.Topmost);

        window.DebugContinue();
        await Until(() => !window.IsDebugPaused);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Parar_abre_o_arquivo_na_linha_e_mostra_as_variaveis()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        window.ShowVariables();
        Assert.Contains("linha marcada", window.VariablesHintText);
        Assert.False(window.AreDebugButtonsVisible);

        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 3, FakeXdebug.Int("$a", 1)));
        engine.Connect(server.Port);
        await Until(() => window.IsDebugPaused);

        Assert.Equal(php, window.ActiveDocument!.FullPath);
        Assert.Equal(3, window.CaretLine);
        Assert.Equal(3, window.PausedLine);
        Assert.True(window.AreDebugButtonsVisible);
        Assert.True(window.IsShowingVariables);
        Assert.Equal("Parado em a.php, linha 3", window.TerminalInfoText);
        var row = Assert.Single(window.VariableRows);
        Assert.Equal(("$a", "1", "int"), (row.Name, row.Value, row.Type));

        window.DebugContinue();
        await Until(() => !window.IsDebugPaused);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Proxima_linha_destaca_so_o_que_mudou()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n$c = 3;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(
            new FakeXdebug.Stop(php, 2, FakeXdebug.Int("$a", 1)),
            new FakeXdebug.Stop(php, 3, FakeXdebug.Int("$a", 1) + FakeXdebug.Int("$b", 2)));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 2);
        Assert.All(window.VariableRows, row => Assert.NotEqual(Palette.Current.DiffAdded, row.Background));

        window.DebugNext();
        await Until(() => window.PausedLine == 3);

        Assert.NotEqual(Palette.Current.DiffAdded, window.VariableRows.Single(r => r.Name == "$a").Background);
        Assert.Equal(Palette.Current.DiffAdded, window.VariableRows.Single(r => r.Name == "$b").Background);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clique_abre_os_itens_de_um_array()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = [1];\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        const string array = "<property name=\"$a\" fullname=\"$a\" type=\"array\" numchildren=\"1\"><property name=\"0\" fullname=\"$a[0]\" type=\"int\"><![CDATA[1]]></property></property>";
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 2, array));
        engine.Connect(server.Port);
        await Until(() => window.IsDebugPaused);
        Assert.Single(window.VariableRows);
        Assert.Equal("array(1)", window.VariableRows[0].Value);

        window.ToggleVariable("$a");

        Assert.Equal(["$a", "0"], window.VariableRows.Select(r => r.Name));
        window.ToggleVariable("$a");
        Assert.Single(window.VariableRows);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task F5_continua_e_F10_avanca_quando_parado()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 2), new FakeXdebug.Stop(php, 3));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 2);

        window.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.None);
        await Until(() => window.PausedLine == 3);

        window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await Until(() => !window.IsDebugPaused);

        Assert.False(window.IsTaskRunning);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task F11_entra_e_Shift_F11_sai_da_funcao()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n$c = 3;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(
            new FakeXdebug.Stop(php, 2),
            new FakeXdebug.Stop(php, 4, Function: "calcular"),
            new FakeXdebug.Stop(php, 3));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 2);
        Assert.False(window.IsDebugOutVisible);
        Assert.Equal("Parado em a.php, linha 2", window.TerminalInfoText);

        // No corpo principal, sair correria até o fim do programa sem avisar.
        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.PausedLine);

        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
        await Until(() => window.PausedLine == 4);
        Assert.True(window.IsDebugOutVisible);
        Assert.Equal("Parado em a.php, linha 4, em calcular()", window.TerminalInfoText);

        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
        await Until(() => window.PausedLine == 3);
        Assert.False(window.IsDebugOutVisible);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Botoes_entrar_e_sair_funcionam()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n$b = 2;\n$c = 3;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(
            new FakeXdebug.Stop(php, 2),
            new FakeXdebug.Stop(php, 4, Function: "calcular"),
            new FakeXdebug.Stop(php, 3));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 2);

        ButtonNamed(window, "DebugIntoButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => window.PausedLine == 4);

        ButtonNamed(window, "DebugOutButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => window.PausedLine == 3);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sair_no_corpo_principal_avisa_e_nao_solta_o_programa()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\necho 1;\necho 2;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 2));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 2);

        window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, window.PausedLine);
        Assert.Contains("função", window.StatusText);
        Assert.DoesNotContain(engine.Commands, command => command.StartsWith("step_out"));
        window.EndDebug();
        window.Close();
    }

    private const string StackCode = "<?php\nfunction calcular($x) {\n    $y = $x * 2;\n    return $y;\n}\n$base = 7; $total = calcular($base);\necho $total;\n";

    [AvaloniaFact]
    public async Task Pilha_mostra_quem_chamou_e_as_variaveis_de_la()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", StackCode);
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(
            new FakeXdebug.Stop(php, 3, FakeXdebug.Int("$x", 5), Function: "calcular",
                CallerVariables: "<property name=\"$total\" fullname=\"$total\" type=\"uninitialized\"></property>" + FakeXdebug.Int("$base", 7), CallerLine: 6),
            new FakeXdebug.Stop(php, 4, FakeXdebug.Int("$x", 5) + FakeXdebug.Int("$y", 10), Function: "calcular", CallerLine: 6));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 3);

        Assert.True(window.AreFramesVisible);
        Assert.Equal(["calcular()", "Arquivo principal"], window.FrameRows.Select(r => r.Name));
        Assert.Equal(["a.php:3", "a.php:6"], window.FrameRows.Select(r => r.Location));
        Assert.Contains(window.VariableRows, r => r.Name == "$x");
        Assert.NotNull(window.DebugTipAt(StackCode.IndexOf("$x * 2", StringComparison.Ordinal)));

        // O clique no nível de baixo leva à linha que chamou, com as variáveis de lá.
        var caller = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => b.DataContext is FrameRow { Index: 1 });
        caller.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, window.SelectedFrameIndex);
        Assert.Equal(6, window.CaretLine);
        Assert.Equal(3, window.PausedLine);
        Assert.Equal(["$base"], window.VariableRows.Select(r => r.Name));
        Assert.StartsWith("Vendo quem chamou: Arquivo principal", window.TerminalInfoText);
        Assert.StartsWith("$base = 7", window.DebugTipAt(StackCode.IndexOf("$base)", StringComparison.Ordinal)));
        Assert.Null(window.DebugTipAt(StackCode.IndexOf("$x * 2", StringComparison.Ordinal)));

        // A próxima parada volta para onde o programa está.
        window.DebugNext();
        await Until(() => window.PausedLine == 4);
        Assert.Equal(0, window.SelectedFrameIndex);
        Assert.Contains(window.VariableRows, r => r.Name == "$y");
        Assert.Contains("linha 4", window.TerminalInfoText);
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task No_arquivo_principal_a_pilha_fica_escondida()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", StackCode);
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 6));
        engine.Connect(server.Port);
        await Until(() => window.PausedLine == 6);

        Assert.False(window.AreFramesVisible);
        Assert.Empty(window.FrameRows);
        window.EndDebug();
        Assert.False(window.AreFramesVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mostra_o_que_a_pagina_escreveu_e_troca_na_pagina_seguinte()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\necho 'um';\necho 'dois';\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        window.ShowVariables();
        Assert.False(window.IsPageOutputVisible);

        using var first = new FakeXdebug(
            new FakeXdebug.Stop(php, 2),
            new FakeXdebug.Stop(php, 3, Output: [System.Text.Encoding.UTF8.GetBytes("um")]));
        first.Connect(server.Port);
        await Until(() => window.PausedLine == 2);
        window.DebugNext();
        await Until(() => window.PausedLine == 3);

        Assert.Equal("um", window.PageOutputShown);
        Assert.True(window.IsPageOutputVisible);

        window.DebugContinue();
        await first.Finished;
        await Until(() => !window.IsDebugPaused);
        // Com a página terminada, o que ela escreveu continua à vista.
        Assert.Equal("um", window.PageOutputShown);

        using var second = new FakeXdebug(new FakeXdebug.Stop(php, 2, Output: [System.Text.Encoding.UTF8.GetBytes("novo")]));
        second.Connect(server.Port);
        await Until(() => window.PausedLine == 2);
        Assert.Equal("novo", window.PageOutputShown);

        window.EndDebug();
        Assert.False(window.IsPageOutputVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Texto_que_o_php_segura_aparece_sem_repetir_quando_ele_entrega()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\necho 'um';\necho 'dois';\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        window.ShowVariables();

        using var first = new FakeXdebug(new FakeXdebug.Stop(php, 2, Held: "um"), new FakeXdebug.Stop(php, 3, Held: "umdois"));
        first.Connect(server.Port);
        await Until(() => window.PausedLine == 2);
        Assert.Equal("um", window.PageOutputShown);

        window.DebugNext();
        await Until(() => window.PausedLine == 3);
        Assert.Equal("umdois", window.PageOutputShown);
        window.DebugContinue();
        await first.Finished;
        await Until(() => !window.IsDebugPaused && window.TerminalInfoText.StartsWith("A página terminou"));

        // A página nova parou antes de escrever: o texto antigo não pode ficar como se fosse dela.
        using var second = new FakeXdebug(new FakeXdebug.Stop(php, 2, Output: [System.Text.Encoding.UTF8.GetBytes("já foi")], Held: " e guardado"));
        second.Connect(server.Port);
        await Until(() => window.PausedLine == 2);
        Assert.Equal("já foi e guardado", window.PageOutputShown);

        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dica_do_valor_so_vale_no_arquivo_da_parada_e_durante_ela()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\necho $a;\n");
        var outro = temp.File("b.php", "<?php\necho $a;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 3, FakeXdebug.Int("$a", 1)));
        window.OpenFile(php);
        var offset = window.ActiveDocument!.Document.Text.LastIndexOf("$a", StringComparison.Ordinal);
        Assert.Null(window.DebugTipAt(offset));

        engine.Connect(server.Port);
        await Until(() => window.IsDebugPaused);

        Assert.Equal("$a = 1", window.DebugTipAt(offset));
        Assert.Null(window.DebugTipAt(0));

        window.OpenFile(outro);
        Assert.Null(window.DebugTipAt(window.ActiveDocument!.Document.Text.IndexOf('$')));

        window.OpenFile(php);
        window.DebugContinue();
        await Until(() => !window.IsDebugPaused);
        Assert.Null(window.DebugTipAt(offset));
        window.EndDebug();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pagina_que_termina_avisa_e_encerrar_volta_ao_normal()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var window = Open(temp, config);
        var server = window.StartDebugServerForTest();
        window.ShowVariables();
        using var engine = new FakeXdebug(new FakeXdebug.Stop(php, 2, FakeXdebug.Int("$a", 1)));
        engine.Connect(server.Port);
        await Until(() => window.IsDebugPaused);

        window.DebugContinue();
        await Until(() => window.TerminalInfoText.StartsWith("A página terminou"));
        Assert.Empty(window.VariableRows);
        Assert.False(window.AreDebugButtonsVisible);

        window.EndDebug();
        Assert.False(window.IsDebugging);
        Assert.False(window.IsShowingVariables);
        window.Close();
    }

    private static MainWindow OpenWithCommand(TempFolder temp, TempFolder config)
    {
        var settings = new AppSettings();
        settings.RunCommands[temp.Path] = "php -v";
        var window = Open(temp, config, settings);
        window.IsPhpInstalled = () => true;
        return window;
    }

    [AvaloniaFact]
    public async Task Sem_pontos_o_executar_nao_prepara_a_depuracao()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var window = OpenWithCommand(temp, config);
        var asked = false;
        window.PrepareXdebug = () =>
        {
            asked = true;
            return Task.FromResult(new DebugPreparation(false, null));
        };

        await window.RunProjectAsync();
        await window.TaskFinished;

        Assert.False(asked);
        Assert.Null(window.LastDebugCommand);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Com_pontos_o_executar_roda_com_o_xdebug_ligado()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var window = OpenWithCommand(temp, config);
        window.OpenFile(php);
        window.ToggleBreakpoint(2);
        window.PrepareXdebug = () => Task.FromResult(new DebugPreparation(false, config.Path));

        await window.RunProjectAsync();

        Assert.Contains($"PHP_INI_SCAN_DIR={config.Path}", window.LastDebugCommand);
        Assert.EndsWith("&& php -v", window.LastDebugCommand);
        await Until(() => !window.IsTaskRunning);
        Assert.False(window.IsDebugging);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Recusar_a_preparacao_nao_executa_nada()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var window = OpenWithCommand(temp, config);
        window.OpenFile(php);
        window.ToggleBreakpoint(2);
        window.PrepareXdebug = () => Task.FromResult(new DebugPreparation(true, null));

        await window.RunProjectAsync();

        Assert.False(window.IsTaskRunning);
        Assert.Null(window.LastDebugCommand);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sem_xdebug_o_projeto_ainda_executa_normalmente()
    {
        using var temp = new TempFolder();
        using var config = new TempFolder();
        var php = temp.File("a.php", "<?php\n$a = 1;\n");
        var window = OpenWithCommand(temp, config);
        window.OpenFile(php);
        window.ToggleBreakpoint(2);
        window.PrepareXdebug = () => Task.FromResult(new DebugPreparation(false, null));

        await window.RunProjectAsync();
        await window.TaskFinished;

        Assert.Null(window.LastDebugCommand);
        window.Close();
    }
}
