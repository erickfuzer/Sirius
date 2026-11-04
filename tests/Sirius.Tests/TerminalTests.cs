using Avalonia.Input;
using Sirius.Core;
using Sirius.Terminal;

namespace Sirius.Tests;

public class TerminalBufferTests
{
    private static TerminalBuffer Feed(string text, int columns = 20, int rows = 5)
    {
        var buffer = new TerminalBuffer(columns, rows);
        buffer.Feed(text);
        return buffer;
    }

    [Fact]
    public void Escreve_e_pula_de_linha()
    {
        var buffer = Feed("um\r\ndois");
        Assert.Equal("um", buffer.LineText(0));
        Assert.Equal("dois", buffer.LineText(1));
        Assert.Equal((1, 4), (buffer.CursorRow, buffer.CursorColumn));
    }

    [Fact]
    public void Volta_ao_inicio_e_sobrescreve()
    {
        Assert.Equal("xbc", Feed("abc\rx").LineText(0));
    }

    [Fact]
    public void Texto_longo_quebra_e_lembra_que_quebrou()
    {
        var buffer = Feed("0123456789AB", columns: 10);
        Assert.Equal("0123456789", buffer.LineText(0));
        Assert.Equal("AB", buffer.LineText(1));
        Assert.True(buffer.GetLine(0).Wrapped);
        Assert.False(buffer.GetLine(1).Wrapped);
    }

    [Fact]
    public void Cursor_na_ultima_coluna_so_quebra_no_proximo_caractere()
    {
        var buffer = Feed("0123456789", columns: 10);
        Assert.Equal(0, buffer.CursorRow);
        Assert.False(buffer.GetLine(0).Wrapped);
    }

    [Fact]
    public void Linhas_que_saem_da_tela_vao_para_o_historico()
    {
        var buffer = Feed("a\r\nb\r\nc\r\nd", rows: 2);
        Assert.Equal(2, buffer.ScrollbackCount);
        Assert.Equal("a", buffer.LineText(0));
        Assert.Equal("d", buffer.LineText(3));
    }

    [Fact]
    public void Historico_tem_limite()
    {
        var buffer = new TerminalBuffer(10, 2);
        for (var i = 0; i < TerminalBuffer.MaxScrollback + 1000; i++)
            buffer.Feed("x\r\n");
        Assert.True(buffer.ScrollbackCount < TerminalBuffer.MaxScrollback + 500);
    }

    [Theory]
    [InlineData("\x1b[3;5H", 2, 4)]
    [InlineData("\x1b[H", 0, 0)]
    [InlineData("\x1b[99;99H", 4, 19)]
    [InlineData("\x1b[3;5H\x1b[A", 1, 4)]
    [InlineData("\x1b[3;5H\x1b[2B", 4, 4)]
    [InlineData("\x1b[3;5H\x1b[2C", 2, 6)]
    [InlineData("\x1b[3;5H\x1b[2D", 2, 2)]
    [InlineData("\x1b[3;5H\x1b[10G", 2, 9)]
    [InlineData("\x1b[3;5H\x1b[4d", 3, 4)]
    public void Move_o_cursor(string sequence, int row, int column)
    {
        var buffer = Feed(sequence);
        Assert.Equal((row, column), (buffer.CursorRow, buffer.CursorColumn));
    }

    [Theory]
    [InlineData("abcdef\x1b[3G\x1b[K", "ab")]
    [InlineData("abcdef\x1b[3G\x1b[1K", "   def")]
    [InlineData("abcdef\x1b[2K", "")]
    [InlineData("abcdef\x1b[2G\x1b[2X", "a  def")]
    [InlineData("abcdef\x1b[2G\x1b[2P", "adef")]
    [InlineData("abcdef\x1b[2G\x1b[2@", "a  bcdef")]
    public void Apaga_e_insere_na_linha(string sequence, string expected)
    {
        Assert.Equal(expected, Feed(sequence).LineText(0));
    }

    [Fact]
    public void Limpa_a_tela()
    {
        var buffer = Feed("a\r\nb\x1b[2J");
        Assert.Equal("", buffer.AllText());
    }

    [Fact]
    public void Limpa_da_posicao_ate_o_fim()
    {
        var buffer = Feed("a\r\nb\r\nc\x1b[2;1H\x1b[J");
        Assert.Equal("a", buffer.AllText());
    }

