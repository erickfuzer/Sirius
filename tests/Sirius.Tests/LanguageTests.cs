using System.IO.Pipelines;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Sirius.Core;
using Sirius.Lsp;

namespace Sirius.Tests;

// Um servidor de linguagem de mentira, ligado à Sirius por canais em memória.
public sealed class FakeLanguageServer : IDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly List<(string Method, JsonElement Parameters)> _notifications = new();
    private readonly LspConnection _server;

    public FakeLanguageServer()
    {
        _server = new LspConnection(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        _server.RequestHandler = Handle;
        _server.NotificationReceived += (method, parameters) =>
        {
            lock (_notifications)
                _notifications.Add((method, parameters));
        };
        _server.Start();
        Connection = new ServerConnection(_toClient.Reader.AsStream(), _toServer.Writer.AsStream(), this);
    }

    public ServerConnection Connection { get; }
    public Func<JsonElement, object?> Completion { get; set; } = _ => new { items = new[] { new { label = "imprimir", insertText = "imprimir" } } };
    public Func<JsonElement, object?> Definition { get; set; } = _ => null;
    public Func<JsonElement, object?> Rename { get; set; } = _ => null;
    public Func<JsonElement, object?> Hover { get; set; } = _ => null;

    public List<(string Method, JsonElement Parameters)> Notifications
    {
        get
        {
            lock (_notifications)
                return _notifications.ToList();
        }
    }

    public void Publish(string path, params object[] diagnostics) =>
        _server.Notify("textDocument/publishDiagnostics", new { uri = LspParsers.ToUri(path), diagnostics });

    public void PublishUri(string uri, params object[] diagnostics) =>
        _server.Notify("textDocument/publishDiagnostics", new { uri, diagnostics });

    public static object Diagnostic(int line, int start, int end, int severity, string message) => new
    {
        range = new { start = new { line, character = start }, end = new { line, character = end } },
        severity,
        message,
    };

    public void Crash()
    {
        _toClient.Writer.Complete();
    }

    public void Dispose()
    {
        _toClient.Writer.Complete();
        _toServer.Writer.Complete();
    }

    public JsonElement? InitializeParameters { get; private set; }

    private object? Handle(string method, JsonElement parameters) => method switch
    {
        "initialize" => Initialize(parameters),
        "textDocument/completion" => Completion(parameters),
        "textDocument/definition" => Definition(parameters),
        "textDocument/rename" => Rename(parameters),
        "textDocument/hover" => Hover(parameters),
        _ => null,
    };

    private object Initialize(JsonElement parameters)
    {
        InitializeParameters = parameters;
        return new
        {
            capabilities = new
            {
                textDocumentSync = 1,
                completionProvider = new { triggerCharacters = new[] { ">", "." } },
                definitionProvider = true,
                hoverProvider = true,
                renameProvider = true,
            },
        };
    }
}

public class LspConnectionTests
{
    private static (LspConnection Client, LspConnection Server) Pair()
    {
        var a = new Pipe();
        var b = new Pipe();
        var client = new LspConnection(a.Reader.AsStream(), b.Writer.AsStream());
        var server = new LspConnection(b.Reader.AsStream(), a.Writer.AsStream());
        client.Start();
        server.Start();
        return (client, server);
    }

    [Fact]
    public async Task Pedido_recebe_a_resposta_certa()
    {
        var (client, server) = Pair();
        server.RequestHandler = (method, parameters) => new { echo = method + parameters.GetProperty("x").GetInt32() };

        var one = client.RequestAsync("a", new { x = 1 });
        var two = client.RequestAsync("b", new { x = 2 });

        Assert.Equal("b2", (await two).GetProperty("echo").GetString());
        Assert.Equal("a1", (await one).GetProperty("echo").GetString());
    }

    [Fact]
    public async Task Acentos_passam_inteiros_e_o_tamanho_conta_bytes()
    {
        var (client, server) = Pair();
        server.RequestHandler = (_, parameters) => new { texto = parameters.GetProperty("t").GetString() };

        var result = await client.RequestAsync("a", new { t = "coração 🚀" });

        Assert.Equal("coração 🚀", result.GetProperty("texto").GetString());
    }

    [Fact]
    public async Task Queda_do_servidor_avisa_quem_estava_esperando()
    {
        var a = new Pipe();
        var b = new Pipe();
        var client = new LspConnection(a.Reader.AsStream(), b.Writer.AsStream());
        client.Start();

        var pending = client.RequestAsync("a", null);
        a.Writer.Complete();

        await Assert.ThrowsAsync<LspException>(() => pending);
    }

    [Fact]
    public async Task Cancelar_nao_deixa_o_pedido_pendurado()
    {
        var a = new Pipe();
        var b = new Pipe();
        var client = new LspConnection(a.Reader.AsStream(), b.Writer.AsStream());
        client.Start();
        using var cancel = new CancellationTokenSource();

        var pending = client.RequestAsync("a", null, cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Pedido_de_configuracao_do_servidor_recebe_um_item_por_chave()
    {
        var (client, server) = Pair();
        _ = client;

        var result = await server.RequestAsync("workspace/configuration", new { items = new[] { new { section = "a" }, new { section = "b" } } });

        Assert.Equal(2, result.GetArrayLength());
        // Objeto vazio, e não null: o servidor de CSS real quebra ao validar com null.
        Assert.All(result.EnumerateArray(), item => Assert.Equal(JsonValueKind.Object, item.ValueKind));
    }
}

public class LspParsersTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Le_diagnosticos()
    {
        var list = LspParsers.Diagnostics(Json("""
            {"diagnostics":[{"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":5}},"severity":2,"message":"cuidado"}]}
            """));
        Assert.Equal(new LspDiagnostic(1, 2, 1, 5, DiagnosticSeverity.Warning, "cuidado"), Assert.Single(list));
    }

    [Fact]
    public void Diagnostico_sem_gravidade_conta_como_erro()
    {
        var list = LspParsers.Diagnostics(Json("""{"diagnostics":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"x"}]}"""));
        Assert.Equal(DiagnosticSeverity.Error, list[0].Severity);
    }

    [Theory]
    [InlineData("""[{"label":"a"}]""")]
    [InlineData("""{"isIncomplete":false,"items":[{"label":"a"}]}""")]
    public void Sugestoes_aceitam_lista_ou_objeto(string json)
    {
        Assert.Equal("a", Assert.Single(LspParsers.Completion(Json(json))).InsertText);
    }

    [Fact]
    public void Sugestao_prefere_o_texto_da_edicao()
    {
        var entry = LspParsers.Completion(Json("""[{"label":"a","insertText":"b","textEdit":{"newText":"c"}}]"""))[0];
        Assert.Equal("c", entry.InsertText);
    }

    [Fact]
    public void Sugestao_em_formato_de_trecho_perde_os_marcadores()
    {
        var entry = LspParsers.Completion(Json("""[{"label":"f","insertText":"f(${1:x}, $2)$0","insertTextFormat":2}]"""))[0];
        Assert.Equal("f(x, )", entry.InsertText);
    }

    [Fact]
    public void Resposta_vazia_nao_quebra()
    {
        Assert.Empty(LspParsers.Completion(Json("null")));
        Assert.Null(LspParsers.Definition(Json("null")));
        Assert.Null(LspParsers.Definition(Json("[]")));
        Assert.Null(LspParsers.Hover(Json("null")));
        Assert.Empty(LspParsers.WorkspaceEdit(Json("null")));
    }

    [Theory]
    [InlineData("""{"uri":"file:///C:/p/a.cs","range":{"start":{"line":3,"character":4},"end":{"line":3,"character":5}}}""")]
    [InlineData("""[{"uri":"file:///C:/p/a.cs","range":{"start":{"line":3,"character":4},"end":{"line":3,"character":5}}}]""")]
    [InlineData("""[{"targetUri":"file:///C:/p/a.cs","targetSelectionRange":{"start":{"line":3,"character":4},"end":{"line":3,"character":5}}}]""")]
    public void Definicao_aceita_os_tres_formatos(string json)
    {
        Assert.Equal(new LspLocation(@"C:\p\a.cs", 3, 4), LspParsers.Definition(Json(json)));
    }

    [Theory]
    [InlineData("""{"contents":"texto"}""", "texto")]
    [InlineData("""{"contents":{"kind":"markdown","value":"texto"}}""", "texto")]
    [InlineData("""{"contents":[{"language":"php","value":"um"},"dois"]}""", "um\n\ndois")]
    public void Dica_aceita_os_formatos(string json, string expected)
    {
        Assert.Equal(expected, LspParsers.Hover(Json(json)));
    }

    [Fact]
    public void Renomear_aceita_changes_e_documentChanges()
    {
        const string range = """{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}""";
        var changes = LspParsers.WorkspaceEdit(Json("""{"changes":{"file:///C:/p/a.cs":[{"range":RANGE,"newText":"x"}]}}""".Replace("RANGE", range)));
        var documents = LspParsers.WorkspaceEdit(Json("""{"documentChanges":[{"textDocument":{"uri":"file:///C:/p/b.cs"},"edits":[{"range":RANGE,"newText":"y"}]}]}""".Replace("RANGE", range)));
        Assert.Equal("x", changes[@"C:\p\a.cs"][0].NewText);
        Assert.Equal("y", documents[@"C:\p\b.cs"][0].NewText);
    }

    [Theory]
    [InlineData("file:///C:/p/a.php", @"C:\p\a.php")]
    [InlineData("file:///c:/p/a.php", @"C:\p\a.php")]
    [InlineData("file:///c%3A/p/a.php", @"C:\p\a.php")]
    [InlineData("file:///c%3a/Meus%20Projetos/cora%C3%A7%C3%A3o/a.php", @"C:\Meus Projetos\coração\a.php")]
    public void Entende_as_formas_de_escrever_um_caminho(string uri, string expected)
    {
        Assert.Equal(expected, LspParsers.ToPath(uri));
    }

    [Fact]
    public void Caminho_com_espaco_e_acento_vai_e_volta()
    {
        const string path = @"C:\Meus Projetos\coração\a.php";
        Assert.Equal(path, LspParsers.ToPath(LspParsers.ToUri(path)));
    }
}

public class LspPositionsTests
{
    [Fact]
    public void Converte_posicoes_nos_dois_sentidos()
    {
        var document = new TextDocument("um\ndois\ntres");
        Assert.Equal(4, LspPositions.ToOffset(document, 1, 1));
        Assert.Equal((1, 1), LspPositions.FromOffset(document, 4));
    }