    [Fact]
    public void Aplica_cores_e_estilos()
    {
        var buffer = Feed("\x1b[1;31mA\x1b[0mB\x1b[92mC\x1b[38;5;200mD\x1b[48;2;1;2;3mE");
        var cells = buffer.GetLine(0).Cells;
        Assert.Equal(1, cells[0].Style.Foreground);
        Assert.True(cells[0].Style.Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(CellStyle.Default, cells[1].Style);
        Assert.Equal(10, cells[2].Style.Foreground);
        Assert.Equal(200, cells[3].Style.Foreground);
        Assert.Equal(CellStyle.TrueColorBit | 0x010203, cells[4].Style.Background);
    }

    [Fact]
    public void Sequencia_cortada_entre_dois_pedacos_funciona()
    {
        var buffer = new TerminalBuffer(20, 5);
        buffer.Feed("\x1b[");
        buffer.Feed("3");
        buffer.Feed(";5");
        buffer.Feed("H");
        buffer.Feed("x");
        Assert.Equal("    x", buffer.LineText(2));
    }

    [Fact]
    public void Emoji_cortado_entre_dois_pedacos_funciona()
    {
        var buffer = new TerminalBuffer(20, 5);
        var emoji = char.ConvertFromUtf32(0x1F680);
        buffer.Feed(emoji[..1]);
        buffer.Feed(emoji[1..]);
        Assert.Equal(emoji, buffer.LineText(0));
        Assert.Equal(2, buffer.CursorColumn);
    }

    [Fact]
    public void Titulo_da_janela_nao_aparece_na_tela()
    {
        Assert.Equal("oi", Feed("\x1b]0;Meu título\aoi").LineText(0));
        Assert.Equal("oi", Feed("\x1b]0;Meu título\x1b\\oi").LineText(0));
    }

    [Fact]
    public void Sequencias_desconhecidas_nao_sujam_a_tela()
    {
        Assert.Equal("oi", Feed("\x1b[?9001h\x1b[?1004h\x1b[>4;2m\x1b[2 qoi").LineText(0));
    }

    [Fact]
    public void Tela_alternativa_guarda_e_devolve_o_conteudo()
    {
        var buffer = Feed("principal\x1b[?1049h\x1b[Halternativa");
        Assert.True(buffer.IsAlternateScreen);
        Assert.Equal("alternativa", buffer.LineText(0));

        buffer.Feed("\x1b[?1049l");

        Assert.False(buffer.IsAlternateScreen);
        Assert.Equal("principal", buffer.LineText(0));
        Assert.Equal(9, buffer.CursorColumn);
    }

    [Fact]
    public void Tela_alternativa_nao_enche_o_historico()
    {
        var buffer = Feed("\x1b[?1049h\r\n\r\n\r\n\r\n\r\n\r\n\r\n\x1b[?1049l");
        Assert.Equal(0, buffer.ScrollbackCount);
    }

    [Fact]
    public void Regiao_de_rolagem_preserva_o_resto()
    {
        var buffer = Feed("a\r\nb\r\nc\r\nd\r\ne\x1b[2;4r\x1b[4;1H\n", rows: 5);
        Assert.Equal("a", buffer.LineText(0));
        Assert.Equal("c", buffer.LineText(1));
        Assert.Equal("d", buffer.LineText(2));
        Assert.Equal("", buffer.LineText(3));
        Assert.Equal("e", buffer.LineText(4));
        Assert.Equal(0, buffer.ScrollbackCount);
    }

    [Fact]
    public void Inserir_e_apagar_linhas()
    {
        var buffer = Feed("a\r\nb\r\nc\x1b[2;1H\x1b[L");
        Assert.Equal(["a", "", "b", "c"], Enumerable.Range(0, 4).Select(buffer.LineText));
        buffer.Feed("\x1b[M");
        Assert.Equal(["a", "b", "c"], Enumerable.Range(0, 3).Select(buffer.LineText));
    }

    [Fact]
    public void Responde_quando_o_programa_pergunta_a_posicao()
    {
        var buffer = new TerminalBuffer(20, 5);
        string? reply = null;
        buffer.Reply += text => reply = text;
        buffer.Feed("\x1b[3;5H\x1b[6n");
        Assert.Equal("\x1b[3;5R", reply);
    }

    [Fact]
    public void Salva_e_restaura_o_cursor()
    {
        var buffer = Feed("\x1b[3;5H\u001b7\x1b[H\u001b8");
        Assert.Equal((2, 4), (buffer.CursorRow, buffer.CursorColumn));
    }

    [Fact]
    public void Redimensionar_mantem_o_texto_e_o_cursor_na_tela()
    {
        var buffer = Feed("a\r\nb\r\nc\r\nd", rows: 4);
        buffer.Resize(30, 2);
        Assert.Equal(2, buffer.Rows);
        Assert.InRange(buffer.CursorRow, 0, 1);
        Assert.Equal("a\nb\nc\nd", buffer.AllText());

        buffer.Resize(5, 6);
        Assert.Equal(5, buffer.GetLine(buffer.TotalLines - 1).Cells.Length);
    }

    [Fact]
    public void Linha_logica_junta_o_que_a_tela_quebrou()
    {
        var buffer = Feed("0123456789AB", columns: 10);
        var (text, first) = buffer.LogicalLine(1);
        Assert.Equal("0123456789AB", text);
        Assert.Equal(0, first);
    }
}

public class TerminalKeysTests
{
    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, "\r")]
    [InlineData(Key.Back, KeyModifiers.None, "\x7f")]
    [InlineData(Key.Tab, KeyModifiers.None, "\t")]
    [InlineData(Key.Tab, KeyModifiers.Shift, "\x1b[Z")]
    [InlineData(Key.Up, KeyModifiers.None, "\x1b[A")]
    [InlineData(Key.Left, KeyModifiers.Control, "\x1b[1;5D")]
    [InlineData(Key.Delete, KeyModifiers.None, "\x1b[3~")]
    [InlineData(Key.F1, KeyModifiers.None, "\x1bOP")]
    [InlineData(Key.F5, KeyModifiers.None, "\x1b[15~")]
    [InlineData(Key.C, KeyModifiers.Control, "\x03")]
    [InlineData(Key.A, KeyModifiers.Control, "\x01")]
    [InlineData(Key.B, KeyModifiers.Alt, "\u001bb")]
    public void Traduz_as_teclas(Key key, KeyModifiers modifiers, string expected)
    {
        Assert.Equal(expected, TerminalKeys.Encode(key, modifiers, false));
    }

    [Fact]
    public void Setas_mudam_no_modo_de_aplicativo()
    {
        Assert.Equal("\x1bOA", TerminalKeys.Encode(Key.Up, KeyModifiers.None, true));
    }

    [Fact]
    public void AltGr_do_teclado_brasileiro_fica_para_a_digitacao()
    {
        Assert.Null(TerminalKeys.Encode(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, false));
    }

    [Fact]
    public void Letra_comum_fica_para_a_digitacao()
    {
        Assert.Null(TerminalKeys.Encode(Key.A, KeyModifiers.None, false));
    }
}