    [Fact]
    public void Posicao_fora_do_arquivo_fica_no_limite()
    {
        var document = new TextDocument("um\ndois");
        Assert.Equal(2, LspPositions.ToOffset(document, 0, 99));
        Assert.Equal(document.TextLength, LspPositions.ToOffset(document, 99, 0));
    }

    [Fact]
    public void Varias_edicoes_nao_deslocam_umas_as_outras()
    {
        var document = new TextDocument("foo foo\nfoo");
        LspPositions.ApplyEdits(document,
        [
            new LspTextEdit(0, 0, 0, 3, "barra"),
            new LspTextEdit(0, 4, 0, 7, "barra"),
            new LspTextEdit(1, 0, 1, 3, "barra"),
        ]);
        Assert.Equal("barra barra\nbarra", document.Text);
    }

    [Fact]
    public void Edicoes_se_desfazem_de_uma_vez()
    {
        var document = new TextDocument("a b");
        LspPositions.ApplyEdits(document, [new LspTextEdit(0, 0, 0, 1, "x"), new LspTextEdit(0, 2, 0, 3, "y")]);
        document.UndoStack.Undo();
        Assert.Equal("a b", document.Text);
    }
}

public class LanguageServerCatalogTests
{
    [Theory]
    [InlineData("a.php", "php", "php")]
    [InlineData("a.cs", "csharp", "csharp")]
    [InlineData("a.TS", "typescript", "typescript")]
    [InlineData("a.py", "python", "python")]
    public void Escolhe_o_servidor_pela_extensao(string file, string server, string language)
    {
        var entry = LanguageServerCatalog.ForPath(file)!.Value;
        Assert.Equal((server, language), (entry.Server.Id, entry.LanguageId));
    }

    [Fact]
    public void Arquivo_sem_servidor_conhecido()
    {
        Assert.Null(LanguageServerCatalog.ForPath("notas.txt"));
    }

    [Theory]
    [InlineData("a.cs", "dotnet tool install -g csharp-ls")]
    [InlineData("a.php", "npm install -g intelephense")]
    [InlineData("a.py", "npm install -g pyright")]
    public void Sabe_como_instalar(string file, string expected)
    {
        var server = LanguageServerCatalog.ForPath(file)!.Value.Server;
        Assert.Contains(expected, LanguageServerCatalog.InstallCommand(server));
    }

    [Fact]
    public void Instalacao_manual_nao_tem_comando()
    {
        Assert.Null(LanguageServerCatalog.InstallCommand(LanguageServerCatalog.ForPath("a.go")!.Value.Server));
    }
}

public class LanguageWindowTests
{
    private static (MainWindow Window, FakeLanguageServer Server) Open(TempFolder temp)
    {
        var server = new FakeLanguageServer();
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => true;
        window.Language.Launcher = (_, _) => server.Connection;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, server);
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "A condição não aconteceu a tempo.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Task Received(FakeLanguageServer server, string method) =>
        Until(() => server.Notifications.Any(n => n.Method == method));

    [AvaloniaFact]
    public async Task Avisa_o_servidor_ao_abrir_editar_salvar_e_fechar()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "<?php");
        var (window, server) = Open(temp);

        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");
        var opened = server.Notifications.First(n => n.Method == "textDocument/didOpen").Parameters.GetProperty("textDocument");
        Assert.Equal("php", opened.GetProperty("languageId").GetString());
        Assert.Equal("<?php", opened.GetProperty("text").GetString());

        var document = window.ActiveDocument!;
        document.Document.Insert(5, " echo 1;");
        await Received(server, "textDocument/didChange");
        var change = server.Notifications.Last(n => n.Method == "textDocument/didChange").Parameters;
        Assert.Equal("<?php echo 1;", change.GetProperty("contentChanges")[0].GetProperty("text").GetString());

        await window.SaveAsync(document);
        await Received(server, "textDocument/didSave");