public class ErrorLinksTests
{
    [Theory]
    [InlineData(@"C:\p\Program.cs(12,34): error CS1002: ; expected", @"C:\p\Program.cs", 12, 34)]
    [InlineData(@"src\a.cs(7): warning", @"src\a.cs", 7, 1)]
    [InlineData("src/app.ts:10:5 - error TS2322", "src/app.ts", 10, 5)]
    [InlineData("  --> src/main.rs:3:9", "src/main.rs", 3, 9)]
    [InlineData("./main.go:14:2: undefined: x", "./main.go", 14, 2)]
    [InlineData("  File \"C:\\Meus Projetos\\a.py\", line 8, in <module>", @"C:\Meus Projetos\a.py", 8, 1)]
    [InlineData(@"C:\p\a.c:3:1: error", @"C:\p\a.c", 3, 1)]
    public void Encontra_o_arquivo_e_a_linha(string text, string path, int line, int column)
    {
        var link = Assert.Single(ErrorLinks.Find(text));
        Assert.Equal((path, line, column), (link.Path, link.Line, link.Column));
    }

    [Theory]
    [InlineData("Compilação concluída.")]
    [InlineData("http://localhost:5000")]
    [InlineData("são 10:30 agora")]
    public void Texto_comum_nao_vira_link(string text)
    {
        Assert.DoesNotContain(ErrorLinks.Find(text), link => ErrorLinks.Resolve(link.Path, null) is not null);
    }

    [Fact]
    public void Posicao_do_link_aponta_o_trecho_certo()
    {
        var text = "erro em src/a.ts:1:2 aqui";
        var link = Assert.Single(ErrorLinks.Find(text));
        Assert.Equal("src/a.ts:1:2", text.Substring(link.Start, link.Length));
    }

    [Fact]
    public void Resolve_caminho_relativo_a_raiz_e_exige_que_o_arquivo_exista()
    {
        using var temp = new TempFolder();
        var path = temp.File(Path.Combine("src", "a.cs"), "x");
        Assert.Equal(path, ErrorLinks.Resolve(Path.Combine("src", "a.cs"), temp.Path));
        Assert.Null(ErrorLinks.Resolve(Path.Combine("src", "b.cs"), temp.Path));
    }
}