        await window.CloseDocumentAsync(document);
        await Received(server, "textDocument/didClose");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Erros_do_servidor_aparecem_na_capsula_e_na_lista()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "um\ndois");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");

        server.Publish(path,
            FakeLanguageServer.Diagnostic(1, 0, 4, 2, "aviso aqui"),
            FakeLanguageServer.Diagnostic(0, 0, 2, 1, "erro aqui\ncom segunda linha"));
        await Until(() => window.ProblemRows.Count == 2);

        Assert.Equal("1 erro, 1 aviso", window.ProblemsSummary);
        Assert.Equal("erro aqui", window.ProblemRows[0].Message);
        Assert.Equal("a.php:1", window.ProblemRows[0].Location);

        window.ShowProblems();
        Assert.True(window.IsShowingProblems);
        Assert.True(window.IsTerminalOpen);

        window.OpenProblem(1);
        Assert.Equal(2, window.CaretLine);

        server.Publish(path);
        await Until(() => window.ProblemRows.Count == 0);
        Assert.Equal("", window.ProblemsSummary);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Erros_com_caminho_codificado_chegam_ao_arquivo_certo()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");

        var encoded = LspParsers.ToUri(path).Replace("file:///C:", "file:///c%3A");
        server.PublishUri(encoded, FakeLanguageServer.Diagnostic(0, 0, 1, 1, "erro"));
        await Until(() => window.ProblemRows.Count == 1);

        Assert.Equal(path, window.ProblemRows[0].FullPath, ignoreCase: true);
        Assert.Single(window.Language.DiagnosticsFor(path));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Fechar_o_arquivo_limpa_os_erros_dele()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");
        server.Publish(path, FakeLanguageServer.Diagnostic(0, 0, 1, 1, "erro"));
        await Until(() => window.ProblemRows.Count == 1);

        await window.CloseDocumentAsync(window.ActiveDocument!);

        Assert.Empty(window.ProblemRows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Digitar_abre_sugestoes_e_enter_completa()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("im");
        await Until(() => window.IsCompletionOpen);
        Assert.Contains("imprimir", window.CompletionLabels);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("imprimir", window.ActiveDocument!.Document.Text);
        Assert.False(window.IsCompletionOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sugestao_envia_a_posicao_certa_com_o_texto_atualizado()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "um\n");
        var (window, server) = Open(temp);
        JsonElement? received = null;
        server.Completion = parameters =>
        {
            received = parameters.Clone();
            return Array.Empty<object>();
        };
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");
        window.GoToLocation(path, 2, 1);
        window.ActiveDocument!.Document.Insert(3, "ab");
        window.GoToLocation(path, 2, 3);

        await window.RequestCompletionAsync();

        Assert.Equal(1, received!.Value.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(2, received!.Value.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Contains(server.Notifications, n => n.Method == "textDocument/didChange");
        Assert.False(window.IsCompletionOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task F12_abre_a_definicao_em_outro_arquivo()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var other = temp.File("b.php", "um\ndois\ntres");
        var (window, server) = Open(temp);
        server.Definition = _ => new
        {
            uri = LspParsers.ToUri(other),
            range = new { start = new { line = 2, character = 1 }, end = new { line = 2, character = 2 } },
        };
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");

        await window.GoToDefinitionAsync();

        Assert.Equal("b.php", window.ActiveDocument?.Name);
        Assert.Equal(3, window.CaretLine);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Definicao_nao_encontrada_nao_muda_de_arquivo()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");

        await window.GoToDefinitionAsync();

        Assert.Equal("a.php", window.ActiveDocument?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renomear_simbolo_altera_todos_os_arquivos_sem_salvar()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "foo");
        var other = temp.File("b.php", "x foo");
        var (window, server) = Open(temp);
        server.Rename = _ => new
        {
            changes = new Dictionary<string, object[]>
            {
                [LspParsers.ToUri(path)] = [new { range = Range(0, 0, 3), newText = "barra" }],
                [LspParsers.ToUri(other)] = [new { range = Range(0, 2, 5), newText = "barra" }],
            },
        };
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");

        var renaming = window.RenameSymbolAsync();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        window.KeyTextInput("barra");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await renaming;

        Assert.Equal("a.php", window.ActiveDocument?.Name);
        Assert.Equal(2, window.Documents.Count);
        Assert.Equal("barra", window.Documents.Single(d => d.Name == "a.php").Document.Text);
        Assert.Equal("x barra", window.Documents.Single(d => d.Name == "b.php").Document.Text);
        Assert.All(window.Documents, d => Assert.True(d.IsDirty));
        Assert.Equal("x foo", File.ReadAllText(other));
        window.Close();
    }

    private static object Range(int line, int start, int end) =>
        new { start = new { line, character = start }, end = new { line, character = end } };

    [AvaloniaFact]
    public async Task Queda_do_servidor_limpa_os_erros_e_nao_trava_o_editor()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");
        server.Publish(path, FakeLanguageServer.Diagnostic(0, 0, 1, 1, "erro"));
        await Until(() => window.ProblemRows.Count == 1);

        server.Crash();
        await Until(() => window.ProblemRows.Count == 0);

        window.ActiveDocument!.Document.Insert(0, "y");
        Assert.Equal("yx", window.ActiveDocument!.Document.Text);
        await window.RequestCompletionAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renomear_o_arquivo_avisa_o_servidor()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var (window, server) = Open(temp);
        window.OpenFile(path);
        await Received(server, "textDocument/didOpen");

        var renaming = window.RenameAsync(path);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("b");
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await renaming;

        await Received(server, "textDocument/didClose");
        await Until(() => server.Notifications.Count(n => n.Method == "textDocument/didOpen") == 2);
        var reopened = server.Notifications.Last(n => n.Method == "textDocument/didOpen").Parameters.GetProperty("textDocument");
        Assert.EndsWith("b.php", reopened.GetProperty("uri").GetString());
        window.Close();
    }

    [AvaloniaFact]
    public void Sem_servidor_instalado_oferece_a_instalacao()
    {
        using var temp = new TempFolder();
        var path = temp.File("a.php", "x");
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.OpenFile(path);

        Assert.Equal("PHP", window.MissingServerName);
        window.Close();
    }

    [AvaloniaFact]
    public void Arquivo_sem_linguagem_nao_oferece_nada()
    {
        using var temp = new TempFolder();
        var path = temp.File("notas.txt", "x");
        var window = new MainWindow(new AppSettings(), Path.Combine(temp.Path, "settings.json"), temp.Path);
        window.Language.IsInstalled = _ => false;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.OpenFile(path);

        Assert.Null(window.MissingServerName);
        window.Close();
    }
}