public class RunDetectorTests
{
    [Fact]
    public void Projeto_dotnet_usa_o_executavel_e_ignora_os_testes()
    {
        using var temp = new TempFolder();
        temp.File(Path.Combine("tests", "App.Tests", "App.Tests.csproj"), "<OutputType>Exe</OutputType>");
        temp.File(Path.Combine("src", "App", "App.csproj"), "<OutputType>WinExe</OutputType>");
        Assert.Equal($"dotnet run --project \"{Path.Combine("src", "App", "App.csproj")}\"", RunDetector.Detect(temp.Path, null));
    }

    [Theory]
    [InlineData("{\"scripts\":{\"dev\":\"vite\",\"start\":\"x\"}}", "npm run dev")]
    [InlineData("{\"scripts\":{\"start\":\"x\"}}", "npm run start")]
    public void Projeto_node_usa_os_scripts(string json, string expected)
    {
        using var temp = new TempFolder();
        temp.File("package.json", json);
        Assert.Equal(expected, RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Node_respeita_o_gerenciador_de_pacotes()
    {
        using var temp = new TempFolder();
        temp.File("package.json", "{\"scripts\":{\"dev\":\"x\"}}");
        temp.File("pnpm-lock.yaml", "");
        Assert.Equal("pnpm run dev", RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Package_json_invalido_nao_quebra()
    {
        using var temp = new TempFolder();
        temp.File("package.json", "{ isto não é json");
        Assert.Null(RunDetector.Detect(temp.Path, null));
    }

    [Theory]
    [InlineData("Cargo.toml", "cargo run")]
    [InlineData("go.mod", "go run .")]
    [InlineData("main.py", "python main.py")]
    [InlineData("Makefile", "make")]
    public void Reconhece_outros_projetos(string file, string expected)
    {
        using var temp = new TempFolder();
        temp.File(file, "");
        Assert.Equal(expected, RunDetector.Detect(temp.Path, null));
    }

    [Fact]
    public void Sem_projeto_usa_o_script_aberto()
    {
        using var temp = new TempFolder();
        var script = temp.File("teste.py", "");
        Assert.Equal($"python \"{script}\"", RunDetector.Detect(temp.Path, script));
    }

    [Fact]
    public void Sem_nada_reconhecivel_nao_inventa_comando()
    {
        using var temp = new TempFolder();
        temp.File("notas.txt", "");
        Assert.Null(RunDetector.Detect(temp.Path, Path.Combine(temp.Path, "notas.txt")));
    }
}

// Rodam programas de verdade dentro do pseudoconsole do Windows.
public class ConPtySessionTests
{
    private static (string Output, int Code) Run(string command, string? folder = null, Action<ConPtySession>? act = null)
    {
        var output = new System.Text.StringBuilder();
        using var done = new ManualResetEventSlim();
        var code = -999;
        using var session = ConPtySession.Start(RunDetector.WrapForShell(command), folder ?? Path.GetTempPath(), 80, 24);
        session.Output += text => { lock (output) output.Append(text); };
        session.Exited += exit => { code = exit; done.Set(); };
        act?.Invoke(session);
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "O programa não terminou.");
        lock (output)
            return (output.ToString(), code);
    }

    [Fact]
    public void Captura_a_saida_e_o_codigo_de_saida()
    {
        var (output, code) = Run("echo sirius-funciona");
        Assert.Contains("sirius-funciona", output);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Devolve_o_codigo_de_erro()
    {
        Assert.Equal(3, Run("exit /b 3").Code);
    }

    [Fact]
    public void Roda_na_pasta_pedida()
    {
        using var temp = new TempFolder();
        var marker = Path.GetFileName(temp.Path);
        var buffer = new TerminalBuffer(200, 24);
        buffer.Feed(Run("cd", temp.Path).Output);
        Assert.Contains(marker, buffer.AllText());
    }

    [Fact]
    public void Recebe_o_que_e_digitado()
    {
        var (output, _) = Run("set /p valor=&& call echo digitou:%valor%", act: session => session.Write("abc\r"));
        var buffer = new TerminalBuffer(120, 24);
        buffer.Feed(output);
        Assert.Contains("digitou:abc", buffer.AllText());
    }

    [Fact]
    public void Encerrar_derruba_o_programa()
    {
        var (_, code) = Run("ping -n 60 127.0.0.1", act: session =>
        {
            Thread.Sleep(300);
            session.Kill();
        });
        Assert.NotEqual(0, code);
    }

    [Fact]
    public void Redimensionar_com_o_programa_rodando_nao_quebra()
    {
        var (output, code) = Run("echo ok", act: session => session.Resize(100, 30));
        Assert.Contains("ok", output);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Comando_invalido_de_programa_avisa()
    {
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() =>
            ConPtySession.Start("programa-que-nao-existe-sirius.exe", Path.GetTempPath(), 80, 24));
    }
}
